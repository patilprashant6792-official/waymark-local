using System.Globalization;
using Waymark.Core.Security;
using Waymark.Modules.Code.Content;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// One folder found by <see cref="FolderScanner"/>. The totals cover everything visible below it, so a folder that is not
/// opened in the tree can still be summarised in one line.
/// </summary>
internal sealed class FolderNode(FolderNode? parent, string name, string relativePath, bool isVendored)
{
    public FolderNode? Parent { get; } = parent;

    public string Name { get; } = name;

    /// <summary>Workspace-relative path with '/'; empty for the workspace root.</summary>
    public string RelativePath { get; } = relativePath;

    /// <summary>Inside one of the vendored folders that code_grep also leaves out (wwwroot/lib, dist, vendor).</summary>
    public bool IsVendored { get; } = isVendored;

    /// <summary>False when the scan stopped before it reached this folder.</summary>
    public bool Scanned { get; set; }

    public bool Unreadable { get; set; }

    public List<FolderNode> Folders { get; } = [];

    public List<FileInfo> Files { get; } = [];

    /// <summary>The first project file directly inside this folder, if any.</summary>
    public string? ProjectFile { get; set; }

    public int TotalFiles { get; set; }

    public int TotalFolders { get; set; }

    public long TotalBytes { get; set; }

    public int TotalLargeFiles { get; set; }

    /// <summary>Part of the subtree was not scanned or could not be read, so the totals are lower bounds.</summary>
    public bool Incomplete { get; set; }

    /// <summary>File count per extension (lower case, "(none)" for no extension) in the whole subtree.</summary>
    public Dictionary<string, int> Extensions { get; } = new(StringComparer.Ordinal);

    public bool HasEntries => Folders.Count > 0 || Files.Count > 0;

    /// <summary>
    /// Follows folders that hold no file and exactly one subfolder, the way an IDE shows 'lib/bootstrap/dist' as one line.
    /// The chain stops where the vendored mark changes, so a vendored folder is never hidden inside a normal one.
    /// </summary>
    public (string Label, FolderNode Tail) FollowChain()
    {
        var label = Name;
        var tail = this;
        while (tail.Scanned
            && !tail.Unreadable
            && tail.Files.Count == 0
            && tail.Folders.Count == 1
            && tail.Folders[0].IsVendored == tail.IsVendored)
        {
            tail = tail.Folders[0];
            label = $"{label}/{tail.Name}";
        }

        return (label, tail);
    }
}

/// <param name="StopReason">Why the scan stopped early, or null when the whole subtree was scanned.</param>
internal sealed record FolderScanResult(FolderNode Root, string? StopReason);

/// <summary>
/// Walks one folder's subtree breadth-first and rolls the totals up. Only folder listings are read, never file contents, so even a
/// large workspace costs one directory listing per folder; cloud placeholders are skipped as links and never downloaded.
/// The walk stops at SkeletonOptions.MaxScanEntries entries or after the time budget, and the folders it did not reach are marked.
/// </summary>
internal sealed class FolderScanner(FileAccessPolicy policy, SkeletonOptions options, TimeProvider time)
{
    private const string ProjectExtension = ".csproj";
    private const string NoExtension = "(none)";
    private const string FolderProbeName = "waymark-folder-probe";

    /// <param name="workspaceRoot">Absolute workspace folder; relative paths are measured from it.</param>
    /// <param name="startAbsolute">Absolute folder to scan.</param>
    /// <param name="startRelative">That folder relative to the workspace root; empty for the root.</param>
    /// <param name="startName">Name shown for the scanned folder.</param>
    public FolderScanResult Scan(
        string workspaceRoot,
        string startAbsolute,
        string startRelative,
        string startName,
        WorkspaceAccessRules rules,
        IReadOnlyList<string> vendoredFolders,
        TimeSpan timeBudget,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(vendoredFolders);

        var root = new FolderNode(null, startName, startRelative, IsVendoredFolder(startRelative, vendoredFolders));
        var order = new List<FolderNode>();
        var pending = new Queue<(FolderNode Node, string Absolute)>();
        pending.Enqueue((root, startAbsolute));
        var started = time.GetTimestamp();
        var entries = 0;
        string? stopReason = null;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, absolute) = pending.Dequeue();
            order.Add(node);
            stopReason ??= StopReason(entries, time.GetElapsedTime(started), timeBudget);
            if (stopReason is not null)
            {
                continue;
            }

            List<DirectoryInfo> folders;
            List<FileInfo> files;
            try
            {
                folders = VisibleEntries.Folders(workspaceRoot, absolute, policy, rules);
                files = VisibleEntries.Files(workspaceRoot, absolute, policy, rules);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                node.Scanned = true;
                node.Unreadable = true;
                continue;
            }

            node.Scanned = true;
            entries += folders.Count + files.Count;
            node.Files.AddRange(files);
            node.ProjectFile = files.FirstOrDefault(file => file.Extension.Equals(ProjectExtension, StringComparison.OrdinalIgnoreCase))?.Name;
            foreach (var folder in folders)
            {
                var relative = CodeFileWalker.ToRelative(workspaceRoot, folder.FullName);
                var child = new FolderNode(node, folder.Name, relative, node.IsVendored || IsVendoredFolder(relative, vendoredFolders));
                node.Folders.Add(child);
                pending.Enqueue((child, folder.FullName));
            }
        }

        RollUp(order);
        return new FolderScanResult(root, stopReason);
    }

    private string? StopReason(int entries, TimeSpan elapsed, TimeSpan budget)
    {
        if (entries >= options.MaxScanEntries)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the limit of {options.MaxScanEntries} scanned entries");
        }

        return elapsed > budget
            ? string.Create(CultureInfo.InvariantCulture, $"the scan time limit of {budget.TotalSeconds:0} seconds")
            : null;
    }

    // Breadth-first order puts every folder before its subfolders, so walking it backwards finishes each child before its parent.
    private void RollUp(List<FolderNode> order)
    {
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            foreach (var file in node.Files)
            {
                node.TotalFiles++;
                node.TotalBytes += file.Length;
                if (file.Length > options.LargeFileBytes)
                {
                    node.TotalLargeFiles++;
                }

                var extension = file.Extension.Length == 0 ? NoExtension : file.Extension.ToLowerInvariant();
                node.Extensions[extension] = node.Extensions.GetValueOrDefault(extension) + 1;
            }

            node.TotalFolders += node.Folders.Count;
            if (!node.Scanned || node.Unreadable)
            {
                node.Incomplete = true;
            }

            if (node.Parent is not { } parent)
            {
                continue;
            }

            parent.TotalFiles += node.TotalFiles;
            parent.TotalFolders += node.TotalFolders;
            parent.TotalBytes += node.TotalBytes;
            parent.TotalLargeFiles += node.TotalLargeFiles;
            parent.Incomplete |= node.Incomplete;
            foreach (var (extension, count) in node.Extensions)
            {
                parent.Extensions[extension] = parent.Extensions.GetValueOrDefault(extension) + count;
            }
        }
    }

    private static bool IsVendoredFolder(string relativePath, IReadOnlyList<string> vendoredFolders) =>
        relativePath.Length > 0 && ContentEligibility.IsVendored($"{relativePath}/{FolderProbeName}", vendoredFolders);
}
