namespace Waymark.Core.Memory;

/// <summary>What a use is recorded on: a flow (feature_maps) or a note.</summary>
public enum MemoryItemKind
{
    Flow,
    Note,
}

/// <summary>One use of a flow or note by a model, at <paramref name="UsedUtc"/>.</summary>
public sealed record MemoryUse(long WorkspaceId, MemoryItemKind Kind, long Id, DateTimeOffset UsedUtc);

/// <summary>The order of a list of flows or notes on the admin pages.</summary>
public enum MemoryOrder
{
    /// <summary>Last changed first (updated_utc), the order memory_recall and the guide use.</summary>
    Changed,

    /// <summary>Last used by a model first; never used last.</summary>
    Used,
}

/// <summary>
/// Records that a model opened flows or notes. <see cref="Touch"/> only remembers the use in memory and returns at once; the uses are written
/// later in one batch, so a read tool never waits for the write queue. It never throws.
/// </summary>
public interface IMemoryUsage
{
    void Touch(long workspaceId, MemoryItemKind kind, IReadOnlyCollection<long> ids);
}

/// <summary>The last-used times of flows and notes (the last_used_utc columns).</summary>
public interface IMemoryUsageStore
{
    /// <summary>
    /// Writes the uses in one transaction. A time only moves forward; a retired or replaced note, a deleted item or an item of another workspace is left alone.
    /// </summary>
    Task TouchAsync(IReadOnlyList<MemoryUse> uses, CancellationToken ct);

    /// <summary>The last-used time of each item that has one; items never used are not in the result.</summary>
    Task<IReadOnlyDictionary<long, DateTimeOffset>> GetAsync(long workspaceId, MemoryItemKind kind, IReadOnlyCollection<long> ids, CancellationToken ct);
}
