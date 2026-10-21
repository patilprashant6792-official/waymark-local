using Waymark.Core.Security;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>A file the access policy lets tools see. RelativePath uses '/'.</summary>
internal sealed record VisibleFile(string RelativePath, FileInfo Info);

/// <summary>
/// Lists the folders and files of a workspace that tools may see. Anything the access policy refuses, and every link,
/// is left out, so an overview never names what code_read would refuse to open.
/// </summary>
internal static class VisibleEntries
{
    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public static List<DirectoryInfo> Folders(string root, string directory, FileAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new DirectoryInfo(directory)
            .EnumerateDirectories("*", EntryOptions)
            .Where(child => CodeFileWalker.IsTraversableFolder(CodeFileWalker.ToRelative(root, child.FullName), policy))
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public static List<FileInfo> Files(string root, string directory, FileAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new DirectoryInfo(directory)
            .EnumerateFiles("*", EntryOptions)
            .Where(file => CodeFileWalker.IsIndexable(CodeFileWalker.ToRelative(root, file.FullName), policy))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Walks the whole workspace and returns the visible files accepted by <paramref name="include"/>, sorted by path.
    /// Truncated is true when more than <paramref name="maxScanned"/> files were examined or more than <paramref name="maxResults"/> matched.
    /// A folder that cannot be listed is skipped.
    /// </summary>
    public static (List<VisibleFile> Files, bool Truncated) Scan(
        string root,
        FileAccessPolicy policy,
        Func<FileInfo, bool> include,
        int maxScanned,
        int maxResults,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(include);
        var results = new List<VisibleFile>();
        var scanned = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Files(root, directory, policy))
                {
                    if (++scanned > maxScanned)
                    {
                        return (Sorted(results), true);
                    }

                    if (!include(file))
                    {
                        continue;
                    }

                    if (results.Count >= maxResults)
                    {
                        return (Sorted(results), true);
                    }

                    results.Add(new VisibleFile(CodeFileWalker.ToRelative(root, file.FullName), file));
                }

                foreach (var child in Folders(root, directory, policy))
                {
                    pending.Push(child.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return (Sorted(results), false);
    }

    private static List<VisibleFile> Sorted(List<VisibleFile> files) =>
        files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
}
