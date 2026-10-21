using Waymark.Core.Security;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Modules.Code.Content;

/// <summary>
/// Lists the files of a workspace that code_grep may read: everything the workspace's access rules allow (not only C#),
/// minus vendored and minified files and files over the size limit. What is left out is counted so a result can say so.
/// </summary>
internal static class ContentFileWalker
{
    private const string FolderProbeName = "waymark-folder-probe";

    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>Files come back in path order. Past MaxFilesPerWorkspace the walk stops and Truncated is true.</summary>
    public static ContentWalk Enumerate(
        string root,
        FileAccessPolicy policy,
        WorkspaceAccessRules rules,
        ContentOptions options,
        PathGlob? glob,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<ContentFile>();
        var coverage = new ScanCoverage();
        var truncated = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in directory.EnumerateFiles("*", EntryOptions))
            {
                var relative = CodeFileWalker.ToRelative(root, file.FullName);
                if (!IsAllowed(relative, policy, rules) || (glob is not null && !glob.IsMatch(relative)))
                {
                    continue;
                }

                if (!TryClassify(file, relative, options, coverage, out var size))
                {
                    continue;
                }

                if (files.Count >= options.MaxFilesPerWorkspace)
                {
                    truncated = true;
                    break;
                }

                files.Add(new ContentFile(relative, file.FullName, size));
            }

            if (truncated)
            {
                break;
            }

            foreach (var child in directory.EnumerateDirectories("*", EntryOptions))
            {
                if (IsTraversableFolder(CodeFileWalker.ToRelative(root, child.FullName), policy, rules))
                {
                    pending.Push(child);
                }
            }
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return new ContentWalk(files, coverage, truncated);
    }

    // True when the file should be read; otherwise the reason is counted (or the file is empty and ignored).
    private static bool TryClassify(FileInfo file, string relative, ContentOptions options, ScanCoverage coverage, out long size)
    {
        size = 0;
        try
        {
            size = file.Length;
        }
        catch (IOException)
        {
            coverage.Unreadable++;
            return false;
        }

        if (size == 0)
        {
            return false;
        }

        if (ContentEligibility.IsMinifiedName(file.Name) || ContentEligibility.IsVendored(relative, options.VendoredFolders))
        {
            coverage.VendoredOrMinified++;
            return false;
        }

        if (size > options.MaxFileBytes)
        {
            coverage.TooLarge++;
            return false;
        }

        return true;
    }

    // The same rules the path guard applies to a file path, with this workspace's own rules.
    private static bool IsAllowed(string relativePath, FileAccessPolicy policy, WorkspaceAccessRules rules) =>
        !relativePath.Contains(':')
        && !CodeFileWalker.HasShortNameMarker(relativePath)
        && policy.Evaluate(relativePath, rules) is null;

    // A folder name such as 'Waymark.Core' looks like a file with an unknown extension to the policy, so a child name is judged.
    private static bool IsTraversableFolder(string relativePath, FileAccessPolicy policy, WorkspaceAccessRules rules) =>
        !relativePath.Contains(':')
        && !CodeFileWalker.HasShortNameMarker(relativePath)
        && policy.Evaluate($"{relativePath}/{FolderProbeName}", rules) is null;
}
