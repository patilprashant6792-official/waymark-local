using System.Reflection;

namespace Waymark.Host.Ui;

/// <summary>
/// The product version. It comes from the Version property in Directory.Build.props, the one place to bump it; the menu, feedback
/// emails and the MCP server info all read it here, so they always say the same.
/// </summary>
public static class AppInfo
{
    private const string Unknown = "0.0.0";

    // Declared before the properties: static initializers run in the order they are written.
    private static readonly string? Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>The release, such as "0.4.0".</summary>
    public static string Version { get; } = ReleaseOf(Informational);

    /// <summary>The release plus the short commit the build came from, when the SDK stamped one: "0.4.0 (eb7f7da)".</summary>
    public static string VersionWithBuild { get; } = Describe(Informational);

    /// <summary>The SDK writes the informational version as the release, then '+' and the commit ("0.4.0+eb7f7da...").</summary>
    internal static string ReleaseOf(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Unknown;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var release = (plus < 0 ? informationalVersion : informationalVersion[..plus]).Trim();
        return release.Length == 0 ? Unknown : release;
    }

    internal static string Describe(string? informationalVersion)
    {
        var release = ReleaseOf(informationalVersion);
        var plus = informationalVersion?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (informationalVersion is null || plus < 0 || plus == informationalVersion.Length - 1)
        {
            return release;
        }

        var commit = informationalVersion[(plus + 1)..];
        return release + " (" + commit[..Math.Min(7, commit.Length)] + ")";
    }
}
