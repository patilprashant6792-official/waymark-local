namespace Waymark.Core.Platform;

/// <summary>
/// How this computer compares file paths. Windows (NTFS) and macOS (APFS and HFS+ as formatted by default) ignore letter case, so
/// 'Src/A.cs' and 'src/a.cs' are one file there; Linux file systems do not. Used for lists, duplicates and ancestry checks.
/// The workspace boundary in PathGuard stays case-exact off Windows on purpose: a case-sensitive macOS volume exists, and there a
/// looser check would let '../REPO' reach a sibling folder of 'repo'.
/// </summary>
public static class PathComparisons
{
    /// <summary>True on Windows and macOS.</summary>
    public static bool IgnoreCase { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparison Comparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer => IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
