using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>
/// The memory_repairs queue and the per-workspace memory_repair_state row. Every write is one job on the write queue, so a sync, an automatic fix and a
/// resolve never interleave. An open item is unique per (workspace, kind, target, step) through a partial unique index; finding it again only refreshes it.
/// </summary>
public sealed class MemoryRepairStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IMemoryRepairStore
{
    private const int MaxPage = 200;

    private const string Columns =
        "id, workspace_id, kind, target_kind, target_id, target_name, step_ordinal, step_spec, path, detail, suggestion, owner_only, state, seq, " +
        "first_seen_utc, last_seen_utc, resolved_utc, resolved_by, undo_json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<RepairSyncResult> SyncAsync(
        long workspaceId,
        IReadOnlyCollection<RepairScope> scopes,
        IReadOnlyList<RepairFinding> found,
        string resolvedBy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(found);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<RepairSyncResult>(async (connection, tx, k) =>
        {
            // The open items the check covered, by their identity.
            var inScope = new Dictionary<string, RepairRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in scopes.Distinct())
            {
                var rows = await connection.QueryAsync<RepairRow>(new CommandDefinition(
                    $"SELECT {Columns} FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'open' AND target_kind = @target AND target_id = @targetId " +
                    "AND (@spec IS NULL OR step_spec = @spec)",
                    new { workspaceId, target = MemoryRepairText.Target(scope.Target), targetId = scope.TargetId, spec = scope.StepSpec },
                    tx,
                    cancellationToken: k));
                foreach (var row in rows)
                {
                    inScope[Key(row.Kind, row.TargetKind, row.TargetId, row.StepSpec)] = row;
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var opened = 0;
            long? seq = null;
            foreach (var finding in found)
            {
                var key = Key(MemoryRepairText.Kind(finding.Kind), MemoryRepairText.Target(finding.Target), finding.TargetId, finding.StepSpec);
                if (!seen.Add(key))
                {
                    continue;
                }

                var known = inScope.ContainsKey(key) || await ExistsOpenAsync(connection, tx, workspaceId, finding, k);
                if (!known)
                {
                    seq ??= await NextSeqAsync(connection, tx, workspaceId, k);
                    opened++;
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO memory_repairs (workspace_id, kind, target_kind, target_id, target_name, step_ordinal, step_spec, path, detail, suggestion, owner_only, state, seq, first_seen_utc, last_seen_utc) " +
                    "VALUES (@workspaceId, @kind, @target, @targetId, @targetName, @ordinal, @spec, @path, @detail, @suggestion, @ownerOnly, 'open', @seq, @stamp, @stamp) " +
                    "ON CONFLICT (workspace_id, kind, target_kind, target_id, step_spec) WHERE state = 'open' DO UPDATE SET " +
                    "target_name = excluded.target_name, step_ordinal = excluded.step_ordinal, path = excluded.path, detail = excluded.detail, " +
                    "suggestion = excluded.suggestion, owner_only = excluded.owner_only, last_seen_utc = excluded.last_seen_utc",
                    new
                    {
                        workspaceId,
                        kind = MemoryRepairText.Kind(finding.Kind),
                        target = MemoryRepairText.Target(finding.Target),
                        targetId = finding.TargetId,
                        targetName = Cap(finding.TargetName, 200),
                        ordinal = finding.StepOrdinal,
                        spec = finding.StepSpec,
                        path = finding.Path,
                        detail = Cap(finding.Detail, 600),
                        suggestion = finding.Suggestion is null ? null : Cap(finding.Suggestion, 600),
                        ownerOnly = finding.OwnerOnly ? 1 : 0,
                        seq = seq ?? 0,
                        stamp,
                    },
                    tx,
                    cancellationToken: k));
            }

            var resolved = 0;
            foreach (var (key, row) in inScope)
            {
                if (seen.Contains(key))
                {
                    continue;
                }

                resolved += await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE memory_repairs SET state = 'resolved', resolved_utc = @stamp, resolved_by = @resolvedBy WHERE id = @id AND state = 'open'",
                    new { id = row.Id, stamp, resolvedBy = Cap(resolvedBy, 120) },
                    tx,
                    cancellationToken: k));
            }

            var open = await CountOpenAsync(connection, tx, workspaceId, k);
            var current = seq ?? await CurrentSeqAsync(connection, tx, workspaceId, k);
            return new RepairSyncResult(opened, resolved, open, current);
        }, ct);
    }

    public Task<int> ResolveTargetAsync(long workspaceId, MemoryRepairTarget target, long targetId, string resolvedBy, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE memory_repairs SET state = 'resolved', resolved_utc = @stamp, resolved_by = @resolvedBy " +
                "WHERE workspace_id = @workspaceId AND state = 'open' AND target_kind = @target AND target_id = @targetId",
                new { workspaceId, target = MemoryRepairText.Target(target), targetId, stamp, resolvedBy = Cap(resolvedBy, 120) },
                tx,
                cancellationToken: k)), ct);
    }

    public Task<long> RecordAutoFixAsync(long workspaceId, RepairFinding finding, AutoFixUndo undo, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(undo);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<long>(async (connection, tx, k) =>
        {
            // The open item for the old step, if any, is done: the fix is the answer to it.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE memory_repairs SET state = 'resolved', resolved_utc = @stamp, resolved_by = 'waymark (auto-fix)' " +
                "WHERE workspace_id = @workspaceId AND state = 'open' AND target_kind = 'flow' AND target_id = @targetId AND step_spec = @spec",
                new { workspaceId, targetId = finding.TargetId, spec = undo.OldSpec, stamp },
                tx,
                cancellationToken: k));
            var seq = await CurrentSeqAsync(connection, tx, workspaceId, k);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO memory_repairs (workspace_id, kind, target_kind, target_id, target_name, step_ordinal, step_spec, path, detail, suggestion, owner_only, state, seq, " +
                "first_seen_utc, last_seen_utc, resolved_utc, resolved_by, undo_json) " +
                "VALUES (@workspaceId, @kind, @target, @targetId, @targetName, @ordinal, @spec, @path, @detail, @suggestion, 0, 'auto_fixed', @seq, @stamp, @stamp, @stamp, 'waymark (auto-fix)', @undo)",
                new
                {
                    workspaceId,
                    kind = MemoryRepairText.Kind(finding.Kind),
                    target = MemoryRepairText.Target(finding.Target),
                    targetId = finding.TargetId,
                    targetName = Cap(finding.TargetName, 200),
                    ordinal = finding.StepOrdinal,
                    spec = finding.StepSpec,
                    path = finding.Path,
                    detail = Cap(finding.Detail, 600),
                    suggestion = finding.Suggestion,
                    seq,
                    stamp,
                    undo = JsonSerializer.Serialize(undo, Json),
                },
                tx,
                cancellationToken: k));
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: k));
        }, ct);
    }

    public Task<bool> MarkUndoneAsync(long workspaceId, long id, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE memory_repairs SET state = 'undone', resolved_utc = @stamp, resolved_by = 'owner (undo)' WHERE workspace_id = @workspaceId AND id = @id AND state = 'auto_fixed'",
                new { workspaceId, id, stamp },
                tx,
                cancellationToken: k)) > 0, ct);
    }

    public async Task<bool> WasUndoneAsync(long workspaceId, long flowId, string oldSpec, string newSpec, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'undone' AND target_kind = 'flow' AND target_id = @flowId " +
            "AND step_spec = @newSpec AND suggestion = @oldSpec",
            new { workspaceId, flowId, oldSpec, newSpec },
            cancellationToken: ct)) > 0;
    }
    public async Task<IReadOnlyList<MemoryRepair>> ListOpenAsync(long workspaceId, bool? ownerOnly, int skip, int take, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RepairRow>(new CommandDefinition(
            $"SELECT {Columns} FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'open' AND (@ownerOnly IS NULL OR owner_only = @ownerOnly) " +
            "ORDER BY seq, id LIMIT @take OFFSET @skip",
            new { workspaceId, ownerOnly = ownerOnly is null ? (int?)null : ownerOnly.Value ? 1 : 0, skip = Math.Max(0, skip), take = Math.Clamp(take, 1, MaxPage) },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToRepair())];
    }

    public async Task<IReadOnlyList<MemoryRepair>> ListOpenForPathAsync(long workspaceId, string path, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RepairRow>(new CommandDefinition(
            $"SELECT {Columns} FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'open' AND path = @path ORDER BY seq, id LIMIT @take",
            new { workspaceId, path, take = Math.Clamp(take, 1, MaxPage) },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToRepair())];
    }

    public async Task<IReadOnlyList<MemoryRepair>> ListAutoFixedAsync(long workspaceId, int take, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RepairRow>(new CommandDefinition(
            $"SELECT {Columns} FROM memory_repairs WHERE workspace_id = @workspaceId AND state IN ('auto_fixed', 'undone') ORDER BY id DESC LIMIT @take",
            new { workspaceId, take = Math.Clamp(take, 1, MaxPage) },
            cancellationToken: ct));
        return [.. rows.Select(row => row.ToRepair())];
    }

    public async Task<MemoryRepair?> GetAsync(long workspaceId, long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RepairRow>(new CommandDefinition(
            $"SELECT {Columns} FROM memory_repairs WHERE workspace_id = @workspaceId AND id = @id",
            new { workspaceId, id },
            cancellationToken: ct));
        return row?.ToRepair();
    }

    public async Task<RepairCounts> CountsAsync(long workspaceId, DateTimeOffset recentSince, CancellationToken ct)
    {
        var all = await CountsCoreAsync(workspaceId, recentSince, ct);
        return all.TryGetValue(workspaceId, out var counts) ? counts : RepairCounts.None;
    }

    public Task<IReadOnlyDictionary<long, RepairCounts>> CountsAllAsync(DateTimeOffset recentSince, CancellationToken ct) =>
        CountsCoreAsync(null, recentSince, ct);

    public Task SetFullScanAsync(long workspaceId, int otherBranchSteps, string? branch, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO memory_repair_state (workspace_id, seq, other_branch_steps, branch, full_scan_utc) VALUES (@workspaceId, 0, @otherBranchSteps, @branch, @stamp) " +
                "ON CONFLICT (workspace_id) DO UPDATE SET other_branch_steps = excluded.other_branch_steps, branch = excluded.branch, full_scan_utc = excluded.full_scan_utc",
                new { workspaceId, otherBranchSteps = Math.Max(0, otherBranchSteps), branch, stamp },
                tx,
                cancellationToken: k)), ct);
    }

    private async Task<IReadOnlyDictionary<long, RepairCounts>> CountsCoreAsync(long? workspaceId, DateTimeOffset recentSince, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<CountRow>(new CommandDefinition(
            "SELECT r.workspace_id, " +
            "SUM(CASE WHEN r.state = 'open' THEN 1 ELSE 0 END) AS open_count, " +
            "SUM(CASE WHEN r.state = 'open' AND r.owner_only = 0 THEN 1 ELSE 0 END) AS for_agents, " +
            "SUM(CASE WHEN r.state = 'open' AND r.owner_only = 1 THEN 1 ELSE 0 END) AS for_owner, " +
            "SUM(CASE WHEN r.state = 'auto_fixed' AND r.resolved_utc >= @since THEN 1 ELSE 0 END) AS auto_fixed " +
            "FROM memory_repairs r WHERE (@workspaceId IS NULL OR r.workspace_id = @workspaceId) AND r.state IN ('open', 'auto_fixed') GROUP BY r.workspace_id",
            new { workspaceId, since = Timestamp.Format(recentSince) },
            cancellationToken: ct));
        var states = (await connection.QueryAsync<StateRow>(new CommandDefinition(
            "SELECT workspace_id, seq, other_branch_steps, branch, full_scan_utc FROM memory_repair_state WHERE (@workspaceId IS NULL OR workspace_id = @workspaceId)",
            new { workspaceId },
            cancellationToken: ct))).ToDictionary(row => row.WorkspaceId);
        var result = new Dictionary<long, RepairCounts>();
        foreach (var row in rows)
        {
            states.TryGetValue(row.WorkspaceId, out var state);
            result[row.WorkspaceId] = new RepairCounts(
                (int)row.OpenCount,
                (int)row.ForAgents,
                (int)row.ForOwner,
                (int)row.AutoFixed,
                (int)(state?.OtherBranchSteps ?? 0),
                state?.Seq ?? 0,
                state?.Branch,
                Timestamp.ParseNullable(state?.FullScanUtc));
        }

        foreach (var (id, state) in states)
        {
            if (!result.ContainsKey(id))
            {
                result[id] = new RepairCounts(0, 0, 0, 0, (int)state.OtherBranchSteps, state.Seq, state.Branch, Timestamp.ParseNullable(state.FullScanUtc));
            }
        }

        return result;
    }

    private static async Task<bool> ExistsOpenAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, RepairFinding finding, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'open' AND kind = @kind AND target_kind = @target AND target_id = @targetId AND step_spec = @spec",
            new { workspaceId, kind = MemoryRepairText.Kind(finding.Kind), target = MemoryRepairText.Target(finding.Target), targetId = finding.TargetId, spec = finding.StepSpec },
            tx,
            cancellationToken: ct)) > 0;

    private static async Task<long> NextSeqAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO memory_repair_state (workspace_id, seq) VALUES (@workspaceId, 1) ON CONFLICT (workspace_id) DO UPDATE SET seq = seq + 1",
            new { workspaceId },
            tx,
            cancellationToken: ct));
        return await CurrentSeqAsync(connection, tx, workspaceId, ct);
    }

    private static async Task<long> CurrentSeqAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT seq FROM memory_repair_state WHERE workspace_id = @workspaceId",
            new { workspaceId },
            tx,
            cancellationToken: ct)) ?? 0;

    private static async Task<int> CountOpenAsync(SqliteConnection connection, SqliteTransaction tx, long workspaceId, CancellationToken ct) =>
        (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM memory_repairs WHERE workspace_id = @workspaceId AND state = 'open'",
            new { workspaceId },
            tx,
            cancellationToken: ct));

    private static string Key(string kind, string target, long targetId, string stepSpec) => $"{kind}|{target}|{targetId}|{stepSpec}";

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private sealed class RepairRow
    {
        public long Id { get; set; }

        public long WorkspaceId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string TargetKind { get; set; } = string.Empty;

        public long TargetId { get; set; }

        public string TargetName { get; set; } = string.Empty;

        public long StepOrdinal { get; set; }

        public string StepSpec { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Detail { get; set; } = string.Empty;

        public string? Suggestion { get; set; }

        public long OwnerOnly { get; set; }

        public string State { get; set; } = string.Empty;

        public long Seq { get; set; }

        public string FirstSeenUtc { get; set; } = string.Empty;

        public string LastSeenUtc { get; set; } = string.Empty;

        public string? ResolvedUtc { get; set; }

        public string? ResolvedBy { get; set; }

        public string? UndoJson { get; set; }

        public MemoryRepair ToRepair() => new(
            Id,
            WorkspaceId,
            MemoryRepairText.KindFrom(Kind),
            MemoryRepairText.TargetFrom(TargetKind),
            TargetId,
            TargetName,
            (int)StepOrdinal,
            StepSpec,
            Path,
            Detail,
            Suggestion,
            OwnerOnly != 0,
            MemoryRepairText.StateFrom(State),
            Seq,
            Timestamp.Parse(FirstSeenUtc),
            Timestamp.Parse(LastSeenUtc),
            Timestamp.ParseNullable(ResolvedUtc),
            ResolvedBy,
            UndoJson);
    }

    private sealed class CountRow
    {
        public long WorkspaceId { get; set; }

        public long OpenCount { get; set; }

        public long ForAgents { get; set; }

        public long ForOwner { get; set; }

        public long AutoFixed { get; set; }
    }

    private sealed class StateRow
    {
        public long WorkspaceId { get; set; }

        public long Seq { get; set; }

        public long OtherBranchSteps { get; set; }

        public string? Branch { get; set; }

        public string? FullScanUtc { get; set; }
    }
}
