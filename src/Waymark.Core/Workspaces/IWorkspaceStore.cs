namespace Waymark.Core.Workspaces;

public sealed record Workspace(
    long Id,
    string Name,
    string Path,
    bool Enabled,
    DateTimeOffset AddedUtc,
    DateTimeOffset? LastIndexedUtc);

public interface IWorkspaceStore
{
    /// <summary>Stores a new enabled workspace. Null when the name or the path is already registered (compared ignoring case).</summary>
    Task<Workspace?> AddAsync(string name, string path, DateTimeOffset now, CancellationToken ct);

    Task<Workspace?> GetByNameAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct);

    /// <summary>False when the workspace does not exist.</summary>
    Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct);

    /// <summary>False when the workspace does not exist.</summary>
    Task<bool> MarkIndexedAsync(long id, DateTimeOffset at, CancellationToken ct);

    /// <summary>False when the workspace does not exist.</summary>
    Task<bool> RemoveAsync(long id, CancellationToken ct);
}
