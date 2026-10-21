using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Draws the ASCII folder tree with size marks and line counts. Folders with many files are collapsed.</summary>
internal sealed class FolderTreeRenderer(FileAccessPolicy policy, SkeletonOptions options)
{
    /// <returns>True when the tree was cut at <see cref="SkeletonOptions.MaxTreeEntries"/> entries.</returns>
    public async Task<bool> RenderAsync(string root, string workspaceName, OutputBudget output, CancellationToken ct)
    {
        var state = new TreeState(output, options.MaxTreeEntries);
        if (state.Emit($"└── {Path.GetFileName(root)}/"))
        {
            await RenderChildrenAsync(root, root, "    ", workspaceName, state, ct).ConfigureAwait(false);
        }

        return state.CutByEntryLimit;
    }

    private async Task RenderChildrenAsync(string root, string current, string indent, string workspaceName, TreeState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<DirectoryInfo> folders;
        List<FileInfo> files;
        try
        {
            folders = VisibleEntries.Folders(root, current, policy);
            files = VisibleEntries.Files(root, current, policy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.Emit($"{indent}└── [This folder cannot be read]");
            return;
        }

        var collapse = files.Count > options.CollapseThreshold;
        var total = folders.Count + (collapse ? 1 : files.Count);
        var position = 0;

        foreach (var folder in folders)
        {
            position++;
            var isLast = position == total;
            if (!state.Emit($"{indent}{Connector(isLast)}{folder.Name}/"))
            {
                return;
            }

            await RenderChildrenAsync(root, folder.FullName, indent + (isLast ? "    " : "│   "), workspaceName, state, ct).ConfigureAwait(false);
            if (state.Stopped)
            {
                return;
            }
        }

        if (collapse)
        {
            var relative = CodeFileWalker.ToRelative(root, current);
            if (state.Emit($"{indent}└── ({files.Count} files collapsed - use code_folder_files to explore)"))
            {
                state.Emit($"{indent}    Example: code_folder_files(\"{workspaceName}\", \"{relative}\")");
            }

            return;
        }

        foreach (var file in files)
        {
            position++;
            var lines = await FileFacts.CountLinesAsync(file.FullName, file.Length, options.MaxLineCountBytes, ct).ConfigureAwait(false);
            if (!state.Emit($"{indent}{Connector(position == total)}{file.Name} {FileFacts.Describe(file.Length, lines, options)}"))
            {
                return;
            }
        }
    }

    private static string Connector(bool isLast) => isLast ? "└── " : "├── ";

    private sealed class TreeState(OutputBudget output, int maxEntries)
    {
        private int _entries;

        public bool Stopped { get; private set; }

        public bool CutByEntryLimit { get; private set; }

        public bool Emit(string line)
        {
            if (Stopped)
            {
                return false;
            }

            if (++_entries > maxEntries)
            {
                CutByEntryLimit = true;
                Stopped = true;
                return false;
            }

            if (!output.TryAppendLine(line))
            {
                Stopped = true;
                return false;
            }

            return true;
        }
    }
}
