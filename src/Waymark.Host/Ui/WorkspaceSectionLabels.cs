using System.Globalization;
using Waymark.Core.Memory;

namespace Waymark.Host.Ui;

/// <summary>
/// The text of a workspace section tab: the title, with the count of what the section holds for Flows and Notes ('Flows (121)').
/// No count is shown until the counts are loaded, so a tab never flashes a wrong zero.
/// </summary>
public static class WorkspaceSectionLabels
{
    public static string Of(WorkspaceSection tab, MemoryCounts? counts)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return CountOf(tab, counts) is { } count
            ? string.Create(CultureInfo.InvariantCulture, $"{tab.Title} ({count:N0})")
            : tab.Title;
    }

    /// <summary>What a screen reader says: 'Flows, 121 flows'.</summary>
    public static string AriaOf(WorkspaceSection tab, MemoryCounts? counts)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return CountOf(tab, counts) is { } count
            ? string.Create(CultureInfo.InvariantCulture, $"{tab.Title}, {count:N0} {(count == 1 ? Singular(tab) : tab.Title.ToLowerInvariant())}")
            : tab.Title;
    }

    private static int? CountOf(WorkspaceSection tab, MemoryCounts? counts) =>
        counts is null ? null
        : tab.Key == WorkspaceSections.Flows.Key ? counts.Flows
        : tab.Key == WorkspaceSections.Notes.Key ? counts.Notes
        : null;

    private static string Singular(WorkspaceSection tab) => tab.Key == WorkspaceSections.Flows.Key ? "flow" : "note";
}
