using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// Draws the ASCII folder tree that <see cref="TreePlanner"/> planned. Open folders show their entries with size marks and line
/// counts, as before; closed folders show a one-line summary and the ▸ mark. Folders with many files are collapsed.
/// </summary>
internal sealed class FolderTreeRenderer(SkeletonOptions options)
{
    /// <returns>True when the tree was cut at <see cref="SkeletonOptions.MaxTreeEntries"/> entries.</returns>
    public async Task<bool> RenderAsync(FolderNode root, string rootLabel, TreePlan plan, string workspaceName, OutputBudget output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(plan);
        var state = new TreeState(output, options.MaxTreeEntries, options.LineCountReadBudgetBytes);
        if (!state.Emit($"└── {rootLabel}/"))
        {
            return state.CutByEntryLimit;
        }

        if (root.Unreadable)
        {
            state.Emit("    └── [This folder cannot be read]");
        }
        else if (plan.RootItemTotal == 0)
        {
            state.Emit("    └── (no visible files)");
        }
        else
        {
            await RenderItemsAsync(plan.RootItems, "    ", plan, workspaceName, state, ct).ConfigureAwait(false);
        }

        return state.CutByEntryLimit;
    }

    private async Task RenderItemsAsync(IReadOnlyList<TreeItem> items, string indent, TreePlan plan, string workspaceName, TreeState state, CancellationToken ct)
    {
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var isLast = i == items.Count - 1;
            var item = items[i];
            if (item.Folder is { } folder)
            {
                var (label, tail) = folder.FollowChain();
                var isOpen = plan.Open.Contains(tail);
                var line = isOpen
                    ? $"{indent}{Connector(isLast)}{label}/"
                    : $"{indent}{Connector(isLast)}{label}/ {FileFacts.DescribeFolder(tail, options)}";
                if (!state.Emit(line))
                {
                    return;
                }

                if (isOpen)
                {
                    var childIndent = indent + (isLast ? "    " : "│   ");
                    await RenderItemsAsync(TreeItems.Of(tail, options.CollapseThreshold), childIndent, plan, workspaceName, state, ct).ConfigureAwait(false);
                    if (state.Stopped)
                    {
                        return;
                    }
                }
            }
            else if (item.CollapsedFilesOf is { } holder)
            {
                var relative = holder.RelativePath.Length == 0 ? "." : holder.RelativePath;
                if (state.Emit($"{indent}└── ({holder.Files.Count} files collapsed - use code_folder_files to explore)"))
                {
                    state.Emit($"{indent}    Example: code_folder_files(\"{workspaceName}\", \"{relative}\")");
                }
            }
            else if (item.File is { } file)
            {
                var lines = await state.CountLinesAsync(file, options, ct).ConfigureAwait(false);
                if (!state.Emit($"{indent}{Connector(isLast)}{file.Name} {FileFacts.Describe(file.Length, lines, options)}"))
                {
                    return;
                }
            }
        }
    }

    private static string Connector(bool isLast) => isLast ? "└── " : "├── ";

    private sealed class TreeState(OutputBudget output, int maxEntries, long lineCountBudgetBytes)
    {
        private int _entries;
        private long _lineBytesLeft = lineCountBudgetBytes;

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

        // Line counts read the file, so one tree reads at most the line-count budget; past it, files show their size only.
        public async Task<int?> CountLinesAsync(FileInfo file, SkeletonOptions options, CancellationToken ct)
        {
            if (file.Length > options.MaxLineCountBytes || file.Length > _lineBytesLeft)
            {
                return null;
            }

            _lineBytesLeft -= file.Length;
            return await FileFacts.CountLinesAsync(file.FullName, file.Length, options.MaxLineCountBytes, ct).ConfigureAwait(false);
        }
    }
}
