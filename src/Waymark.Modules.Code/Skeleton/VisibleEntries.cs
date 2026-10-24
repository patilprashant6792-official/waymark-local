using Waymark.Core.Security;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>A file the access policy lets tools see. RelativePath uses '/'.</summary>
internal sealed record VisibleFile(string RelativePath, FileInfo Info);

/// <summary>
/// Lists the folders and files of a workspace that tools may see: the built-in policy plus the workspace's own access rules,
/// the same check the path guard makes. Anything refused, and every link, is left out, so an overview never names what
/// code_read would refuse to open. Entries are sorted by name ignoring case, then by exact name, so the order is the same on every call.
/// </summary>
internal static class VisibleEntries
{
    private const string FolderProbeName = "waymark-folder-probe";

    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public static List<DirectoryInfo> Folders(string root, string directory, FileAccessPolicy policy, WorkspaceAccessRules rules)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);
        return new DirectoryInfo(directory)
            .EnumerateDirectories("*", EntryOptions)
            .Where(child => IsVisibleFolder(CodeFileWalker.ToRelative(root, child.FullName), policy, rules))
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public static List<FileInfo> Files(string root, string directory, FileAccessPolicy policy, WorkspaceAccessRules rules)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);
        return new DirectoryInfo(directory)
            .EnumerateFiles("*", EntryOptions)
            .Where(file => IsVisibleFile(CodeFileWalker.ToRelative(root, file.FullName), policy, rules))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Walks the tree below <paramref name="start"/> (a folder inside <paramref name="root"/>) and returns the visible files accepted by
    /// <paramref name="include"/>, sorted by path. Truncated is true when more than <paramref name="maxScanned"/> files were examined or
    /// more than <paramref name="maxResults"/> matched. A folder that cannot be listed is skipped.
    /// </summary>
    public static (List<VisibleFile> Files, bool Truncated) Scan(
        string root,
        string start,
        FileAccessPolicy policy,
        WorkspaceAccessRules rules,
        Func<FileInfo, bool> include,
        int maxScanned,
        int maxResults,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(include);
        var results = new List<VisibleFile>();
        var scanned = 0;
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Files(root, directory, policy, rules))
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

                foreach (var child in Folders(root, directory, policy, rules))
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

    /// <summary>The path guard's file rules: no ':' and no 8.3 short name marker, then the policy with this workspace's rules.</summary>
    public static bool IsVisibleFile(string relativePath, FileAccessPolicy policy, WorkspaceAccessRules rules) =>
        !relativePath.Contains(':')
        && !CodeFileWalker.HasShortNameMarker(relativePath)
        && policy.Evaluate(relativePath, rules) is null;

    /// <summary>A folder name such as 'Waymark.Core' looks like a file with an unknown extension to the policy, so a child name is judged.</summary>
    public static bool IsVisibleFolder(string relativePath, FileAccessPolicy policy, WorkspaceAccessRules rules) =>
        !relativePath.Contains(':')
        && !CodeFileWalker.HasShortNameMarker(relativePath)
        && policy.Evaluate($"{relativePath}/{FolderProbeName}", rules) is null;

    private static List<VisibleFile> Sorted(List<VisibleFile> files) =>
        files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
}
