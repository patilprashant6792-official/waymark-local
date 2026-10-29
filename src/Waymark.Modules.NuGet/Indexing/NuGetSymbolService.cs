using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Loading;
using Waymark.Modules.NuGet.Npm;
using Waymark.Modules.NuGet.Pypi;

namespace Waymark.Modules.NuGet.Indexing;

internal sealed class NuGetSymbolService(
    IPackageIndexer indexer,
    INuGetIndexStore store,
    PackageFeed feed,
    PypiPackageSearch pypiSearch,
    NpmPackageSearch npmSearch,
    NuGetIndexOptions options,
    TimeProvider time) : INuGetSymbolService
{
    private const int DefaultPackageResults = 5;
    private const int MaxCandidates = 200;
    private const int NamePartsLimit = 2;
    private const double MinimumRelevance = 0.0001;

    public async Task<string> PackagesAsync(string ecosystem, string query, int? take, bool includePrerelease, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var count = Math.Clamp(take ?? DefaultPackageResults, 1, options.MaxPackageResults);
        var kind = PackageEcosystems.Parse(ecosystem);
        IReadOnlyList<PackageSearchHit> hits;
        try
        {
            hits = kind switch
            {
                PackageEcosystems.PyPI => await pypiSearch.SearchAsync(query.Trim(), count, cancellationToken).ConfigureAwait(false),
                PackageEcosystems.Npm => await npmSearch.SearchAsync(query.Trim(), count, cancellationToken).ConfigureAwait(false),
                _ => await feed.SearchPackagesAsync(query.Trim(), count, includePrerelease, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            throw new NuGetLoadException(ex.Message, ex);
        }

        return SymbolMarkdown.Packages(kind, query.Trim(), hits, options.MaxPackagesOutputChars);
    }

    public async Task<string> OutlineAsync(PackageIndexRequest package, string? namespacePrefix, bool refresh, CancellationToken cancellationToken)
    {
        var index = await indexer.EnsureIndexedAsync(package, refresh, cancellationToken).ConfigureAwait(false);
        var outline = await store.GetOutlineAsync(index.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new NuGetLoadException($"The index of '{index.Key.PackageId}' {index.Key.Version} is not available. Try `pkg_outline` with refresh=true.", index.Key.PackageId, index.Key.Version);

        var prefix = namespacePrefix?.Trim();
        var matching = string.IsNullOrEmpty(prefix)
            ? outline.Namespaces
            : outline.Namespaces.Where(n => n.Namespace.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = matching.Take(options.MaxOutlineNamespaces).ToList();
        var age = time.GetUtcNow() - (index.IndexedUtc ?? index.StartedUtc);
        return SymbolMarkdown.Outline(outline, shown, matching.Count, prefix, age, options.MaxOutlineOutputChars);
    }

    public async Task<string> FindAsync(
        PackageIndexRequest package,
        string? query,
        string? kind,
        string? namespaceName,
        string? declaringType,
        int? limit,
        CancellationToken cancellationToken)
    {
        var parsedKind = QueryParsing.ParseKind(kind);
        var wanted = Math.Clamp(limit ?? options.DefaultFindResults, 1, options.MaxFindResults);
        var index = await indexer.EnsureIndexedAsync(package, false, cancellationToken).ConfigureAwait(false);

        var tokens = SymbolText.Tokenize(query).ToList();
        var candidates = Math.Min(MaxCandidates, wanted * options.CandidateMultiplier);
        var result = await store.SearchAsync(
            new NuGetSymbolQuery(index.Id, tokens, parsedKind, namespaceName, declaringType, candidates),
            [],
            cancellationToken).ConfigureAwait(false);

        var ranked = tokens.Count == 0
            ? result.Hits.Take(wanted).ToList()
            : result.Hits.OrderByDescending(h => Math.Max(h.Relevance, MinimumRelevance) * (1 + (h.Prominence / 100))).Take(wanted).ToList();
        return SymbolMarkdown.Find(index.Key, ranked, result.TotalMatches, options.MaxFindOutputChars);
    }

    public async Task<string> ExpandAsync(
        PackageIndexRequest package, IReadOnlyList<string> identifiers, string? sections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        var ids = identifiers.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Pass at least one symbol id (from `pkg_find`) or a type name.", nameof(identifiers));
        }

        if (ids.Count > options.MaxExpandSymbols)
        {
            throw new ArgumentException($"Expand at most {options.MaxExpandSymbols} symbols per call; split the list.", nameof(identifiers));
        }

        var requested = QueryParsing.ParseSections(sections);
        var index = await indexer.EnsureIndexedAsync(package, false, cancellationToken).ConfigureAwait(false);

        var sids = ids.Where(QueryParsing.IsSid).ToList();
        var bySid = sids.Count == 0
            ? new Dictionary<string, NuGetSymbolHit>(StringComparer.Ordinal)
            : (await store.GetBySidsAsync(index.Id, sids, NuGetSections.Everything, cancellationToken).ConfigureAwait(false))
                .ToDictionary(h => h.Sid, StringComparer.Ordinal);

        var items = new List<ExpandItem>(ids.Count);
        foreach (var id in ids)
        {
            var hit = QueryParsing.IsSid(id)
                ? bySid.GetValueOrDefault(id)
                : (await store.ResolveByNameAsync(index.Id, id, NamePartsLimit, NuGetSections.Everything, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            items.Add(new ExpandItem(id, hit, requested ?? QueryParsing.DefaultSections(hit?.Kind ?? NuGetSymbolKind.Class)));
        }

        return SymbolMarkdown.Expand(index.Key, items, options.MaxExpandOutputChars);
    }
}
