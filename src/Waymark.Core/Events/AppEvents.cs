using Waymark.Core.Approvals;
using Waymark.Core.Edge;
using Waymark.Core.Workspaces;

namespace Waymark.Core.Events;

/// <summary>A new pairing is waiting for a decision in the admin UI.</summary>
public sealed record ApprovalRequested(long ApprovalId);

public sealed record ApprovalDecided(long ApprovalId, ApprovalStatus Status);

/// <summary>The public URL was saved (or cleared).</summary>
public sealed record PublicUrlChanged(string? Url);

/// <summary>A client, token family or API key was revoked; cached credentials must be dropped.</summary>
public sealed record CredentialsRevoked;

/// <summary>The managed ngrok tunnel changed state (installing, starting, online, failed).</summary>
public sealed record TunnelStatusChanged(TunnelStatus Status);
/// <summary>A workspace was added, removed, enabled or disabled.</summary>
public sealed record WorkspaceChanged(long WorkspaceId, string Name, WorkspaceChangeKind Kind);

/// <summary>A tool created, changed or deleted a file inside a workspace. RelativePath uses '/'.</summary>
public sealed record FileChanged(long WorkspaceId, string RelativePath, FileChangeKind Kind);

/// <summary>A tool created, moved or deleted a whole folder inside a workspace. RelativePath uses '/'.</summary>
public sealed record FolderChanged(long WorkspaceId, string RelativePath, FileChangeKind Kind);

/// <summary>
/// Files of a workspace changed on disk, whoever changed them (an editor, git, another agent, Waymark's own tools), after the code index took them in.
/// <paramref name="Paths"/> are relative with '/'. <paramref name="Whole"/> means too many changed to list (a reconcile, a large checkout): treat the whole workspace as changed.
/// </summary>
public sealed record CodeFilesChanged(long WorkspaceId, IReadOnlyList<string> Paths, bool Whole);

/// <summary>The branch checked out in a workspace changed (or the folder became, or stopped being, a git checkout). Null means no branch: not git, or a detached HEAD.</summary>
public sealed record BranchChanged(long WorkspaceId, string? From, string? To);

/// <summary>The memory repair queue of a workspace changed: items opened, resolved or fixed automatically. <paramref name="Seq"/> rises whenever an item is opened.</summary>
public sealed record MemoryRepairsChanged(long WorkspaceId, int Open, long Seq);

/// <summary>The access rules of a workspace were added, changed or removed in the admin UI.</summary>
public sealed record AccessRulesChanged(long WorkspaceId);

/// <summary>A feature map of a workspace was saved, changed or deleted, by Claude through memory_save or by the owner in the admin UI.</summary>
public sealed record FeatureMapsChanged(long WorkspaceId);

/// <summary>The owner's summary or instructions of a workspace were saved or cleared, on its Instructions page or by an import.</summary>
public sealed record WorkspaceGuideChanged(long WorkspaceId);
public enum FlowChange
{
    /// <summary>A flow was created or its steps were replaced.</summary>
    Saved,

    /// <summary>A flow was deleted.</summary>
    Deleted,

    /// <summary>An import added flows; the flow id is 0 because it is more than one.</summary>
    Imported,
}

/// <summary>One flow of a workspace changed (see <see cref="FlowChange"/>). The pages listen to <see cref="FeatureMapsChanged"/>; this one carries the id for what indexes flows.</summary>
public sealed record FlowChanged(long WorkspaceId, long FlowId, FlowChange Change);

public enum NoteChange
{
    /// <summary>A note was added, revised, confirmed, pinned or unpinned.</summary>
    Saved,

    /// <summary>A note was retired.</summary>
    Retired,

    /// <summary>An import added notes; the note id is 0 because it is more than one.</summary>
    Imported,
}

/// <summary>A note of a workspace changed, by Claude through memory_save, by the owner in the admin UI or by an import.</summary>
public sealed record NoteChanged(long WorkspaceId, long NoteId, NoteChange Change);

/// <summary>Models' uses of flows and notes were written (their last-used times changed) for these workspaces. At most one per flush, every few seconds.</summary>
public sealed record MemoryUsageRecorded(IReadOnlyCollection<long> WorkspaceIds);

/// <summary>The search-by-meaning settings changed: the switch, the provider or model, a key, or a workspace's exclusion.</summary>
public sealed record MemorySettingsChanged;

/// <summary>The embedding index changed state or counts (for the Setup page).</summary>
public sealed record MemoryIndexStatusChanged;
/// <summary>The NuGet index or its stored results were reloaded, cleared or changed from the admin UI.</summary>
public sealed record NuGetCacheChanged;

public enum FileChangeKind
{
    Created,
    Modified,
    Deleted,
}

