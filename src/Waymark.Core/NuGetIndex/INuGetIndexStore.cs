namespace Waymark.Core.NuGetIndex;

/// <summary>
/// The symbol index of NuGet packages. A package is built in steps (begin, add batches, complete) and is invisible to
/// readers until it is complete, so a crash or a failure never leaves a half-indexed package searchable.
/// </summary>
public interface INuGetIndexStore
{
    /// <summary>
    /// Starts a build and returns the package row id. Any earlier version of the same key (ready or failed) is dropped first.
    /// Returns null when another build of the key started less than <paramref name="staleAfter"/> ago.
    /// </summary>
    Task<long?> TryBeginBuildAsync(NuGetPackageKey key, int schemaVersion, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct);

    /// <summary>Adds one batch to a package that is still building. Throws when the package is not building (dropped or taken over).</summary>
    Task AddSymbolsAsync(long packageRowId, IReadOnlyList<NuGetSymbolEntry> symbols, CancellationToken ct);

    /// <summary>Stores the namespace map, counts the symbols and makes the package ready. Throws when the package is not building.</summary>
    Task CompleteBuildAsync(long packageRowId, IReadOnlyDictionary<string, int> typesPerNamespace, DateTimeOffset now, CancellationToken ct);

    /// <summary>Marks a build as failed and drops what it had stored. False when the package is not building any more.</summary>
    Task<bool> FailBuildAsync(long packageRowId, string error, CancellationToken ct);

    /// <summary>Null when the key was never indexed.</summary>
    Task<NuGetPackageIndex?> GetPackageAsync(NuGetPackageKey key, CancellationToken ct);

    Task<NuGetPackageOutline?> GetOutlineAsync(NuGetPackageKey key, CancellationToken ct);

    Task<NuGetSearchResult> SearchAsync(NuGetSymbolQuery query, IReadOnlyCollection<string> sections, CancellationToken ct);

    /// <summary>Symbols by id inside one package, in the order of <paramref name="sids"/>; unknown ids are skipped.</summary>
    Task<IReadOnlyList<NuGetSymbolHit>> GetBySidsAsync(long packageRowId, IReadOnlyCollection<string> sids, IReadOnlyCollection<string> sections, CancellationToken ct);

    /// <summary>
    /// Resolves a full name, a simple name or a trailing part of a full name (Chat.ChatClient) to symbols, best match first.
    /// </summary>
    Task<IReadOnlyList<NuGetSymbolHit>> ResolveByNameAsync(long packageRowId, string identifier, int limit, IReadOnlyCollection<string> sections, CancellationToken ct);

    /// <summary>False when the key was not indexed. Symbols and namespaces go with it.</summary>
    Task<bool> DropPackageAsync(NuGetPackageKey key, CancellationToken ct);
}
