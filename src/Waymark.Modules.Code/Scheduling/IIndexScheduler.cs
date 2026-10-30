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

    /// <summary>
    /// Compares one folder of the workspace with the index (a folder that was created, moved or deleted), without walking the rest of the workspace.
    /// The path uses '/' and is workspace-relative; an empty path or "." means the whole workspace.
    /// </summary>
    void RequestFolder(long workspaceId, string relativeFolder);

    /// <summary>Queues several changed files and folders with one wake-up of the worker. Same rules as <see cref="RequestFile"/> and <see cref="RequestFolder"/>.</summary>
    void RequestChanges(long workspaceId, IReadOnlyCollection<string> files, IReadOnlyCollection<string> folders);

    /// <summary>Drops pending work of a workspace that was removed or disabled.</summary>
    void Forget(long workspaceId);
}