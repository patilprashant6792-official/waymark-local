namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// Queues index work. Requests return at once and are coalesced per workspace; one background loop does the work,
/// one workspace at a time, so a burst of file events never runs two parses of the same file.
/// </summary>
public interface IIndexScheduler
{
    /// <summary>Compares the disk with the index and parses only files whose size or time changed. Removes files that are gone.</summary>
    void RequestReconcile(long workspaceId);

    /// <summary>Forgets the whole index of the workspace first, then rebuilds it. Used by the re-index button.</summary>
    void RequestRebuild(long workspaceId);

    /// <summary>Re-reads one file (deleted, created or changed is decided from the disk). The path uses '/' and is workspace-relative.</summary>
    void RequestFile(long workspaceId, string relativePath);

    /// <summary>Drops pending work of a workspace that was removed or disabled.</summary>
    void Forget(long workspaceId);
}
