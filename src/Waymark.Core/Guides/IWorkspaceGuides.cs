using Waymark.Core.Workspaces;

namespace Waymark.Core.Guides;

/// <summary>The owner's text for one workspace. <see cref="ChangedUtc"/> and <see cref="ChangedBy"/> are null while the owner has written nothing.</summary>
public sealed record WorkspaceGuideText(string Summary, string Instructions, DateTimeOffset? ChangedUtc, string? ChangedBy)
{
    public static WorkspaceGuideText Empty { get; } = new(string.Empty, string.Empty, null, null);

    public bool HasInstructions => Instructions.Length > 0;

    public bool IsEmpty => Summary.Length == 0 && Instructions.Length == 0;
}

/// <summary>
/// The owner's summary and instructions of each workspace, kept in memory after the first read. This is their only writer, so the copy stays
/// current and the call pipeline can ask for them on every call without touching the database.
/// </summary>
public interface IWorkspaceGuides
{
    /// <summary>The text of one workspace; <see cref="WorkspaceGuideText.Empty"/> when the owner wrote nothing.</summary>
    Task<WorkspaceGuideText> GetAsync(long workspaceId, CancellationToken ct);

    /// <summary>Every workspace that has text, by workspace id.</summary>
    Task<IReadOnlyDictionary<long, WorkspaceGuideText>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Saves the text of one workspace; both empty removes it. Throws <see cref="ArgumentException"/> for text over a limit or text that holds a secret,
    /// and <see cref="KeyNotFoundException"/> when the workspace no longer exists.
    /// </summary>
    Task SaveAsync(long workspaceId, string summary, string instructions, string changedBy, CancellationToken ct);

    /// <summary>Drops what is kept for a workspace that was removed.</summary>
    void Forget(long workspaceId);
}

/// <summary>What is live in one workspace right now (flows, its own command gates), written for an agent. It is filled into the workspace guide when it is sent.</summary>
public interface IWorkspaceSetup
{
    Task<string> DescribeAsync(Workspace workspace, CancellationToken ct);
}
