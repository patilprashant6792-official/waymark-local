namespace Waymark.Core.NuGetIndex;

/// <summary>
/// The package ecosystems the pkg_* tools read: NuGet (the compiled package at the version a .csproj pins) and PyPI (the wheel at the version a lock file pins).
/// Both live in the same index tables. A PyPI package is told apart by its target: the <c>tfm</c> column holds <see cref="PyPITarget"/> instead of a
/// framework such as net10.0, so <c>Polly</c> on NuGet and <c>polly</c> on PyPI are different rows without a second schema.
/// </summary>
public static class PackageEcosystems
{
    public const string NuGet = "nuget";

    public const string PyPI = "pypi";

    /// <summary>The value of the target column of a PyPI package. A Python package has one API surface per version, not one per framework.</summary>
    public const string PyPITarget = "pypi";

    public static bool IsPyPI(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Equals(key.TargetFramework, PyPITarget, StringComparison.OrdinalIgnoreCase);
    }

    public static string Of(NuGetPackageKey key) => IsPyPI(key) ? PyPI : NuGet;

    /// <summary>Reads the 'ecosystem' argument of a tool: nuget when it is empty.</summary>
    /// <exception cref="ArgumentException">The value is neither nuget nor pypi.</exception>
    public static string Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return NuGet;
        }

        var value = text.Trim();
        if (string.Equals(value, NuGet, StringComparison.OrdinalIgnoreCase))
        {
            return NuGet;
        }

        return string.Equals(value, PyPI, StringComparison.OrdinalIgnoreCase)
            ? PyPI
            : throw new ArgumentException($"Unknown ecosystem '{value}'. Use nuget or pypi.", nameof(text));
    }
}
