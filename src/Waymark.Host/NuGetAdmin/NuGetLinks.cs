using Waymark.Core.NuGetIndex;

namespace Waymark.Host.NuGetAdmin;

/// <summary>Addresses of the NuGet pages, so every page builds them the same way.</summary>
public static class NuGetLinks
{
    /// <summary>The explorer page of one indexed package, optionally opened on a symbol.</summary>
    public static string Explore(NuGetPackageKey key, string? sid = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = $"/nuget/{Uri.EscapeDataString(key.PackageId)}/{Uri.EscapeDataString(key.Version)}/{Uri.EscapeDataString(key.TargetFramework)}";
        return string.IsNullOrEmpty(sid) ? path : path + "?sid=" + Uri.EscapeDataString(sid);
    }

    public static string OnNuGetOrg(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"https://www.nuget.org/packages/{Uri.EscapeDataString(key.PackageId)}/{Uri.EscapeDataString(key.Version)}";
    }
}
