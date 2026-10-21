namespace Waymark.Host.Ui;

/// <summary>Shows workspace folders relative to the folder they share, so the full location on disk is not displayed.</summary>
public static class WorkspacePathDisplay
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The deepest folder that contains every given folder, or null when they share none (for example different drives).</summary>
    public static string? CommonParent(IEnumerable<string> paths)
    {
        string[]? shared = null;
        foreach (var path in paths)
        {
            var segments = Split(path);
            var parent = segments.Length > 1 ? segments[..^1] : [];
            shared = shared is null
                ? parent
                : shared.TakeWhile((segment, i) => i < parent.Length && Same(segment, parent[i])).ToArray();
        }

        return shared is { Length: > 0 } ? string.Join('\\', shared) : null;
    }

    /// <summary>The path below commonParent. A path outside it, or no parent at all, shows only the folder name.</summary>
    public static string Relative(string path, string? commonParent)
    {
        var segments = Split(path);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        if (commonParent is not null)
        {
            var root = Split(commonParent);
            if (segments.Length > root.Length && root.Where((segment, i) => Same(segment, segments[i])).Count() == root.Length)
            {
                return string.Join('\\', segments[root.Length..]);
            }
        }

        return segments[^1];
    }

    private static string[] Split(string path) => path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
