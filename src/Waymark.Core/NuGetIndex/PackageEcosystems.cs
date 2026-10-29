namespace Waymark.Core.NuGetIndex;

/// <summary>
/// The package ecosystems the pkg_* tools read: NuGet (the compiled package at the version a .csproj pins), PyPI (the wheel at the version a lock file pins)
/// and npm (the declaration files of the package at the version a lock file pins). All live in the same index tables. A package of another ecosystem than
/// NuGet is told apart by its target: the <c>tfm</c> column holds <see cref="PyPITarget"/> or <see cref="NpmTarget"/> instead of a framework such as net10.0,
/// so <c>Polly</c> on NuGet, <c>polly</c> on PyPI and <c>polly</c> on npm are different rows without a second schema.
/// </summary>
public static class PackageEcosystems
{
    public const string NuGet = "nuget";

    public const string PyPI = "pypi";

    public const string Npm = "npm";

    /// <summary>The value of the target column of a PyPI package. A Python package has one API surface per version, not one per framework.</summary>
    public const string PyPITarget = "pypi";

    /// <summary>The value of the target column of an npm package.</summary>
    public const string NpmTarget = "npm";

    public static bool IsPyPI(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Equals(key.TargetFramework, PyPITarget, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNpm(NuGetPackageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Equals(key.TargetFramework, NpmTarget, StringComparison.OrdinalIgnoreCase);
    }

    public static string Of(NuGetPackageKey key) => IsPyPI(key) ? PyPI : IsNpm(key) ? Npm : NuGet;

    /// <summary>The target column value of an ecosystem; null for NuGet, whose target is a framework.</summary>
    public static string? TargetOf(string ecosystem) => ecosystem switch
    {
        PyPI => PyPITarget,
        Npm => NpmTarget,
        _ => null,
    };

    /// <summary>What the dotted or slashed container of a symbol is called in this ecosystem: a namespace in NuGet, a module in the others.</summary>
    public static string ContainerWord(NuGetPackageKey key) => Of(key) == NuGet ? "namespace(s)" : "module(s)";

    /// <summary>The info string of a markdown code fence for the symbols of this package.</summary>
    public static string Fence(NuGetPackageKey key) => Of(key) switch
    {
        PyPI => "python",
        Npm => "typescript",
        _ => "csharp",
    };

    /// <summary>Reads the 'ecosystem' argument of a tool: nuget when it is empty.</summary>
    /// <exception cref="ArgumentException">The value is not nuget, pypi or npm.</exception>
    public static string Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return NuGet;
        }

        var value = text.Trim().ToLowerInvariant();
        return value switch
        {
            NuGet => NuGet,
            PyPI => PyPI,
            Npm => Npm,
            _ => throw new ArgumentException($"Unknown ecosystem '{text.Trim()}'. Use nuget, pypi or npm.", nameof(text)),
        };
    }
}
