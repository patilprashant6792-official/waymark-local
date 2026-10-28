using System.Text.RegularExpressions;

namespace Waymark.Core.Workspaces;

/// <summary>Pure checks on workspace names and folder paths. No file system access.</summary>
public static partial class WorkspacePathRules
{
    public const int MaxNameLength = 64;
    public const int MaxPathLength = 260;

    /// <summary>Folders that never hold source a workspace should index.</summary>
    public static readonly IReadOnlySet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", "node_modules", "packages",
        ".venv", "venv", "__pycache__", "site-packages", ".tox", ".nox", ".eggs", ".mypy_cache", ".pytest_cache", ".ruff_cache",
    };

    /// <summary>Null when the name is acceptable. '*' is reserved because tools use it to mean every workspace.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the workspace a name.";
        }

        if (!NameRegex().IsMatch(name))
        {
            return $"Use 1-{MaxNameLength} letters, digits, dots, dashes or underscores, starting with a letter or digit.";
        }

        return null;
    }

    /// <summary>
    /// Trims, checks the characters and canonicalises the path (full path, no trailing separator).
    /// Returns the normalised path, or the reason it was refused.
    /// </summary>
    public static (string? Path, string? Error) NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "Folder path cannot be empty.");
        }

        var trimmed = path.Trim();
        if (trimmed.Length > MaxPathLength)
        {
            return (null, $"Folder path is longer than {MaxPathLength} characters.");
        }

        if (!PathRegex().IsMatch(trimmed))
        {
            return (null, "Folder path contains characters that are not allowed.");
        }

        if (!System.IO.Path.IsPathFullyQualified(trimmed))
        {
            return (null, "Use the full path, for example C:\\Users\\you\\source\\app.");
        }

        var full = System.IO.Path.GetFullPath(trimmed);
        var root = System.IO.Path.GetPathRoot(full);
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Choose a project folder, not a whole drive.");
        }

        return (System.IO.Path.TrimEndingDirectorySeparator(full), null);
    }

    // Letters and digits of any script plus the punctuation common in Windows folder names.
    [GeneratedRegex(@"^[\p{L}\p{N}\\/:_\-.\s()\[\]'&+,~#@]+$")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();
}
