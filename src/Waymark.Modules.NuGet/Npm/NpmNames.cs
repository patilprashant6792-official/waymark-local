using System.Text.RegularExpressions;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>Allow-list checks for the values that become cache keys, folder names and web addresses.</summary>
internal static partial class NpmNames
{
    /// <summary>True when the text is an npm package name: lower case, optionally scoped, no leading dot, underscore or dash.</summary>
    public static bool IsPackage(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 214 && NamePattern().IsMatch(trimmed);
    }

    public static string RequirePackage(string? name, string parameter)
    {
        var trimmed = name?.Trim();
        return IsPackage(trimmed)
            ? trimmed!
            : throw new ArgumentException("The package name must be an npm name in lower case, such as 'lodash' or '@types/node'.", parameter);
    }

    /// <summary>An exact version (1.2.3, 1.2.3-beta.1); null when empty. A range such as ^1.2.0 is refused: the lock file pins an exact one.</summary>
    public static string? RequireVersion(string? version)
    {
        var trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return VersionPattern().IsMatch(trimmed)
            ? trimmed
            : throw new ArgumentException($"'{trimmed}' is not an exact version such as 4.17.21.", nameof(version));
    }

    /// <summary>The @types package that holds the declarations of a package: <c>react</c> is <c>@types/react</c>, <c>@scope/pkg</c> is <c>@types/scope__pkg</c>.</summary>
    public static string TypesPackageOf(string name) =>
        "@types/" + (name.StartsWith('@') ? name[1..].Replace("/", "__", StringComparison.Ordinal) : name);

    /// <summary>The package name as the registry wants it in a path: the slash of a scope is escaped.</summary>
    public static string PathSegment(string name) => name.Replace("/", "%2F", StringComparison.Ordinal);

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~\-]{0,99}/)?[a-z0-9][a-z0-9._~\-]{0,99}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.\-]{1,50})?(\+[0-9A-Za-z.\-]{1,50})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();
}
