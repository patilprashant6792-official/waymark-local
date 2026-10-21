namespace Waymark.Core.NuGetIndex;

/// <summary>
/// Persistent cache of a loaded package's reflection metadata. The payload is opaque text to the store; the key and the
/// payload format belong to the package loader. There is no expiry: a pinned version never changes, and an alias such as
/// 'latest' is removed by the loader when it must be resolved again.
/// </summary>
public interface INuGetMetadataStore
{
    Task<NuGetMetadataEntry?> GetAsync(string cacheKey, CancellationToken ct);

    Task SetAsync(string cacheKey, string resolvedVersion, string payload, DateTimeOffset now, CancellationToken ct);

    /// <summary>False when the key was not cached.</summary>
    Task<bool> RemoveAsync(string cacheKey, CancellationToken ct);
}
