using System.Reflection;

namespace Waymark.Host.Ui;

/// <summary>The product version: shown at the foot of the menu and sent with feedback, so both always say the same.</summary>
public static class AppInfo
{
    /// <summary>The release, as people see it. Change it with each release.</summary>
    public const string Version = "0.3";

    /// <summary>The release plus the short commit the build came from, when the SDK stamped one: "0.3 (eb7f7da)".</summary>
    public static string VersionWithBuild { get; } =
        Describe(typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The SDK writes the commit after a '+' in the informational version ("1.0.0+eb7f7da..."); only that part is used.</summary>
    internal static string Describe(string? informationalVersion)
    {
        var plus = informationalVersion?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (informationalVersion is null || plus < 0 || plus == informationalVersion.Length - 1)
        {
            return Version;
        }

        var commit = informationalVersion[(plus + 1)..];
        return Version + " (" + commit[..Math.Min(7, commit.Length)] + ")";
    }
}
