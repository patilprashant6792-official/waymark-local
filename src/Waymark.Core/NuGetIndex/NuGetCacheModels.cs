namespace Waymark.Core.NuGetIndex;

/// <summary>
/// How cache keys of the reflection-result cache are written. The loader builds them and the admin store reads them back,
/// so the format lives in one place: lower case, package@version@framework, plus an extras marker when extra resolver packages were used.
/// </summary>
public static class NuGetCacheKeys
{
    public const string LatestVersion = "latest";
    public const string ExtrasMarker = "@extras:";

    /// <param name="version">Null or empty means the 'latest' alias.</param>
    public static string Base(string packageId, string? version, string targetFramework) =>
        $"{packageId}@{(string.IsNullOrEmpty(version) ? LatestVersion : version)}@{targetFramework}".ToLowerInvariant();

    public static string WithExtras(string baseKey, IReadOnlyList<string> extraIds) =>
        $"{baseKey}{ExtrasMarker}{string.Join('+', extraIds)}".ToLowerInvariant();

    /// <summary>The extra resolver package ids in the key; empty when it has none.</summary>
    public static IReadOnlyList<string> ExtrasOf(string cacheKey)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        var at = cacheKey.IndexOf(ExtrasMarker, StringComparison.Ordinal);
        return at < 0 ? [] : cacheKey[(at + ExtrasMarker.Length)..].Split('+', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>One indexed package with what it costs. Cached results are the reflection results stored under the package's keys.</summary>
/// <param name="IndexBytes">Approximate size of the stored symbol text.</param>
/// <param name="ExtraPackageIds">Resolver packages the package needed when it was loaded; a reload must pass them again.</param>
public sealed record NuGetCacheEntry(
    NuGetPackageIndex Package,
    long IndexBytes,
    int CachedResults,
    long CachedBytes,
    DateTimeOffset? CachedUtc,
    IReadOnlyList<string> ExtraPackageIds);

/// <summary>A stored reflection result.</summary>
public sealed record NuGetCachedResult(string CacheKey, string ResolvedVersion, long Bytes, DateTimeOffset StoredUtc);

public sealed record NuGetCacheStats(
    int Ready,
    int Failed,
    int Building,
    long Types,
    long Symbols,
    long IndexBytes,
    int CachedResults,
    long CachedBytes,
    DateTimeOffset? LastIndexedUtc);

/// <param name="OtherResults">Stored reflection results that belong to no indexed package (a failed or cleared index, or a 'latest' alias).</param>
public sealed record NuGetCacheOverview(
    NuGetCacheStats Stats,
    IReadOnlyList<NuGetCacheEntry> Entries,
    IReadOnlyList<NuGetCachedResult> OtherResults);

public sealed record NuGetClearResult(int Packages, int CachedResults);
