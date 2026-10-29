using Waymark.Core.NuGetIndex;

namespace Waymark.Host.NuGetAdmin;

/// <summary>Addresses of the package pages, so every page builds them the same way.</summary>
public static class NuGetLinks
{
    // A scoped npm name (@types/node) has a slash, which a route segment cannot hold; it travels as this marker instead.
    private const string SlashMarker = "~~";

    /// <summary>The explorer page of one indexed package, optionally opened on a symbol.</summary>
    public static string Explore(NuGetPackageKey key, string? sid = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = $"/packages/{Uri.EscapeDataString(EncodeId(key.PackageId))}/{Uri.EscapeDataString(key.Version)}/{Uri.EscapeDataString(key.TargetFramework)}";
        return string.IsNullOrEmpty(sid) ? path : path + "?sid=" + Uri.EscapeDataString(sid);
    }

    /// <summary>The package id as one route segment.</summary>
    public static string EncodeId(string packageId) => packageId.Replace("/", SlashMarker, StringComparison.Ordinal);

    /// <summary>The package id back from a route segment.</summary>
    public static string DecodeId(string segment) => segment.Replace(SlashMarker, "/", StringComparison.Ordinal);

    /// <summary>What kind of package it is, for a chip or a list line: <c>NuGet net10.0</c>, <c>PyPI</c> or <c>npm</c>.</summary>
    public static string EcosystemLabel(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return PackageEcosystems.Of(key) switch
        {
            PackageEcosystems.PyPI => "PyPI",
            PackageEcosystems.Npm => "npm",
            _ => $"NuGet {key.TargetFramework}",
        };
    }

    /// <summary>The package's page on its registry: nuget.org, pypi.org or npmjs.com.</summary>
    public static string OnNuGetOrg(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return PackageEcosystems.Of(key) switch
        {
            PackageEcosystems.PyPI => $"https://pypi.org/project/{Uri.EscapeDataString(key.PackageId)}/{Uri.EscapeDataString(key.Version)}/",
            PackageEcosystems.Npm => $"https://www.npmjs.com/package/{key.PackageId}/v/{Uri.EscapeDataString(key.Version)}",
            _ => $"https://www.nuget.org/packages/{Uri.EscapeDataString(key.PackageId)}/{Uri.EscapeDataString(key.Version)}",
        };
    }

    public static string RegistryName(NuGetPackageKey key) => PackageEcosystems.Of(key) switch
    {
        PackageEcosystems.PyPI => "pypi.org",
        PackageEcosystems.Npm => "npmjs.com",
        _ => "nuget.org",
    };
}
