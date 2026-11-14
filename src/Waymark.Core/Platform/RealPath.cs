namespace Waymark.Core.Platform;

/// <summary>
/// The path of an existing folder as the file system stores it. Off Windows every link on the way is resolved (macOS: /tmp and /var are
/// links to /private/...) and every name gets its stored spelling (macOS ignores case, so '/users/me/Repo' opens '/Users/me/repo').
/// A workspace root kept this way matches the paths the file watcher, link checks and process paths report, which always come in
/// the stored form. Windows is left as it is: its comparisons ignore case already and stored workspace paths must not change.
/// </summary>
public static class RealPath
{
    private const int MaxLinkHops = 32;

    /// <param name="fullPath">A fully qualified path of an existing folder.</param>
    /// <returns>The stored form, or <paramref name="fullPath"/> unchanged on Windows or when a part of it cannot be read.</returns>
    public static string OfDirectory(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Resolve(Path.GetFullPath(fullPath), 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return fullPath;
        }
    }

    private static string Resolve(string fullPath, int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? "/";
        var parts = fullPath[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i];
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? root;
                continue;
            }

            var stored = StoredName(current, name);
            if (stored is null)
            {
                // Unreadable or missing: keep the rest as given.
                return Path.Combine([current, .. parts[i..]]);
            }

            var next = Path.Combine(current, stored);
            var info = new DirectoryInfo(next);
            if (info.LinkTarget is not null)
            {
                if (hops >= MaxLinkHops)
                {
                    throw new IOException($"Too many links on the way to '{fullPath}'.");
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException($"The link '{next}' cannot be followed.");
                return Resolve(Path.Combine([target.FullName, .. parts[(i + 1)..]]), hops + 1);
            }

            current = next;
        }

        return current;
    }

    // The exact name wins (a case-sensitive volume may hold both 'a' and 'A'); otherwise the entry that differs only in case or in
    // Unicode composition (HFS+ stores 'é' decomposed, a typed path has it composed).
    // Names are compared, never used as a search pattern, because '*', '?' and '[' are ordinary characters in macOS file names.
    private static string? StoredName(string folder, string name)
    {
        var composed = name.Normalize(System.Text.NormalizationForm.FormC);
        string? looseMatch = null;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.Ordinal))
                {
                    return entryName;
                }

                if (looseMatch is null
                    && string.Equals(entryName.Normalize(System.Text.NormalizationForm.FormC), composed, StringComparison.OrdinalIgnoreCase))
                {
                    looseMatch = entryName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return looseMatch;
    }
}
