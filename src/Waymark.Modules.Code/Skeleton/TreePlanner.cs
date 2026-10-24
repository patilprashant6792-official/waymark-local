using System.Globalization;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>One line under an open folder: a subfolder (shown with its chain), a file, or the note for a folder whose many files are collapsed.</summary>
internal readonly record struct TreeItem(FolderNode? Folder, FileInfo? File, FolderNode? CollapsedFilesOf);

internal static class TreeItems
{
    /// <summary>The lines an open folder shows: its subfolders, then its files, or one note when it holds more than the collapse threshold.</summary>
    public static List<TreeItem> Of(FolderNode folder, int collapseThreshold)
    {
        var items = new List<TreeItem>(folder.Folders.Count + Math.Min(folder.Files.Count, collapseThreshold + 1));
        foreach (var child in folder.Folders)
        {
            items.Add(new TreeItem(child, null, null));
        }

        if (folder.Files.Count > collapseThreshold)
        {
            items.Add(new TreeItem(null, null, folder));
            return items;
        }

        foreach (var file in folder.Files)
        {
            items.Add(new TreeItem(null, file, null));
        }

        return items;
    }

    public static int CountOf(FolderNode folder, int collapseThreshold) =>
        folder.Folders.Count + (folder.Files.Count > collapseThreshold ? 1 : folder.Files.Count);
}

/// <param name="BudgetChars">Characters the tree may use.</param>
/// <param name="MaxLevels">Levels below the viewed folder that may be shown.</param>
/// <param name="Overview">No depth was asked for: vendored folders and folders with too many entries stay closed.</param>
/// <param name="Page">Page of the viewed folder's own entries.</param>
internal sealed record PlanSettings(int BudgetChars, int MaxLevels, bool Overview, int Page);

internal sealed class TreePlan
{
    /// <summary>The viewed folder's entries on the requested page.</summary>
    public required IReadOnlyList<TreeItem> RootItems { get; init; }

    public required int RootItemTotal { get; init; }

    public required int Page { get; init; }

    public required int PageCount { get; init; }

    public required int PageSize { get; init; }

    /// <summary>Open folders, by the last folder of their chain.</summary>
    public required IReadOnlySet<FolderNode> Open { get; init; }

    /// <summary>Folders shown closed that hold something, by the last folder of their chain, in tree order.</summary>
    public required IReadOnlyList<FolderNode> Closed { get; init; }

    /// <summary>The budget ran out before every level allowed by the depth was opened.</summary>
    public required bool CutByBudget { get; init; }
}

/// <summary>
/// Decides which folders the tree opens. Levels open breadth-first: a level opens only when the whole level above it is open, so the
/// budget never buys detail in one corner while a shallower folder elsewhere stays unseen. When a whole level does not fit, its
/// cheapest folders open first and planning stops there. Costs are estimated from the same text the renderer writes.
/// Pure: no file system access and no clock.
/// </summary>
internal static class TreePlanner
{
    private const int IndentWidth = 4;
    private const int ConnectorWidth = 4;
    private const int EstimatedLines = 99_999;
    private const int CollapsedFilesNoteChars = 130;

    /// <exception cref="ArgumentException">The page is past the last page.</exception>
    public static TreePlan Plan(FolderNode root, string rootLabel, PlanSettings settings, SkeletonOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        var all = TreeItems.Of(root, options.CollapseThreshold);
        var pageSize = options.MaxChildrenShown;
        var pageCount = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        if (settings.Page > pageCount)
        {
            // No parameter name: the message goes to the AI client as is, and '(Parameter ...)' would only add noise.
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"page {settings.Page} is past the last page ({pageCount}) of '{rootLabel}'."));
        }

        var shown = all.Skip((settings.Page - 1) * pageSize).Take(pageSize).ToList();
        var open = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
        long used = rootLabel.Length + IndentWidth + ConnectorWidth + 2;
        foreach (var item in shown)
        {
            used += ItemCost(item, 1, options);
        }

        var frontier = FolderTails(shown);
        var level = 1;
        var cut = false;
        while (frontier.Count > 0 && level < settings.MaxLevels && !cut)
        {
            var candidates = frontier
                .Where(tail => CanOpen(tail, settings, root.IsVendored, options))
                .Select(tail => (Tail: tail, Cost: OpenCost(tail, level, options)))
                .ToList();
            var total = candidates.Sum(candidate => candidate.Cost);
            var chosen = new HashSet<FolderNode>(ReferenceEqualityComparer.Instance);
            if (used + total <= settings.BudgetChars)
            {
                chosen.UnionWith(candidates.Select(candidate => candidate.Tail));
                used += total;
            }
            else
            {
                cut = true;
                foreach (var (tail, cost) in candidates
                    .OrderBy(candidate => candidate.Cost)
                    .ThenBy(candidate => candidate.Tail.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.Tail.RelativePath, StringComparer.Ordinal))
                {
                    if (used + cost <= settings.BudgetChars)
                    {
                        chosen.Add(tail);
                        used += cost;
                    }
                }
            }

            var next = new List<FolderNode>();
            foreach (var tail in frontier.Where(chosen.Contains))
            {
                open.Add(tail);
                next.AddRange(tail.Folders.Select(child => child.FollowChain().Tail));
            }

            frontier = next;
            level++;
        }

        var closed = new List<FolderNode>();
        CollectClosed(shown, open, options, closed);
        return new TreePlan
        {
            RootItems = shown,
            RootItemTotal = all.Count,
            Page = settings.Page,
            PageCount = pageCount,
            PageSize = pageSize,
            Open = open,
            Closed = closed,
            CutByBudget = cut,
        };
    }

    private static List<FolderNode> FolderTails(IEnumerable<TreeItem> items) =>
        items.Where(item => item.Folder is not null).Select(item => item.Folder!.FollowChain().Tail).ToList();

    private static bool CanOpen(FolderNode tail, PlanSettings settings, bool rootIsVendored, SkeletonOptions options)
    {
        if (!tail.Scanned || tail.Unreadable || !tail.HasEntries)
        {
            return false;
        }

        // The overview keeps library code and very wide folders closed; an explicit depth shows them like any other folder.
        return !settings.Overview
            || ((!tail.IsVendored || rootIsVendored) && TreeItems.CountOf(tail, options.CollapseThreshold) <= options.MaxChildrenShown);
    }

    // Opening a folder adds its entries one level deeper and drops the summary from its own line.
    private static long OpenCost(FolderNode tail, int level, SkeletonOptions options)
    {
        long cost = 0;
        foreach (var item in TreeItems.Of(tail, options.CollapseThreshold))
        {
            cost += ItemCost(item, level + 1, options);
        }

        return cost - (FileFacts.DescribeFolder(tail, options).Length + 1);
    }

    private static long ItemCost(TreeItem item, int level, SkeletonOptions options)
    {
        var prefix = (IndentWidth * level) + ConnectorWidth;
        if (item.Folder is { } folder)
        {
            var (label, tail) = folder.FollowChain();
            return prefix + label.Length + 2 + FileFacts.DescribeFolder(tail, options).Length + 1;
        }

        if (item.File is { } file)
        {
            return prefix + file.Name.Length + 1 + FileFacts.Describe(file.Length, EstimatedLines, options).Length + 1;
        }

        return (2 * prefix) + CollapsedFilesNoteChars + item.CollapsedFilesOf!.RelativePath.Length;
    }

    private static void CollectClosed(IEnumerable<TreeItem> items, HashSet<FolderNode> open, SkeletonOptions options, List<FolderNode> closed)
    {
        foreach (var item in items)
        {
            if (item.Folder is null)
            {
                continue;
            }

            var tail = item.Folder.FollowChain().Tail;
            if (open.Contains(tail))
            {
                CollectClosed(TreeItems.Of(tail, options.CollapseThreshold), open, options, closed);
            }
            else if (!tail.Unreadable && (tail.HasEntries || !tail.Scanned))
            {
                closed.Add(tail);
            }
        }
    }
}
