using System.Globalization;
using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Search;

internal sealed class CodeSearchService(
    IPathGuard guard,
    IWorkspaceStore workspaces,
    ICodeSearchStore store,
    CodeSearchOptions options,
    ToolPipelineOptions pipeline) : ICodeSearchService
{
    private const string AllWorkspaces = "*";
    private const string AttributeSuffix = "Attribute";

    public async Task<string> SearchAsync(string? workspace, string? query, string? memberType, bool caseSensitive, int? pageSize, int? page, CancellationToken ct)
    {
        var tokens = Tokenize(query);
        if (!SearchScope.TryParse(memberType, out var kind))
        {
            throw new ArgumentException($"Invalid memberType '{memberType}'. Valid values: {string.Join(", ", Enum.GetNames<SearchKind>())}.");
        }

        var searched = await ResolveWorkspacesAsync(workspace, ct).ConfigureAwait(false);
        var scope = SearchScope.For(kind);
        var result = await store.FindSymbolsAsync(
            new CodeSymbolQuery(
                searched.Select(w => w.Id).ToList(),
                tokens,
                caseSensitive,
                scope.TypeKinds,
                scope.MemberKinds,
                scope.IncludeAttributes,
                scope.IncludeBaseList,
                options.MaxCandidates),
            ct).ConfigureAwait(false);

        var names = searched.ToDictionary(w => w.Id, w => w.Name);
        var hits = result.Rows
            .SelectMany(row => HitsOf(row, names[row.WorkspaceId], scope, tokens, caseSensitive))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ToList();

        return Render(string.Join(' ', tokens), searched, hits, result.Capped, pageSize, page);
    }

    private List<string> Tokenize(string? query)
    {
        var tokens = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("query is required.");
        }

        var joined = string.Join(' ', tokens);
        return joined.Length > options.MaxQueryLength
            ? throw new ArgumentException($"query is longer than {options.MaxQueryLength} characters.")
            : [.. tokens];
    }

    private async Task<IReadOnlyList<Workspace>> ResolveWorkspacesAsync(string? workspace, CancellationToken ct)
    {
        if (!string.Equals(workspace?.Trim(), AllWorkspaces, StringComparison.Ordinal))
        {
            return [await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false)];
        }

        var enabled = (await workspaces.ListAsync(ct).ConfigureAwait(false)).Where(w => w.Enabled).ToList();
        return enabled.Count > 0 ? enabled : throw new ArgumentException("No enabled workspace exists. Add one in Waymark first.");
    }

    private static IEnumerable<Hit> HitsOf(CodeSymbolRow row, string workspace, SearchScope scope, IReadOnlyList<string> tokens, bool caseSensitive)
    {
        var inKindSet = row.IsType
            ? scope.TypeKinds.Contains(Enum.Parse<CodeTypeKind>(row.Kind, ignoreCase: true))
            : scope.MemberKinds.Contains(Enum.Parse<CodeMemberKind>(row.Kind, ignoreCase: true));
        if (inKindSet)
        {
            var score = SymbolScorer.Score(row.Name, tokens, caseSensitive);
            if (score > 0)
            {
                yield return new Hit(score, row.Name, row.Kind, workspace, row.RelativePath, row.Line, DescribeSymbol(row));
            }
        }

        if (scope.IncludeAttributes && !string.IsNullOrEmpty(row.Attributes))
        {
            foreach (var attribute in row.Attributes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = AttributeName(attribute);
                var score = SymbolScorer.Score(name, tokens, caseSensitive);
                if (score > 0)
                {
                    yield return new Hit(score, name, "attribute", workspace, row.RelativePath, row.Line, $"On {(row.IsType ? "type " : string.Empty)}{Qualified(row)}");
                }
            }
        }

        if (scope.IncludeBaseList && row.IsType && !string.IsNullOrEmpty(row.BaseList))
        {
            foreach (var baseType in BaseListParser.SimpleNames(row.BaseList))
            {
                var score = SymbolScorer.Score(baseType, tokens, caseSensitive);
                if (score > 0)
                {
                    yield return new Hit(score, baseType, "base type", workspace, row.RelativePath, row.Line, $"Listed in the base list of {row.Name}");
                }
            }
        }
    }

    private static string Qualified(CodeSymbolRow row) => row.Owner is null ? row.Name : row.Owner + "." + row.Name;

    private static string DescribeSymbol(CodeSymbolRow row)
    {
        if (!row.IsType)
        {
            var owner = row.Owner is null ? string.Empty : $"{row.Owner}: ";
            return owner + (row.Signature ?? row.Name);
        }

        var modifiers = string.IsNullOrEmpty(row.Modifiers) ? string.Empty : row.Modifiers + " ";
        var bases = string.IsNullOrEmpty(row.BaseList) ? string.Empty : " : " + row.BaseList;
        return $"{modifiers}{row.Kind} {row.Name}{bases}";
    }

    // Attribute text is stored without brackets, e.g. Route("api/[controller]"): keep the name only and drop a namespace and the Attribute suffix.
    private static string AttributeName(string attribute)
    {
        var open = attribute.IndexOf('(', StringComparison.Ordinal);
        var name = (open < 0 ? attribute : attribute[..open]).Trim();
        name = name[(name.LastIndexOf('.') + 1)..];
        return name.Length > AttributeSuffix.Length && name.EndsWith(AttributeSuffix, StringComparison.Ordinal)
            ? name[..^AttributeSuffix.Length]
            : name;
    }

    private string Render(
        string query,
        IReadOnlyList<Workspace> searched,
        List<Hit> hits,
        bool capped,
        int? requestedPageSize,
        int? requestedPage)
    {
        var size = Math.Clamp(requestedPageSize ?? options.DefaultPageSize, 1, options.MaxPageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(hits.Count / (double)size));
        var page = Math.Clamp(requestedPage ?? 1, 1, pages);
        var showWorkspace = searched.Count > 1;

        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine($"# Code search: \"{query}\"");
        output.TryAppendLine();
        output.TryAppendLine(
            string.Create(CultureInfo.InvariantCulture, $"**Workspaces searched:** {string.Join(", ", searched.Select(w => w.Name))} | **Matches:** {hits.Count} | **Page:** {page} of {pages} ({size} per page)"));
        output.TryAppendLine();
        output.TryAppendLine("_Names are matched in the syntax index (no symbol resolution), best match first._");
        foreach (var notIndexed in searched.Where(w => w.LastIndexedUtc is null))
        {
            output.TryAppendLine($"_Workspace '{notIndexed.Name}' has not finished its first index; results may be incomplete._");
        }

        if (capped)
        {
            output.TryAppendLine($"_The index returned more than {options.MaxCandidates} candidates; use a longer or more specific query to be sure nothing was left out._");
        }

        output.TryAppendLine();
        if (hits.Count == 0)
        {
            output.TryAppendLine("No matches.");
            return output.Build().TrimEnd();
        }

        var number = (page - 1) * size;
        foreach (var hit in hits.Skip(number).Take(size))
        {
            number++;
            var where = showWorkspace ? $"{hit.Workspace}: {hit.Path}:{hit.Line}" : $"{hit.Path}:{hit.Line}";
            output.TryAppendLine($"{number}. **{hit.Name}** ({hit.Label}) `{where}`");
            output.TryAppendLine($"   {hit.Detail}");
        }

        if (page < pages)
        {
            output.TryAppendLine();
            output.TryAppendLine($"More results: call again with page={page + 1}.");
        }

        return output.Build().TrimEnd();
    }

    private sealed record Hit(double Score, string Name, string Label, string Workspace, string Path, int Line, string Detail);
}
