using System.Globalization;
using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Search;

internal sealed class CodeCallersService(
    IPathGuard guard,
    ICodeIndexStore index,
    ICodeSearchStore store,
    LanguageIndexers languages,
    CodeSearchOptions options,
    ToolPipelineOptions pipeline) : ICodeCallersService
{
    private const int MaxListedClasses = 20;
    private const int MaxListedExclusions = 6;
    private const int MaxGuessedNames = 400;
    private const string ExtensionMarker = "(this ";

    private sealed record View(
        string Workspace,
        string Path,
        CodeTypeEntry? Owner,
        List<CodeMemberEntry> Overloads,
        IReadOnlyList<ClassifiedSite> Shown,
        IReadOnlyList<KeyValuePair<string, int>> Excluded,
        int ExcludedCount,
        string? FilterOff,
        int InTests,
        bool Capped,
        bool IncludeTests,
        bool IncludeUnrelated);

    public async Task<string> CallersAsync(
        string? workspace,
        string? relativeFilePath,
        string? methodName,
        string? className,
        bool includeTests,
        bool includeUnrelated,
        int? pageSize,
        int? page,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("methodName is required.");
        }

        var target = await guard.ResolveAsync(workspace, relativeFilePath, ct).ConfigureAwait(false);
        if (!languages.Handles(target.RelativePath))
        {
            throw new ArgumentException($"'{target.RelativePath}' is not a source file the code tools can analyze ({languages.Describe()}).");
        }

        var file = await index.GetFileAsync(target.Workspace.Id, target.RelativePath, ct).ConfigureAwait(false)
            ?? throw new ArgumentException(
                $"'{target.RelativePath}' is not in the code index (it may still be indexing, or it is excluded). Use code_method to check that the member exists.");

        var (owner, overloads) = FindTarget(file, methodName.Trim(), className);
        var name = overloads[0].Name;
        var sites = await store.FindCallSitesAsync(target.Workspace.Id, name, options.MaxCallSites, ct).ConfigureAwait(false);
        var inTests = sites.Rows.Count(s => TestPaths.IsTest(s.RelativePath));
        var visible = includeTests ? sites.Rows : [.. sites.Rows.Where(s => !TestPaths.IsTest(s.RelativePath))];

        var (classified, filterOff) = await ClassifyAsync(target.Workspace.Id, owner, overloads, visible, ct).ConfigureAwait(false);
        var shown = includeUnrelated ? classified : [.. classified.Where(s => s.Match != SiteMatch.Unrelated)];
        var excluded = includeUnrelated ? [] : ReceiverFilter.CountUnrelated(classified);
        var excludedCount = classified.Count - shown.Count;

        var view = new View(
            target.Workspace.Name,
            target.RelativePath,
            owner,
            overloads,
            shown,
            excluded,
            excludedCount,
            filterOff,
            includeTests ? 0 : inTests,
            sites.Capped,
            includeTests,
            includeUnrelated);
        return Render(view, OutgoingCalls(file, owner, name), pageSize, page);
    }

    private static (CodeTypeEntry? Owner, List<CodeMemberEntry> Overloads) FindTarget(CodeFileIndex file, string methodName, string? className)
    {
        var named = file.Members
            .Where(m => m.Kind is CodeMemberKind.Method or CodeMemberKind.Constructor or CodeMemberKind.Function
                && (string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)
                    || (m.Kind == CodeMemberKind.Constructor && string.Equals(methodName, "__init__", StringComparison.Ordinal))))
            .ToList();
        if (named.Count == 0)
        {
            throw new ArgumentException($"Method '{methodName}' not found in {file.Stamp.RelativePath}. Use code_outline to list its members.");
        }

        var wanted = string.IsNullOrWhiteSpace(className) ? null : className.Trim();
        var groups = named
            .GroupBy(m => m.TypeIndex)
            .Where(g => wanted is null || (g.Key is { } i && TypeMatches(file.Types[i], wanted)))
            .ToList();
        if (groups.Count == 0)
        {
            throw new ArgumentException($"Class '{wanted}' has no method '{methodName}' in {file.Stamp.RelativePath}.");
        }

        if (groups.Count > 1)
        {
            var classes = groups.Select(g => g.Key is { } i ? file.Types[i].Name : "(no type)").Take(MaxListedClasses);
            throw new ArgumentException($"Several classes have '{methodName}': {string.Join(", ", classes)}. Specify className.");
        }

        var group = groups[0];
        return (group.Key is { } index ? file.Types[index] : null, [.. group]);
    }

    private static bool TypeMatches(CodeTypeEntry type, string wanted) =>
        string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type.FullName, wanted, StringComparison.OrdinalIgnoreCase);

    // The index stores a call's caller as type and member name, so the calls made by the target are the sites in its own file with that pair.
    private static List<CodeCallSiteEntry> OutgoingCalls(CodeFileIndex file, CodeTypeEntry? owner, string name) =>
        file.CallSites
            .Where(s => string.Equals(s.CallerMember, name, StringComparison.Ordinal)
                && (owner is null
                    ? s.CallerType is null
                    : string.Equals(s.CallerType, owner.Name, StringComparison.Ordinal)
                        || string.Equals(s.CallerType, owner.FullName, StringComparison.Ordinal)))
            .OrderBy(s => s.Line)
            .ToList();

    /// <summary>
    /// Sorts the calls by whether their receiver can be the method's type. The filter is off (every call is matched by
    /// name, and the reason is returned) where a receiver type says nothing: constructors, extension methods, a method
    /// outside any type, or a type hierarchy too large to read completely.
    /// </summary>
    private async Task<(IReadOnlyList<ClassifiedSite> Sites, string? FilterOff)> ClassifyAsync(
        long workspaceId,
        CodeTypeEntry? owner,
        List<CodeMemberEntry> overloads,
        IReadOnlyList<CodeCallSiteRow> visible,
        CancellationToken ct)
    {
        var reason = FilterOffReason(owner, overloads);
        if (reason is not null)
        {
            return (ReceiverFilter.Unfiltered(visible), reason);
        }

        var relations = await store.FindTypeRelationsAsync(workspaceId, options.MaxTypeRelations, ct).ConfigureAwait(false);
        if (relations.Capped)
        {
            return (ReceiverFilter.Unfiltered(visible), "the type hierarchy is too large to read completely");
        }

        var related = new TypeHierarchy(relations.Rows).RelatedNames(owner!.Name);
        if (related is null)
        {
            return (ReceiverFilter.Unfiltered(visible), "the type hierarchy around this type is too deep or wide to read completely");
        }

        var guessed = visible
            .Where(s => s.ReceiverTypeGuessed && s.ReceiverType is not null)
            .Select(s => s.ReceiverType!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxGuessedNames)
            .ToList();
        var valueNames = await store.FindDeclaredValueNamesAsync(workspaceId, guessed, ct).ConfigureAwait(false);
        return (ReceiverFilter.Classify(visible, related, valueNames), null);
    }

    private static string? FilterOffReason(CodeTypeEntry? owner, List<CodeMemberEntry> overloads)
    {
        if (owner is null)
        {
            return "it is not inside a type";
        }

        if (overloads.Any(o => o.Kind == CodeMemberKind.Constructor))
        {
            return "constructors are matched by name only";
        }

        return overloads.Any(o => o.Signature.Contains(ExtensionMarker, StringComparison.Ordinal))
            ? "extension methods are matched by name only"
            : null;
    }

    private string Render(View view, List<CodeCallSiteEntry> calls, int? requestedPageSize, int? requestedPage)
    {
        var size = Math.Clamp(requestedPageSize ?? options.DefaultPageSize, 1, options.MaxPageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(view.Shown.Count / (double)size));
        var page = Math.Clamp(requestedPage ?? 1, 1, pages);
        var qualified = view.Owner is null ? view.Overloads[0].Name : $"{view.Owner.Name}.{view.Overloads[0].Name}";
        var pageItems = view.Shown.Skip((page - 1) * size).Take(size).ToList();

        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine($"# Callers of `{qualified}`");
        output.TryAppendLine();
        output.TryAppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"**Workspace:** {view.Workspace} | **Defined in:** `{view.Path}:{view.Overloads[0].StartLine}` | **Overloads:** {view.Overloads.Count} | **Callers:** {CallerCounts(view)} | **Page:** {page} of {pages} ({size} per page)"));
        output.TryAppendLine();
        foreach (var note in Notes(view))
        {
            output.TryAppendLine(note);
        }

        output.TryAppendLine();
        output.TryAppendLine("## Called by");
        output.TryAppendLine();
        if (view.Shown.Count == 0)
        {
            output.TryAppendLine("No callers found.");
        }

        foreach (var caller in pageItems)
        {
            if (!output.TryAppendLine(CallerLine(caller)))
            {
                break;
            }
        }

        if (page < pages)
        {
            output.TryAppendLine();
            output.TryAppendLine($"More callers: call again with page={page + 1}.");
        }

        output.TryAppendLine();
        output.TryAppendLine("## Calls made by this method");
        output.TryAppendLine();
        if (calls.Count == 0)
        {
            output.TryAppendLine("None found.");
        }

        foreach (var call in calls)
        {
            var receiver = string.IsNullOrEmpty(call.Receiver) ? string.Empty : call.Receiver + ".";
            output.TryAppendLine($"- line {call.Line}: `{receiver}{call.CalleeName}`");
        }

        return output.Build().TrimEnd();
    }

    private static string CallerCounts(View view)
    {
        if (view.FilterOff is not null)
        {
            return view.Shown.Count.ToString(CultureInfo.InvariantCulture);
        }

        var typed = view.Shown.Count(s => s.Match == SiteMatch.Typed);
        var nameOnly = view.Shown.Count(s => s.Match == SiteMatch.NameOnly);
        var unrelated = view.Shown.Count(s => s.Match == SiteMatch.Unrelated);
        var parts = new List<string> { $"{typed} typed", $"{nameOnly} name-only" };
        if (unrelated > 0)
        {
            parts.Add($"{unrelated} unrelated");
        }

        return $"{view.Shown.Count} ({string.Join(", ", parts)})";
    }

    private IEnumerable<string> Notes(View view)
    {
        if (view.FilterOff is not null)
        {
            yield return $"_Matched by method name only ({view.FilterOff}): a same-named method on another type is listed too, and so are all overloads. Use the receiver to tell them apart._";
        }
        else
        {
            yield return "_Calls are matched by name, then sorted by the declared type of their receiver: `?` marks a receiver whose type is unknown (kept, but may belong to another type). All overloads share one result._";
        }

        if (view.ExcludedCount > 0)
        {
            var top = string.Join(", ", view.Excluded.Take(MaxListedExclusions).Select(p => $"{p.Key} x{p.Value}"));
            var more = view.Excluded.Count > MaxListedExclusions ? ", ..." : string.Empty;
            yield return $"_Left out {view.ExcludedCount} calls whose receiver is declared as an unrelated type: {top}{more}. Set includeUnrelated=true to list them._";
        }

        if (view.InTests > 0)
        {
            yield return $"_{view.InTests} calls in test folders are not listed (includeTests=true shows them)._";
        }

        if (view.Capped)
        {
            yield return $"_The index holds more than {options.MaxCallSites} calls to this name; only the first {options.MaxCallSites} (by path) were examined._";
        }
    }

    private static string CallerLine(ClassifiedSite caller)
    {
        var site = caller.Site;
        var from = string.Join('.', new[] { site.CallerType, site.CallerMember }.Where(p => !string.IsNullOrEmpty(p)));
        var receiver = string.IsNullOrEmpty(site.Receiver) ? string.Empty : $" via `{site.Receiver}`";
        var mark = caller.Match switch
        {
            SiteMatch.NameOnly => " ?",
            SiteMatch.Unrelated => $" [on {site.ReceiverType}]",
            _ => string.Empty,
        };
        return $"- `{site.RelativePath}:{site.Line}` {(from.Length == 0 ? "(top-level code)" : "`" + from + "`")}{receiver}{mark}";
    }
}
