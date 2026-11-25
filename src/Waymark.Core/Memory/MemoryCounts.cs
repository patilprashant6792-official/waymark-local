namespace Waymark.Core.Memory;

/// <summary>What one workspace's memory holds, from two aggregate queries. Cheap enough to load on every change.</summary>
public sealed record MemoryCounts(
    int Flows,
    int Steps,
    int UnorderedFlows,
    int FlowsNeverUsed,
    DateTimeOffset? FlowLastUsedUtc,
    long? FlowLastUsedId,
    string? FlowLastUsedName,
    int Notes,
    int Pinned,
    int FromAgents,
    int Detached,
    int NotesNeverUsed,
    DateTimeOffset? NoteLastUsedUtc);

/// <summary>Flow and note counts of one workspace, for the workspace cards.</summary>
public sealed record MemoryTotals(int Flows, int Notes);

/// <summary>Counts of flows and notes for the admin UI. Reads only.</summary>
public interface IMemoryStatsStore
{
    Task<MemoryCounts> GetAsync(long workspaceId, CancellationToken ct);

    /// <summary>Flows and active notes per workspace, for every workspace that has any.</summary>
    Task<IReadOnlyDictionary<long, MemoryTotals>> CountAllAsync(CancellationToken ct);
}
