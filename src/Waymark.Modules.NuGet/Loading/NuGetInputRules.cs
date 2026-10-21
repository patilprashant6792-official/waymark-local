using System.Text.RegularExpressions;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>Allow-list checks for the values that become folder names and cache keys.</summary>
internal static partial class NuGetInputRules
{
    public static string RequirePackageId(string? packageId, string parameterName)
    {
        var trimmed = packageId?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !PackageIdPattern().IsMatch(trimmed))
        {
            throw new ArgumentException("The package id must be 1 to 128 characters: letters, digits, '.', '_' or '-'.", parameterName);
        }

        return trimmed;
    }

    public static NuGetVersion? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        if (!NuGetVersion.TryParse(version.Trim(), out var parsed))
        {
            throw new ArgumentException($"Invalid version format: {version.Trim()}", nameof(version));
        }

        return parsed;
    }

    public static string NormalizeFramework(string? targetFramework, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(targetFramework) ? fallback : targetFramework.Trim();
        if (!FrameworkPattern().IsMatch(candidate) || NuGetFramework.ParseFolder(candidate).IsUnsupported)
        {
            throw new ArgumentException($"Unknown target framework: {candidate}", nameof(targetFramework));
        }

        return candidate.ToLowerInvariant();
    }

    public static IReadOnlyList<string> NormalizeExtraIds(IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        return ids
            .Select(id => RequirePackageId(id, "additionalPackageIds"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+-]{0,39}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FrameworkPattern();
}
