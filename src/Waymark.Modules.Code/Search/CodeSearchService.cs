using System.Globalization;
using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Search;

/// <summary>
/// code_search. Two legs run over the index and are fused by <see cref="SearchRanker"/>: the name leg (a name pre-filter plus <see cref="SymbolScorer"/>,
/// the first code_search) and the content leg (the full-text index over names, split name words, doc summaries, paths and member body words).
/// </summary>
internal sealed class CodeSearchService(
    IPathGuard guard,
    IWorkspaceStore workspaces,
    ICodeSearchStore store,
    CodeSearchOptions options,
    ToolPipelineOptions pipeline) : ICodeSearchService
{
    private const string AllWorkspaces = "*";
    private const string AttributeSuffix = "Attribute";
    private const int MaxContentFetch = 2000;
    private const int PathGlobFetchFactor = 5;
    private const int MaxCallerNames = 500;

    public async Task<string> SearchAsync(
        string? workspace,
        string? query,
        string? memberType,
        bool caseSensitive,
        int? pageSize,
        int? page,
        IReadOnlyList<string>? terms,
        string? matchIn,
        string? pathGlob,
        bool includeTests,
        CancellationToken ct)
    {
        var parsed = SearchQuery.Parse(query, terms, options.MaxQueryLength, options.MaxTerms);
        if (!SearchScope.TryParse(memberType, out var kind))
        {
            throw new ArgumentException($"Invalid memberType '{memberType}'. Valid values: {string.Join(", ", Enum.GetNames<SearchKind>())}.");
        }

        if (!MatchInParser.TryParse(matchIn, out var mode))
        {
            throw new ArgumentException($"Invalid matchIn '{matchIn}'. Valid values: {string.Join(", ", Enum.GetNames<MatchIn>())}.");
        }

        var notes = new List<string>();
        if (mode == MatchIn.Code && parsed.Phrases.Count > 0)
        {
            parsed = parsed.PhrasesAsRequired();
            notes.Add("Body words are an unordered bag, so in matchIn=Code a quoted phrase means every word of it must occur; their order is not checked.");
        }

        if (pathGlob is not null && pathGlob.AsSpan().IndexOfAny("[]{}") >= 0)
        {
            throw new ArgumentException("pathGlob supports only '*' (inside a folder), '**' (across folders) and '?' (one character); '[ ] { }' are not patterns. Use '?' for one unknown character, e.g. 'src/?id?/**' for a folder named [id].");
        }

        var glob = PathGlob.Parse(pathGlob);
        if (glob is null && !string.IsNullOrWhiteSpace(pathGlob))
        {
            throw new ArgumentException($"pathGlob '{pathGlob.Trim()}' selects nothing; give a folder or a pattern such as 'src/**' or '**/*.cs'.");
        }
        var searched = await ResolveWorkspacesAsync(workspace, ct).ConfigureAwait(false);
        var scope = SearchScope.For(kind);
        var context = new Context(searched.Select(w => w.Id).ToList(), searched.ToDictionary(w => w.Id, w => w.Name), scope, parsed, caseSensitive, glob);

        var candidates = new Dictionary<string, SearchCandidate>(StringComparer.Ordinal);

        var nameCapped = false;
        var contentCapped = false;
        if (mode is MatchIn.All or MatchIn.Names)
        {
            nameCapped = await RunNameLegAsync(context, candidates, ct).ConfigureAwait(false);
        }

        if (mode != MatchIn.Names)
        {
            contentCapped = await RunContentLegAsync(context, mode, candidates, notes, ct).ConfigureAwait(false);
        }

        var kept = candidates.Values.Where(c => glob is null || glob.IsMatch(c.Path)).ToList();
        var hiddenTests = 0;
        if (!includeTests)
        {
            var production = kept.Where(c => !TestPaths.IsTest(c.Path)).ToList();
            hiddenTests = kept.Count - production.Count;
            kept = production;
        }

        var callers = mode == MatchIn.Names
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await CountCallersAsync(context, kept, ct).ConfigureAwait(false);
        var ranked = SearchRanker.Rank(kept, parsed, options.RrfK, callers, legacyOrder: mode == MatchIn.Names);

        return Render(parsed, mode, searched, ranked, nameCapped, contentCapped, notes, hiddenTests, pageSize, page);
    }

    private async Task<bool> RunNameLegAsync(Context context, Dictionary<string, SearchCandidate> candidates, CancellationToken ct)
    {
        var result = await store.FindSymbolsAsync(
            new CodeSymbolQuery(
                context.WorkspaceIds,
                context.Query.Words,
                context.CaseSensitive,
                context.Scope.TypeKinds,
                context.Scope.MemberKinds,
                context.Scope.IncludeAttributes,
                context.Scope.IncludeBaseList,
                options.MaxCandidates),
            ct).ConfigureAwait(false);

        var hits = result.Rows
            .SelectMany(row => HitsOf(row, context.WorkspaceNames[row.WorkspaceId], context.Scope, context.Query.Words, context.CaseSensitive))
            .ToList();

        // A characters-in-order match is noise as soon as a real match exists.
        if (hits.Any(h => h.Score >= SymbolScorer.FuzzyCeiling))
        {
            hits = hits.Where(h => h.Score >= SymbolScorer.FuzzyCeiling).ToList();
        }

        var rank = 0;
        foreach (var hit in hits.OrderByDescending(h => h.Score).ThenBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.Line))
        {
            rank++;
            var candidate = new SearchCandidate(
                KeyOf(hit.Workspace, hit.Path, hit.Line, hit.Name, hit.Label),
                hit.Name,
                hit.Label,
                hit.Workspace,
                hit.Path,
                hit.Line,
                hit.Detail,
                hit.Text,
                hit.IsType,
                hit.Score,
                rank,
                null,
                string.Empty,
                hit.Context);
            candidates.TryAdd(candidate.Key, candidate);
        }

        return result.Capped;
    }

    private async Task<bool> RunContentLegAsync(
        Context context,
        MatchIn mode,
        Dictionary<string, SearchCandidate> candidates,
        List<string> notes,
        CancellationToken ct)
    {
        var columns = FtsQuery.ColumnsFor(mode);
        var all = FtsQuery.Build(context.Query.Words, columns, all: true);
        if (all is null)
        {
            return false;
        }

        var any = FtsQuery.Build(context.Query.Words.Concat(context.Query.Terms), columns, all: false);
        var limit = Math.Min(options.ContentCandidates * (context.Glob is null ? 1 : PathGlobFetchFactor), MaxContentFetch);
        try
        {
            var first = await store.FindByContentAsync(
                new CodeContentQuery(context.WorkspaceIds, all, context.Scope.TypeKinds, context.Scope.MemberKinds, limit),
                ct).ConfigureAwait(false);
            var capped = first.Capped;
            var ordered = new List<CodeContentHit>(first.Hits);
            if (any is not null && !string.Equals(any, all, StringComparison.Ordinal))
            {
                var second = await store.FindByContentAsync(
                    new CodeContentQuery(context.WorkspaceIds, any, context.Scope.TypeKinds, context.Scope.MemberKinds, limit),
                    ct).ConfigureAwait(false);
                capped |= second.Capped;
                var known = ordered.Select(h => HitKey(context, h.Row)).ToHashSet(StringComparer.Ordinal);
                ordered.AddRange(second.Hits.Where(h => !known.Contains(HitKey(context, h.Row))));
            }

            var rank = 0;
            foreach (var hit in ordered)
            {
                rank++;
                var workspaceName = context.WorkspaceNames[hit.Row.WorkspaceId];
                var key = HitKey(context, hit.Row);
                candidates[key] = candidates.TryGetValue(key, out var existing)
                    ? existing with { ContentRank = rank, Terms = hit.Terms }
                    : new SearchCandidate(
                        key,
                        hit.Row.Name,
                        hit.Row.Kind,
                        workspaceName,
                        hit.Row.RelativePath,
                        hit.Row.Line,
                        DescribeSymbol(hit.Row),
                        CoreOf(hit.Row),
                        hit.Row.IsType,
                        0,
                        null,
                        rank,
                        hit.Terms,
                        ContextOf(hit.Row));
            }

            return capped;
        }
        catch (ArgumentException ex)
        {
            notes.Add($"The content search was skipped ({ex.Message}); only names were searched.");
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> CountCallersAsync(Context context, List<SearchCandidate> candidates, CancellationToken ct)
    {
        var names = candidates
            .Where(c => c.Label is "method" or "function" or "constructor")
            .OrderBy(c => Math.Min(c.NameRank ?? int.MaxValue, c.ContentRank ?? int.MaxValue))
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Min(options.CallerBoostTop, MaxCallerNames))
            .ToList();
        return names.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await store.CountCallersAsync(context.WorkspaceIds, names, ct).ConfigureAwait(false);
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
        var text = CoreOf(row);
        var context = ContextOf(row);
        var inKindSet = row.IsType
            ? scope.TypeKinds.Contains(Enum.Parse<CodeTypeKind>(row.Kind, ignoreCase: true))
            : scope.MemberKinds.Contains(Enum.Parse<CodeMemberKind>(row.Kind, ignoreCase: true));
        if (inKindSet)
        {
            var score = SymbolScorer.Score(row.Name, tokens, caseSensitive);
            if (score > 0)
            {
                yield return new Hit(score, row.Name, row.Kind, workspace, row.RelativePath, row.Line, DescribeSymbol(row), row.IsType, text, context);
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
                    yield return new Hit(score, name, "attribute", workspace, row.RelativePath, row.Line, $"On {(row.IsType ? "type " : string.Empty)}{Qualified(row)}", false, name + FieldBreak + text, context);
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
                    yield return new Hit(score, baseType, "base type", workspace, row.RelativePath, row.Line, $"Listed in the base list of {row.Name}", false, baseType + FieldBreak + text, context);
                }
            }
        }
    }

    private static string KeyOf(string workspace, string path, int line, string name, string label) =>
        string.Create(CultureInfo.InvariantCulture, $"{workspace}|{path}|{line}|{name}|{label}");

    private static string HitKey(Context context, CodeSymbolRow row) =>
        KeyOf(context.WorkspaceNames[row.WorkspaceId], row.RelativePath, row.Line, row.Name, row.Kind);

    // A symbol's own words are its name, signature and doc summary; the owner and the path are its context. U+0002 separates the fields so a phrase never runs across two.
    private const char FieldBreak = '\u0002';

    private static string CoreOf(CodeSymbolRow row) =>
        string.Join(FieldBreak, new[] { row.Name, row.Signature, row.Doc }.Where(part => !string.IsNullOrEmpty(part)));

    private static string ContextOf(CodeSymbolRow row) =>
        string.Join(FieldBreak, new[] { row.Owner, row.RelativePath }.Where(part => !string.IsNullOrEmpty(part)));

    private static string Qualified(CodeSymbolRow row) => row.Owner is null ? row.Name : row.Owner + "." + row.Name;

    private static string DescribeSymbol(CodeSymbolRow row)
    {
        if (!row.IsType)
        {
            var owner = row.Owner is null ? string.Empty : $"{row.Owner}: ";

            // A local member has no type; say so, because its name may also be a module-level function or a local of another component.
            var local = row.Owner is null && row.Modifiers is not null && row.Modifiers.Split(' ').Contains(LocalMembers.Modifier, StringComparer.Ordinal) ? "local member: " : string.Empty;
            return owner + local + (row.Signature ?? row.Name);
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
        SearchQuery query,
        MatchIn mode,
        IReadOnlyList<Workspace> searched,
        IReadOnlyList<RankedHit> hits,
        bool nameCapped,
        bool contentCapped,
        IReadOnlyList<string> notes,
        int hiddenTests,
        int? requestedPageSize,
        int? requestedPage)
    {
        var size = Math.Clamp(requestedPageSize ?? options.DefaultPageSize, 1, options.MaxPageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(hits.Count / (double)size));
        var page = Math.Clamp(requestedPage ?? 1, 1, pages);
        var showWorkspace = searched.Count > 1;

        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine(query.Display.Contains('"', StringComparison.Ordinal) ? $"# Code search: `{query.Display}`" : $"# Code search: \"{query.Display}\"");
        output.TryAppendLine();
        output.TryAppendLine(
            string.Create(CultureInfo.InvariantCulture, $"**Workspaces searched:** {string.Join(", ", searched.Select(w => w.Name))} | **Matches:** {hits.Count}{(nameCapped || contentCapped ? "+" : string.Empty)} | **Page:** {page} of {pages} ({size} per page)"));
        output.TryAppendLine();
        output.TryAppendLine(mode switch
        {
            MatchIn.Names => "_Names are matched in the syntax index (no symbol resolution), best match first._",
            MatchIn.Docs => "_Doc comments are matched in the syntax index (no symbol resolution), best match first._",
            MatchIn.Code => "_The words of member bodies are matched in the syntax index (no symbol resolution), best match first._",
            _ => "_Names, doc comments, paths and the words of member bodies are matched in the syntax index (no symbol resolution), best match first._"
        });
        foreach (var notIndexed in searched.Where(w => w.LastIndexedUtc is null))
        {
            output.TryAppendLine($"_Workspace '{notIndexed.Name}' has not finished its first index; results may be incomplete._");
        }

        if (nameCapped)
        {
            output.TryAppendLine($"_The index returned more than {options.MaxCandidates} candidates; use a longer or more specific query to be sure nothing was left out._");
        }

        if (contentCapped)
        {
            output.TryAppendLine($"_The full-text index found more than {options.ContentCandidates} candidates and ranked only the best; add words, +required words or a pathGlob to narrow it._");
        }

        foreach (var note in notes)
        {
            output.TryAppendLine($"_{note}_");
        }

        output.TryAppendLine();
        if (hits.Count == 0)
        {
            output.TryAppendLine("No matches.");
            if (mode != MatchIn.Names)
            {
                output.TryAppendLine("Try fewer or different words, or pass terms with synonyms or other spellings.");
            }

            AppendHiddenTests(output, hiddenTests);
            return output.Build().TrimEnd();
        }

        var number = (page - 1) * size;
        foreach (var ranked in hits.Skip(number).Take(size))
        {
            number++;
            var hit = ranked.Candidate;
            var where = showWorkspace ? $"{hit.Workspace}: {hit.Path}:{hit.Line}" : $"{hit.Path}:{hit.Line}";
            output.TryAppendLine($"{number}. **{hit.Name}** ({hit.Label}) `{where}`");
            output.TryAppendLine($"   {hit.Detail}");
            if (!query.IsIdentifier && ranked.Matched.Count > 0)
            {
                output.TryAppendLine($"   matched: {string.Join(", ", ranked.Matched)}");
            }
        }

        if (page < pages)
        {
            output.TryAppendLine();
            output.TryAppendLine($"More results: call again with page={page + 1}.");
        }

        AppendHiddenTests(output, hiddenTests);
        return output.Build().TrimEnd();
    }

    private static void AppendHiddenTests(OutputBudget output, int hiddenTests)
    {
        if (hiddenTests > 0)
        {
            output.TryAppendLine();
            output.TryAppendLine($"_{hiddenTests} match(es) in test folders were left out; pass includeTests=true to see them._");
        }
    }

    private sealed record Context(
        IReadOnlyList<long> WorkspaceIds,
        IReadOnlyDictionary<long, string> WorkspaceNames,
        SearchScope Scope,
        SearchQuery Query,
        bool CaseSensitive,
        PathGlob? Glob);

    private sealed record Hit(double Score, string Name, string Label, string Workspace, string Path, int Line, string Detail, bool IsType, string Text, string Context);
}
