using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Indexing;

/// <param name="Version">Exact version, or null for the latest.</param>
/// <param name="TargetFramework">Short framework name, or null for the configured default.</param>
/// <param name="IncludePrerelease">Only matters when <paramref name="Version"/> is null.</param>
/// <param name="AdditionalPackageIds">Resolver packages, set after a missing-assembly error.</param>
public sealed record PackageIndexRequest(
    string PackageId,
    string? Version = null,
    string? TargetFramework = null,
    bool IncludePrerelease = false,
    IReadOnlyList<string>? AdditionalPackageIds = null);

/// <summary>Makes sure a package is indexed (downloading and reflecting it when it is not) and returns the ready index.</summary>
internal interface IPackageIndexer
{
    /// <param name="refresh">Rebuild even when a usable index exists (and bypass the cached reflection result).</param>
    Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken);
}
