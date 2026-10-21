namespace Waymark.Core.Workspaces;

/// <summary>Adds, validates, enables and removes workspaces and announces each change on the event bus.</summary>
public interface IWorkspaceService
{
    /// <summary>Checks that the folder exists and holds a solution or project. Changes nothing.</summary>
    Task<WorkspaceValidation> ValidateAsync(string? path, CancellationToken ct);

    /// <summary>Validates the name and folder, stores the workspace and publishes WorkspaceChanged.</summary>
    Task<WorkspaceAddResult> AddAsync(string? name, string? path, CancellationToken ct);

    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct);

    /// <summary>False when the workspace does not exist.</summary>
    Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct);

    /// <summary>False when the workspace does not exist.</summary>
    Task<bool> RemoveAsync(long id, CancellationToken ct);
}
