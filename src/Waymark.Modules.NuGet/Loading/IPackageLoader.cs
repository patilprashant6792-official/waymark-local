namespace Waymark.Modules.NuGet.Loading;

/// <param name="PackageId">NuGet package id.</param>
/// <param name="Version">Exact version, or null for the latest.</param>
/// <param name="TargetFramework">Short framework name such as net10.0, or null for the configured default.</param>
/// <param name="IncludePrerelease">Only matters when <paramref name="Version"/> is null.</param>
/// <param name="ForceRefresh">Ignore the cached reflection result and load the package again.</param>
/// <param name="AdditionalPackageIds">Packages whose assemblies are added to the resolver; set after a <see cref="MissingSiblingAssembliesException"/>.</param>
public sealed record PackageLoadRequest(
    string PackageId,
    string? Version = null,
    string? TargetFramework = null,
    bool IncludePrerelease = false,
    bool ForceRefresh = false,
    IReadOnlyList<string>? AdditionalPackageIds = null);

/// <summary>Downloads a package (or reuses the shared packages folder), reflects over its assemblies and caches the result.</summary>
public interface IPackageLoader
{
    Task<ReflectedPackage> LoadAsync(PackageLoadRequest request, CancellationToken cancellationToken);
}
