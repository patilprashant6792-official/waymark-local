using Waymark.Core.NuGetIndex;
namespace Waymark.Modules.NuGet.Loading;

internal static class MetadataCacheKey
{
    public const string LatestVersion = NuGetCacheKeys.LatestVersion;

    /// <summary>
    /// Package, version and framework, all lower case. Results that needed extra resolver packages carry them in the key so a plain
    /// lookup is never answered with data that only exists because extra assemblies were supplied.
    /// </summary>
    public static string Build(string packageId, string? normalizedVersion, string targetFramework, IReadOnlyList<string> extraIds)
    {
        var key = NuGetCacheKeys.Base(packageId, normalizedVersion, targetFramework);
        return extraIds.Count == 0 ? key : NuGetCacheKeys.WithExtras(key, extraIds);
    }
}
