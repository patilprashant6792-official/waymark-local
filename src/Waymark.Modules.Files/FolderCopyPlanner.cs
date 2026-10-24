using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

/// <summary>One file of a folder copy: where it is read and where it goes (both workspace-relative, with '/').</summary>
internal sealed record FolderCopyItem(string SourceFile, string TargetFile);

/// <summary>What a folder copy would do: the files in a stable order and how many items the access rules or links kept out.</summary>
internal sealed record FolderCopyPlan(Workspace SourceWorkspace, string SourceFolder, string TargetFolder, List<FolderCopyItem> Items, int SkippedItems);

/// <summary>Finds the files of a folder copy. Nothing is read or written here; blocked folders, links and files the access rules refuse are left out and counted.</summary>
internal sealed class FolderCopyPlanner(IPathGuard guard, CopySourceReader reader, FilesOptions options)
{
    // A folder cannot be resolved on its own through every rule set, so a file name inside it is checked instead.
    private const string FolderProbe = "waymark-folder-probe";

    /// <exception cref="PatchValidationException">The request is incomplete, too large, or two files would land on the same path.</exception>
    /// <exception cref="FileAccessDeniedException">The source folder itself is refused.</exception>
    public async Task<FolderCopyPlan> PlanAsync(Workspace callWorkspace, FolderCopy copy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(copy.Folder))
        {
            throw new PatchValidationException("fromFolder.folder is required.");
        }

        if (copy.ToFolder is null)
        {
            throw new PatchValidationException("fromFolder.toFolder is required (use \".\" for the workspace root).");
        }

        var sourceWorkspace = await reader.ResolveWorkspaceAsync(callWorkspace, copy.Workspace, ct).ConfigureAwait(false);
        var folder = guard.Resolve(sourceWorkspace, copy.Folder);
        if (!Directory.Exists(folder.AbsolutePath))
        {
            throw new PatchValidationException($"fromFolder.folder '{copy.Folder}' is not a folder in workspace {sourceWorkspace.Name}.");
        }

        LinkGuard.EnsureNoLinks(sourceWorkspace.Path, folder.RelativePath);
        var glob = ParseGlob(copy.Glob);
        var renames = ValidateRenames(copy.PathRename);
        var targetFolder = NormalizeFolder(copy.ToFolder);
        var (found, skipped) = Walk(sourceWorkspace, folder, glob, ct);
        var items = new List<FolderCopyItem>(found.Count);
        var targets = new Dictionary<string, string>(CopySession.Comparer);
        foreach (var (file, inFolder) in found)
        {
            var target = Join(targetFolder, ApplyRenames(inFolder, renames));
            if (targets.TryGetValue(target, out var other))
            {
                throw new PatchValidationException($"Two source files would be written to {target}: {other} and {file}. Change pathRename.");
            }

            targets[target] = file;
            items.Add(new FolderCopyItem(file, target));
        }

        return new FolderCopyPlan(sourceWorkspace, folder.RelativePath, targetFolder, items, skipped);
    }

    private (List<(string File, string InFolder)> Files, int Skipped) Walk(Workspace workspace, GuardedPath folder, PathGlob? glob, CancellationToken ct)
    {
        var files = new List<(string File, string InFolder)>();
        var skipped = 0;
        var visited = 0;
        var pending = new Stack<string>();
        pending.Push(folder.AbsolutePath);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(pending.Pop());
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++visited > options.MaxFolderEntries)
                {
                    throw new PatchValidationException($"The folder has more than {options.MaxFolderEntries} entries; copy a narrower folder.");
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    continue;
                }

                var relative = Path.GetRelativePath(workspace.Path, entry.FullName).Replace('\\', '/');
                var isFolder = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (!IsAllowed(workspace, isFolder ? $"{relative}/{FolderProbe}" : relative))
                {
                    skipped++;
                    continue;
                }

                if (isFolder)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                var inFolder = folder.RelativePath.Length == 0 ? relative : relative[(folder.RelativePath.Length + 1)..];
                if (glob is not null && !glob.IsMatch(inFolder))
                {
                    continue;
                }

                files.Add((relative, inFolder));
                if (files.Count > options.MaxCopyFilesPerCall)
                {
                    throw new PatchValidationException($"More than {options.MaxCopyFilesPerCall} files match; narrow the folder or the glob and copy in several calls.");
                }
            }
        }

        files.Sort(static (a, b) =>
        {
            var order = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            return order != 0 ? order : string.CompareOrdinal(a.File, b.File);
        });
        return (files, skipped);
    }

    private bool IsAllowed(Workspace workspace, string relativePath)
    {
        try
        {
            guard.Resolve(workspace, relativePath);
            return true;
        }
        catch (FileAccessDeniedException)
        {
            return false;
        }
    }

    private static PathGlob? ParseGlob(string? pattern)
    {
        try
        {
            return PathGlob.Parse(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new PatchValidationException($"fromFolder.glob: {ex.Message}");
        }
    }

    private static List<(string Find, string With)> ValidateRenames(List<PathRename>? renames)
    {
        var rules = new List<(string Find, string With)>();
        foreach (var rename in renames ?? [])
        {
            if (rename is null || string.IsNullOrEmpty(rename.Find) || rename.With is null)
            {
                throw new PatchValidationException("Every pathRename needs find (not empty) and with.");
            }

            rules.Add((rename.Find.Replace('\\', '/'), rename.With.Replace('\\', '/')));
        }

        return rules;
    }

    private static string ApplyRenames(string path, List<(string Find, string With)> renames)
    {
        var current = path;
        foreach (var (find, with) in renames)
        {
            current = current.Replace(find, with, StringComparison.Ordinal);
        }

        var segments = current.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == "..") || current.Contains(':'))
        {
            throw new PatchValidationException($"pathRename turns '{path}' into '{current}', which is not a valid path below toFolder.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeFolder(string folder)
    {
        var value = folder.Trim().Replace('\\', '/').Trim('/');
        return value == "." ? string.Empty : value;
    }

    private static string Join(string folder, string file) => folder.Length == 0 ? file : $"{folder}/{file}";
}
