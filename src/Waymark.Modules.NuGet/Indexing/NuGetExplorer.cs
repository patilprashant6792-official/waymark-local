using System.Text.Json;
using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>
/// The admin UI's view of the symbol index. Searching uses the same tokeniser and the same ranking as nuget_find, so what a person
/// finds here is what Claude finds.
/// </summary>
internal sealed class NuGetExplorer(INuGetIndexStore store) : INuGetExplorer
{
    private const int MaxLimit = 200;
    private const int CandidateMultiplier = 4;
    private const int MaxRelated = 20;
    private const double MinimumRelevance = 0.0001;
    private const string GlobalNamespaceLabel = "(global)";

    public async Task<NuGetExploreResult?> SearchAsync(NuGetExploreQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var package = await ReadyAsync(query.Key, ct).ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }

        var tokens = SymbolText.Tokenize(query.Text).ToList();
        if (!string.IsNullOrWhiteSpace(query.Text) && tokens.Count == 0)
        {
            // Words shorter than two letters are not indexed; browsing everything would look like a match.
            return new NuGetExploreResult(package, [], 0);
        }

        var wanted = Math.Clamp(query.Limit, 1, MaxLimit);
        // The store cannot filter on an empty namespace, so the global namespace is filtered here, among the candidates.
        var global = string.Equals(query.Namespace?.Trim(), GlobalNamespaceLabel, StringComparison.Ordinal);
        var candidates = global ? MaxLimit : Math.Min(MaxLimit, wanted * CandidateMultiplier);
        var result = await store
            .SearchAsync(new NuGetSymbolQuery(package.Id, tokens, query.Kind, global ? null : query.Namespace, query.DeclaringType, candidates), [], ct)
            .ConfigureAwait(false);

        IReadOnlyList<NuGetSymbolHit> hits = result.Hits;
        var total = result.TotalMatches;
        if (global)
        {
            hits = hits.Where(hit => hit.Namespace.Length == 0).ToList();
            total = hits.Count;
        }

        var ranked = tokens.Count == 0
            ? hits
            : hits.OrderByDescending(hit => Math.Max(hit.Relevance, MinimumRelevance) * (1 + (hit.Prominence / 100))).ToList();
        var items = ranked
            .Take(wanted)
            .Select(hit => new NuGetSymbolSummary(hit.Sid, hit.Kind, hit.FullName, hit.Namespace, hit.Card))
            .ToList();
        return new NuGetExploreResult(package, items, total);
    }

    public async Task<NuGetSymbolDetail?> GetSymbolAsync(NuGetPackageKey key, string identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var package = await ReadyAsync(key, ct).ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }

        var id = identifier.Trim();
        var found = QueryParsing.IsSid(id)
            ? await store.GetBySidsAsync(package.Id, [id], NuGetSections.Everything, ct).ConfigureAwait(false)
            : await store.ResolveByNameAsync(package.Id, id, 1, NuGetSections.Everything, ct).ConfigureAwait(false);
        var hit = found.FirstOrDefault();
        if (hit is null)
        {
            return null;
        }

        var related = await ResolveRelatedAsync(package.Id, hit, ct).ConfigureAwait(false);
        var isMethod = hit.Kind == NuGetSymbolKind.MethodGroup;
        return new NuGetSymbolDetail(
            package,
            hit.Sid,
            hit.Kind,
            hit.FullName,
            hit.Namespace,
            hit.Card,
            Lines(hit, NuGetSections.Sig),
            Lines(hit, NuGetSections.Methods),
            Lines(hit, NuGetSections.Props),
            Lines(hit, NuGetSections.Data),
            ParseDocumentation(Section(hit, NuGetSections.Doc)),
            related,
            isMethod ? null : MemberFilter(hit),
            isMethod ? await FindOwnerAsync(package.Id, hit, ct).ConfigureAwait(false) : null);
    }

    private async Task<NuGetPackageIndex?> ReadyAsync(NuGetPackageKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var package = await store.GetPackageAsync(key, ct).ConfigureAwait(false);
        return package is { State: NuGetIndexState.Ready } ? package : null;
    }

    // Method groups store their declaring type without the namespace, so that is the value to filter by.
    private static string MemberFilter(NuGetSymbolHit hit) =>
        hit.Namespace.Length > 0 && hit.FullName.StartsWith(hit.Namespace + ".", StringComparison.Ordinal)
            ? hit.FullName[(hit.Namespace.Length + 1)..]
            : hit.FullName;

    private async Task<NuGetRelatedSymbol?> FindOwnerAsync(long packageRowId, NuGetSymbolHit method, CancellationToken ct)
    {
        var dot = method.FullName.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var typeName = method.FullName[..dot];
        var candidates = await store.ResolveByNameAsync(packageRowId, typeName, 5, [], ct).ConfigureAwait(false);
        var owner = candidates.FirstOrDefault(candidate =>
            candidate.Kind != NuGetSymbolKind.MethodGroup && string.Equals(candidate.FullName, typeName, StringComparison.OrdinalIgnoreCase));
        return owner is null ? null : new NuGetRelatedSymbol("declared in", owner.Sid, owner.FullName);
    }

    // The related section is one 'Name=sid' pair per line.
    private async Task<IReadOnlyList<NuGetRelatedSymbol>> ResolveRelatedAsync(long packageRowId, NuGetSymbolHit hit, CancellationToken ct)
    {
        var pairs = Lines(hit, NuGetSections.Rel)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && QueryParsing.IsSid(parts[1]) && parts[1] != hit.Sid)
            .DistinctBy(parts => parts[1], StringComparer.Ordinal)
            .Take(MaxRelated)
            .ToList();
        if (pairs.Count == 0)
        {
            return [];
        }

        var names = (await store.GetBySidsAsync(packageRowId, pairs.Select(parts => parts[1]).ToList(), [], ct).ConfigureAwait(false))
            .ToDictionary(found => found.Sid, found => found.FullName, StringComparer.Ordinal);
        return pairs
            .Where(parts => names.ContainsKey(parts[1]))
            .Select(parts => new NuGetRelatedSymbol(parts[0], parts[1], names[parts[1]]))
            .ToList();
    }

    private static string Section(NuGetSymbolHit hit, string name) => hit.Sections.GetValueOrDefault(name) ?? string.Empty;

    private static List<string> Lines(NuGetSymbolHit hit, string name) =>
        Section(hit, name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // The doc section is JSON written by SymbolFormatting.DocJson: summary, returns, params and exceptions.
    private static NuGetDocumentation ParseDocumentation(string json)
    {
        var empty = new NuGetDocumentation(string.Empty, string.Empty, [], []);
        if (json.Length == 0)
        {
            return empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new NuGetDocumentation(Text(root, "summary"), Text(root, "returns"), Map(root, "params"), Map(root, "exceptions"));
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static List<KeyValuePair<string, string>> Map(JsonElement root, string name)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            result.AddRange(value.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => new KeyValuePair<string, string>(property.Name, property.Value.GetString() ?? string.Empty)));
        }

        return result;
    }
}
