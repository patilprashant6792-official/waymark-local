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

/// <summary>The access rules of a workspace were added, changed or removed in the admin UI.</summary>
public sealed record AccessRulesChanged(long WorkspaceId);

/// <summary>The NuGet index or its stored results were reloaded, cleared or changed from the admin UI.</summary>
public sealed record NuGetCacheChanged;

public enum FileChangeKind
{
    Created,
    Modified,
    Deleted,
}

