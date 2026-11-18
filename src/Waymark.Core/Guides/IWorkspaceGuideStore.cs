namespace Waymark.Core.Guides;

/// <summary>The owner's text for one workspace as saved: the summary the overview lists and the instructions its guide carries.</summary>
public sealed record WorkspaceGuideRow(long WorkspaceId, string Summary, string Instructions, DateTimeOffset ChangedUtc, string ChangedBy);

/// <summary>Keeps the owner's text of each workspace. A row goes away with its workspace (the table cascades).</summary>
public interface IWorkspaceGuideStore
{
    Task<IReadOnlyList<WorkspaceGuideRow>> ListAsync(CancellationToken ct);

    /// <summary>Creates or replaces the text of one workspace. False when the workspace does not exist.</summary>
    Task<bool> UpsertAsync(long workspaceId, string summary, string instructions, string changedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Removes the text of one workspace. True when there was one.</summary>
    Task<bool> DeleteAsync(long workspaceId, CancellationToken ct);
}
