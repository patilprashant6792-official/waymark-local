using System.Text.RegularExpressions;
using Waymark.Core.Text;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>Allow-list checks for the values that become cache keys, folder names and web addresses.</summary>
internal static partial class PypiNames
{
    /// <summary>True when the text is a project name; the check that does not throw.</summary>
    public static bool IsProject(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && NamePattern().IsMatch(trimmed);
    }

    /// <summary>A project name as PEP 508 writes it: letters and digits, with '.', '_' and '-' between them.</summary>
    public static string RequireProject(string? name, string parameter)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !NamePattern().IsMatch(trimmed))
        {
            throw new ArgumentException("The package name must be 1 to 100 characters: letters and digits, with '.', '_' or '-' between them (for example 'requests' or 'scikit-learn').", parameter);
        }

        return trimmed;
    }

    /// <summary>An exact version: digits, letters and '.', '+', '!', '_', '-' (PEP 440), starting with a digit. Null when empty.</summary>
    public static string? RequireVersion(string? version)
    {
        var trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return VersionPattern().IsMatch(trimmed)
            ? trimmed
            : throw new ArgumentException($"'{trimmed}' is not an exact version such as 2.32.3.", nameof(version));
    }

    /// <summary>The PEP 503 form that keys the index: <c>Scikit_Learn</c> and <c>scikit.learn</c> are both <c>scikit-learn</c>.</summary>
    public static string Normalize(string name) => PythonLockFiles.Normalize(name);

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+!_-]{0,49}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();
}
