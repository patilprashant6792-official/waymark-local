using Waymark.Core.Security;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>A C# file found on disk. RelativePath uses '/'.</summary>
internal sealed record DiskFile(string RelativePath, string AbsolutePath, long Size, DateTimeOffset ModifiedUtc);

/// <summary>
/// Lists the C# files of a workspace that tools are allowed to read. Folders and files the access policy refuses
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

    public static (List<DiskFile> Files, bool Truncated) Enumerate(string root, FileAccessPolicy policy, int maxFiles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var files = new List<DiskFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in directory.EnumerateFiles("*.cs", EntryOptions))
            {
                if (!file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
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
        return !relativePath.Contains(':') && !HasShortNameMarker(relativePath) && policy.Evaluate(relativePath) is null;
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
