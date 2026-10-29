using Waymark.Core.Security;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>A source file found on disk. RelativePath uses '/'.</summary>
internal sealed record DiskFile(string RelativePath, string AbsolutePath, long Size, DateTimeOffset ModifiedUtc);

/// <summary>
/// Lists the source files (of every indexed language) of a workspace that tools are allowed to read. Folders and files the access policy refuses
/// (build output, version control, secrets) and linked folders and files are left out, so the index never holds
/// anything a tool could not open.
/// </summary>
internal static class CodeFileWalker
{
    private const string FolderProbeName = "waymark-folder-probe";

    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public static (List<DiskFile> Files, bool Truncated) Enumerate(string root, FileAccessPolicy policy, int maxFiles, LanguageIndexers languages, CancellationToken ct) =>
        Enumerate(root, null, policy, maxFiles, languages, ct);

    /// <summary>Like the overload above, but from one folder of the workspace only (a workspace-relative path with '/'); null or empty means the whole workspace.</summary>
    public static (List<DiskFile> Files, bool Truncated) Enumerate(string root, string? startFolder, FileAccessPolicy policy, int maxFiles, LanguageIndexers languages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(languages);
        var files = new List<DiskFile>();
        var pending = new Stack<DirectoryInfo>();
        if (string.IsNullOrEmpty(startFolder))
        {
            pending.Push(new DirectoryInfo(root));
        }
        else if (IsTraversableFolder(startFolder, policy))
        {
            // A folder that is gone, or is a link, has no files: the caller then removes whatever the index still holds below it.
            var start = new DirectoryInfo(Path.Combine(root, startFolder.Replace('/', Path.DirectorySeparatorChar)));
            if (start.Exists && (start.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                pending.Push(start);
            }
        }

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in directory.EnumerateFiles("*", EntryOptions))
            {
                if (!languages.Handles(file.Name))
                {
                    continue;
                }

                var relative = ToRelative(root, file.FullName);
                if (!IsIndexable(relative, policy))
                {
                    continue;
                }

                if (files.Count >= maxFiles)
                {
                    return (files, true);
                }

                files.Add(new DiskFile(relative, file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }

            foreach (var child in directory.EnumerateDirectories("*", EntryOptions))
            {
                if (IsTraversableFolder(ToRelative(root, child.FullName), policy))
                {
                    pending.Push(child);
                }
            }
        }

        return (files, false);
    }

    /// <summary>The same rules the path guard applies to a file path: policy plus no ':' and no 8.3 short name marker.</summary>
    public static bool IsIndexable(string relativePath, FileAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return !relativePath.Contains(':') && !HasShortNameMarker(relativePath) && policy.Evaluate(relativePath) is null && !IsGeneratedOutput(relativePath);
    }

    /// <summary>
    /// Build output of a JavaScript or TypeScript project: anything in a dist, out or .output folder, and JavaScript in a build folder. It repeats the source in
    /// a form nobody reads, and it would fill the index with duplicates. C# files are never judged by this.
    /// </summary>
    internal static bool IsGeneratedOutput(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        var isScript = extension.Equals(".js", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase);
        var isTypeScript = extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cts", StringComparison.OrdinalIgnoreCase);
        if (!isScript && !isTypeScript)
        {
            return false;
        }

        foreach (var folder in relativePath.Split('/').SkipLast(1))
        {
            if (folder.Equals("dist", StringComparison.OrdinalIgnoreCase) || folder.Equals("out", StringComparison.OrdinalIgnoreCase) || folder.Equals(".output", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isScript && folder.Equals("build", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a folder may be entered. A folder name such as 'Waymark.Core' looks like a file with an unknown extension to
    /// the policy, so a child name is judged instead of the folder name itself.
    /// </summary>
    public static bool IsTraversableFolder(string relativePath, FileAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return !relativePath.Contains(':') && !HasShortNameMarker(relativePath) && policy.Evaluate($"{relativePath}/{FolderProbeName}") is null;
    }

    public static string ToRelative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    internal static bool HasShortNameMarker(string path)
    {
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (path[i] == '~' && char.IsAsciiDigit(path[i + 1]))
            {
                return true;
            }
        }

        return false;
    }
}
