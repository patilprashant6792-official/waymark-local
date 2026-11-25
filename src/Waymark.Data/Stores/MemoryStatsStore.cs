using Dapper;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>Aggregate counts of flows and notes for the admin UI: a handful of indexed queries, no file is read.</summary>
public sealed class MemoryStatsStore(ISqliteConnectionFactory connections) : IMemoryStatsStore
{
    private const string ActiveNote = "n.retired_utc IS NULL AND n.superseded_utc IS NULL";

    public async Task<MemoryCounts> GetAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var flows = await connection.QuerySingleAsync<FlowRow>(new CommandDefinition(
            "SELECT COUNT(*) AS total, COALESCE(SUM(m.ordered = 0), 0) AS unordered, COALESCE(SUM(m.last_used_utc IS NULL), 0) AS never_used, " +
            "(SELECT COUNT(*) FROM feature_components c JOIN feature_maps m2 ON m2.id = c.map_id WHERE m2.workspace_id = @workspaceId) AS steps " +
            "FROM feature_maps m WHERE m.workspace_id = @workspaceId",
            new { workspaceId },
            cancellationToken: ct));
        var last = await connection.QuerySingleOrDefaultAsync<LastRow>(new CommandDefinition(
            "SELECT m.id, m.name, m.last_used_utc FROM feature_maps m WHERE m.workspace_id = @workspaceId AND m.last_used_utc IS NOT NULL " +
            "ORDER BY m.last_used_utc DESC, m.id DESC LIMIT 1",
            new { workspaceId },
            cancellationToken: ct));
        var notes = await connection.QuerySingleAsync<NoteRow>(new CommandDefinition(
            "SELECT COUNT(*) AS total, COALESCE(SUM(n.pinned), 0) AS pinned, COALESCE(SUM(n.author_kind <> 'human'), 0) AS from_agents, " +
            "COALESCE(SUM(n.map_id IS NULL AND n.detached_from IS NOT NULL), 0) AS detached, COALESCE(SUM(n.last_used_utc IS NULL), 0) AS never_used, " +
            "MAX(n.last_used_utc) AS last_used_utc " +
            $"FROM notes n WHERE n.workspace_id = @workspaceId AND {ActiveNote}",
            new { workspaceId },
            cancellationToken: ct));
        return new MemoryCounts(
            (int)flows.Total,
            (int)flows.Steps,
            (int)flows.Unordered,
            (int)flows.NeverUsed,
            last is null ? null : Timestamp.Parse(last.LastUsedUtc),
            last?.Id,
            last?.Name,
            (int)notes.Total,
            (int)notes.Pinned,
            (int)notes.FromAgents,
            (int)notes.Detached,
            (int)notes.NeverUsed,
            Timestamp.ParseNullable(notes.LastUsedUtc));
    }

    public async Task<IReadOnlyDictionary<long, MemoryTotals>> CountAllAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var flows = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            "SELECT m.workspace_id, COUNT(*) AS total FROM feature_maps m GROUP BY m.workspace_id", cancellationToken: ct)))
            .ToDictionary(row => row.WorkspaceId, row => (int)row.Total);
        var notes = (await connection.QueryAsync<CountRow>(new CommandDefinition(
            $"SELECT n.workspace_id, COUNT(*) AS total FROM notes n WHERE {ActiveNote} GROUP BY n.workspace_id", cancellationToken: ct)))
            .ToDictionary(row => row.WorkspaceId, row => (int)row.Total);
        return flows.Keys.Union(notes.Keys).ToDictionary(
            id => id,
            id => new MemoryTotals(flows.GetValueOrDefault(id), notes.GetValueOrDefault(id)));
    }

    private sealed class FlowRow
    {
        public long Total { get; set; }

        public long Unordered { get; set; }

        public long NeverUsed { get; set; }

        public long Steps { get; set; }
    }

    private sealed class LastRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string LastUsedUtc { get; set; } = string.Empty;
    }

    private sealed class NoteRow
    {
        public long Total { get; set; }

        public long Pinned { get; set; }

        public long FromAgents { get; set; }

        public long Detached { get; set; }

        public long NeverUsed { get; set; }

        public string? LastUsedUtc { get; set; }
    }

    private sealed class CountRow
    {
        public long WorkspaceId { get; set; }

        public long Total { get; set; }
    }
}
