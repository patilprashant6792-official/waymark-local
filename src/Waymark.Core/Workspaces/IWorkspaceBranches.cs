namespace Waymark.Core.Workspaces;

/// <summary>
/// What is checked out in a workspace folder. <paramref name="IsGit"/> is false when the folder is not a git checkout (or .git cannot be read);
/// <paramref name="Branch"/> is null for a detached HEAD.
/// </summary>
public sealed record WorkspaceHead(bool IsGit, string? Branch)
{
    public static WorkspaceHead NotGit { get; } = new(false, null);
}

/// <summary>The head of each workspace, as last read from .git/HEAD (a background monitor reads it every few seconds and publishes BranchChanged).</summary>
public interface IWorkspaceBranches
{
    WorkspaceHead Current(Workspace workspace);
}
