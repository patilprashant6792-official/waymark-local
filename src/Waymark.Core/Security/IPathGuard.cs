using Waymark.Core.Workspaces;

namespace Waymark.Core.Security;

/// <summary>A path that passed the guard. RelativePath uses '/' and is empty for the workspace root.</summary>
public sealed record GuardedPath(Workspace Workspace, string RelativePath, string AbsolutePath);

/// <summary>The single gate every tool passes a path through before touching the disk.</summary>
public interface IPathGuard
{
    /// <summary>Finds an enabled workspace by name. '*' is refused: use it only in tools that loop over workspaces themselves.</summary>
    /// <exception cref="ArgumentException">The name is empty or '*'.</exception>
    /// <exception cref="KeyNotFoundException">No workspace has that name.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<Workspace> GetWorkspaceAsync(string? workspaceName, CancellationToken ct);

    /// <summary>Resolves a workspace-relative path to a canonical absolute path inside the workspace.</summary>
    /// <exception cref="FileAccessDeniedException">Outside the workspace, through a link that leaves it, or refused by the policy.</exception>
    GuardedPath Resolve(Workspace workspace, string? relativePath);

    /// <summary>GetWorkspaceAsync followed by Resolve.</summary>
    Task<GuardedPath> ResolveAsync(string? workspaceName, string? relativePath, CancellationToken ct);
}
