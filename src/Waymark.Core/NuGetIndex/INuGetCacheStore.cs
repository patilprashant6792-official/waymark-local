namespace Waymark.Core.NuGetIndex;

/// <summary>Overview and clean-up of everything Waymark keeps about NuGet packages: the symbol index and the reflection-result cache.</summary>
public interface INuGetCacheStore
{
    Task<NuGetCacheOverview> GetOverviewAsync(CancellationToken ct);

    /// <summary>Drops the package's index (symbols and namespaces with it) and every reflection result stored for it, 'latest' alias included.</summary>
    Task<NuGetClearResult> ClearPackageAsync(NuGetPackageKey key, CancellationToken ct);

    /// <summary>Clears every package in the given state, with its stored results.</summary>
    Task<NuGetClearResult> ClearByStateAsync(NuGetIndexState state, CancellationToken ct);

    /// <summary>Clears every index and every stored reflection result.</summary>
    Task<NuGetClearResult> ClearAllAsync(CancellationToken ct);

    /// <summary>False when the key was not stored.</summary>
    Task<bool> RemoveCachedResultAsync(string cacheKey, CancellationToken ct);
}

/// <summary>Builds a package's index again from the feed, bypassing every cache.</summary>
public interface INuGetReloader
{
    /// <exception cref="Exception">Anything the download, reflection or indexing throws; the message says why.</exception>
    Task ReloadAsync(NuGetPackageKey key, IReadOnlyList<string> extraPackageIds, CancellationToken ct);
}
