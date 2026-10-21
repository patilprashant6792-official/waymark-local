namespace Waymark.Core.Security;

/// <summary>Persists the access rules of each workspace.</summary>
public interface IAccessRuleStore
{
    Task<IReadOnlyList<AccessRule>> ListAsync(long workspaceId, CancellationToken ct);

    Task<AccessRule?> GetAsync(long id, CancellationToken ct);

    /// <summary>Null when the workspace already has a rule of this kind and pattern.</summary>
    Task<AccessRule?> AddAsync(long workspaceId, AccessRuleKind kind, string pattern, string note, DateTimeOffset now, CancellationToken ct);

    /// <summary>False when no rule has this id.</summary>
    Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct);

    /// <summary>False when no rule has this id.</summary>
    Task<bool> RemoveAsync(long id, CancellationToken ct);
}
