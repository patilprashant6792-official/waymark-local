using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Indexing;

/// <param name="Version">Exact version, or null for the latest.</param>
/// <param name="TargetFramework">Short framework name, or null for the configured default. A PyPI request has none: it is <see cref="PackageEcosystems.PyPITarget"/>.</param>
/// <param name="IncludePrerelease">Only matters when <paramref name="Version"/> is null.</param>
/// <param name="AdditionalPackageIds">Resolver packages, set after a missing-assembly error (NuGet only).</param>
/// <param name="Ecosystem">nuget or pypi; see <see cref="PackageEcosystems"/>.</param>
public sealed record PackageIndexRequest(
    string PackageId,
    string? Version = null,
    string? TargetFramework = null,
    bool IncludePrerelease = false,
    IReadOnlyList<string>? AdditionalPackageIds = null,
    string Ecosystem = PackageEcosystems.NuGet);

/// <summary>Makes sure a package is indexed (downloading and reading it when it is not) and returns the ready index.</summary>
internal interface IPackageIndexer
{
    /// <param name="refresh">Rebuild even when a usable index exists (and bypass the cached reflection result).</param>
    Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken);
}
