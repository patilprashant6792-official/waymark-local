using MudBlazor;

namespace Waymark.Host.Ui;

/// <summary>One page of a workspace's details: its address below /workspaces/{id} and how it shows in the section menu.</summary>
public sealed record WorkspaceSection(string Key, string Title, string Icon, string Segment)
{
    public string Href(long workspaceId) =>
        Segment.Length == 0 ? $"/workspaces/{workspaceId}" : $"/workspaces/{workspaceId}/{Segment}";
}

/// <summary>
/// The sections of a workspace's details, in menu order. A new workspace-level setting adds one entry here
/// and one page that wraps its content in WorkspaceShell.
/// </summary>
public static class WorkspaceSections
{
    public static readonly WorkspaceSection Overview = new("overview", "Overview", Icons.Material.Filled.Dashboard, string.Empty);
    public static readonly WorkspaceSection Access = new("access", "Access rules", Icons.Material.Filled.Lock, "access");
    public static readonly WorkspaceSection Commands = new("commands", "Commands", Icons.Material.Filled.Tune, "commands");

    public static IReadOnlyList<WorkspaceSection> All { get; } = [Overview, Access, Commands];
}
