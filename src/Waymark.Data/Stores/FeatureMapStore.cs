using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class FeatureMapStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IFeatureMapStore
{
    private const int MaxQueryWords = 6;
    private const int MaxPageSize = 200;
    private const int MaxPathHits = 200;

    public async Task<FeatureMapPage> ListAsync(long workspaceId, string? query, int skip, int take, CancellationToken ct)
    {
        var (where, parameters) = Filter(workspaceId, query);
        parameters.Add("skip", Math.Max(0, skip));
        parameters.Add("take", Math.Clamp(take, 1, MaxPageSize));
        await using var connection = await connections.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM feature_maps m WHERE {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            "SELECT m.id, m.name, m.summary, m.version, m.ordered, m.updated_utc, (SELECT COUNT(*) FROM feature_components c WHERE c.map_id = m.id) AS step_count " +
            $"FROM feature_maps m WHERE {where} ORDER BY m.updated_utc DESC, m.id DESC LIMIT @take OFFSET @skip",
            parameters,
            cancellationToken: ct));
        var items = rows
            .Select(row => new FeatureMapSummary(row.Id, row.Name, row.Summary, (int)row.StepCount, (int)row.Version, row.Ordered != 0, Timestamp.Parse(row.UpdatedUtc)))
            .ToList();
        return new FeatureMapPage(items, (int)total);
    }

    public async Task<IReadOnlyList<FeatureMap>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var maps = (await connection.QueryAsync<MapRow>(new CommandDefinition(
            "SELECT id, name, summary, version, ordered, distinct_reason, updated_utc FROM feature_maps WHERE workspace_id = @workspaceId AND id IN @ids",
            new { workspaceId, ids = ids.ToArray() },
            cancellationToken: ct))).ToDictionary(row => row.Id);
        if (maps.Count == 0)
        {
            return [];
        }

        var steps = (await connection.QueryAsync<StepRow>(new CommandDefinition(
            "SELECT map_id, spec, depth, link_kind, note FROM feature_components WHERE map_id IN @mapIds ORDER BY map_id, ordinal",
            new { mapIds = maps.Keys.ToArray() },
            cancellationToken: ct))).ToLookup(row => row.MapId);

        var result = new List<FeatureMap>();
        foreach (var id in ids.Distinct())
        {
            if (maps.TryGetValue(id, out var map))
            {
                var own = steps[id].Select(row => new FeatureMapStep(row.Spec, (int)row.Depth, row.LinkKind, row.Note)).ToList();
                result.Add(new FeatureMap(map.Id, map.Name, map.Summary, (int)map.Version, map.Ordered != 0, map.DistinctReason, Timestamp.Parse(map.UpdatedUtc), own));
            }
        }

        return result;
    }

    public async Task<FeatureMap?> GetByNameAsync(long workspaceId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        long? id;
        await using (var connection = await connections.OpenAsync(ct))
        {
            id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                cancellationToken: ct));
        }

        if (id is null)
        {
            return null;
        }

        var found = await GetAsync(workspaceId, [id.Value], ct);
        return found.Count == 0 ? null : found[0];
    }

    public Task<FlowWriteResult> CreateAsync(
        long workspaceId,
        string name,
        string summary,
        string distinctReason,
        IReadOnlyList<FeatureMapStep> steps,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("A flow needs at least one step.", nameof(steps));
        }

        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<FlowWriteResult>(async (connection, tx, k) =>
        {
            var taken = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k));
            if (taken is not null)
            {
                return new FlowWriteResult(FlowWrite.NameTaken, taken.Value, name, 0);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO feature_maps (workspace_id, name, summary, created_utc, updated_utc, version, ordered, distinct_reason) " +
                "VALUES (@workspaceId, @name, @summary, @stamp, @stamp, 1, 1, @distinctReason)",
                new { workspaceId, name, summary, stamp, distinctReason },
                tx,
                cancellationToken: k));
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: k));
            await InsertStepsAsync(connection, tx, id, steps, k);
            return new FlowWriteResult(FlowWrite.Saved, id, name, 1);
        }, ct);
    }

    public Task<FlowWriteResult> ReplaceAsync(
        long workspaceId,
        string name,
        int expectedVersion,
        string? newName,
        string? summary,
        IReadOnlyList<FeatureMapStep> steps,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("A flow needs at least one step.", nameof(steps));
        }

        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<FlowWriteResult>(async (connection, tx, k) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
                "SELECT id, name, version FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k));
            if (existing is null)
            {
                return new FlowWriteResult(FlowWrite.NotFound, 0, name, 0);
            }

            if (existing.Version != expectedVersion)
            {
                return new FlowWriteResult(FlowWrite.VersionConflict, existing.Id, existing.Name, (int)existing.Version);
            }

            var finalName = newName ?? existing.Name;
            if (!string.Equals(finalName, existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                var taken = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @finalName",
                    new { workspaceId, finalName },
                    tx,
                    cancellationToken: k));
                if (taken is not null)
                {
                    return new FlowWriteResult(FlowWrite.NameTaken, taken.Value, finalName, (int)existing.Version);
                }
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM feature_components WHERE map_id = @id",
                new { id = existing.Id },
                tx,
                cancellationToken: k));
            await InsertStepsAsync(connection, tx, existing.Id, steps, k);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE feature_maps SET name = @finalName, summary = COALESCE(@summary, summary), version = version + 1, ordered = 1, updated_utc = @stamp WHERE id = @id",
                new { id = existing.Id, finalName, summary, stamp },
                tx,
                cancellationToken: k));
            return new FlowWriteResult(FlowWrite.Saved, existing.Id, finalName, expectedVersion + 1);
        }, ct);
    }

    public Task<FlowWriteResult> RemoveStepAsync(long workspaceId, string name, int expectedVersion, string spec, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<FlowWriteResult>(async (connection, tx, k) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
                "SELECT id, name, version FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k));
            if (existing is null)
            {
                return new FlowWriteResult(FlowWrite.NotFound, 0, name, 0);
            }

            if (existing.Version != expectedVersion)
            {
                return new FlowWriteResult(FlowWrite.VersionConflict, existing.Id, existing.Name, (int)existing.Version);
            }

            var ordinal = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT ordinal FROM feature_components WHERE map_id = @id AND spec = @spec",
                new { id = existing.Id, spec },
                tx,
                cancellationToken: k));
            if (ordinal is null)
            {
                return new FlowWriteResult(FlowWrite.NotFound, existing.Id, existing.Name, (int)existing.Version);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM feature_components WHERE map_id = @id AND ordinal = @ordinal",
                new { id = existing.Id, ordinal = ordinal.Value },
                tx,
                cancellationToken: k));

            // Two statements, because the unique (map_id, ordinal) is checked row by row: first out of the way, then down by one.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE feature_components SET ordinal = ordinal + 1000000 WHERE map_id = @id AND ordinal > @ordinal",
                new { id = existing.Id, ordinal = ordinal.Value },
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE feature_components SET ordinal = ordinal - 1000001 WHERE map_id = @id AND ordinal >= 1000000",
                new { id = existing.Id },
                tx,
                cancellationToken: k));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE feature_maps SET version = version + 1, updated_utc = @stamp WHERE id = @id",
                new { id = existing.Id, stamp },
                tx,
                cancellationToken: k));
            return new FlowWriteResult(FlowWrite.Saved, existing.Id, existing.Name, expectedVersion + 1);
        }, ct);
    }
    public async Task<IReadOnlyList<FlowRef>> FindByRootAsync(long workspaceId, string rootSpec, long? exceptId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSpec);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<FlowRow>(new CommandDefinition(
            "SELECT m.id, m.name, m.summary " +
            "FROM feature_maps m JOIN feature_components c ON c.map_id = m.id AND c.ordinal = 0 " +
            "WHERE m.workspace_id = @workspaceId AND m.ordered = 1 AND c.spec = @rootSpec AND (@exceptId IS NULL OR m.id <> @exceptId) " +
            "ORDER BY m.updated_utc DESC, m.id DESC LIMIT 5",
            new { workspaceId, rootSpec, exceptId },
            cancellationToken: ct));
        return rows.Select(row => new FlowRef(row.Id, row.Name, row.Summary)).ToList();
    }

    public async Task<IReadOnlyList<FlowStepRef>> FindByPathAsync(long workspaceId, string path, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<PathRow>(new CommandDefinition(
            "SELECT m.id AS flow_id, m.name AS flow_name, c.ordinal AS ordinal, c.spec AS spec " +
            "FROM feature_components c JOIN feature_maps m ON m.id = c.map_id " +
            "WHERE m.workspace_id = @workspaceId AND m.ordered = 1 AND c.path = @path " +
            "ORDER BY m.updated_utc DESC, m.id DESC, c.ordinal LIMIT @limit",
            new { workspaceId, path, limit = Math.Clamp(limit, 1, MaxPathHits) },
            cancellationToken: ct));
        return rows.Select(row => new FlowStepRef(row.FlowId, row.FlowName, (int)row.Ordinal, row.Spec)).ToList();
    }

    public async Task<IReadOnlyList<FeatureMapOverlap>> FindOverlapsAsync(long workspaceId, long mapId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<OverlapRow>(new CommandDefinition(
            "SELECT o.id AS id, o.name AS name, COUNT(*) AS shared " +
            "FROM feature_components mine " +
            "JOIN feature_components theirs ON theirs.spec = mine.spec AND theirs.map_id <> mine.map_id " +
            "JOIN feature_maps o ON o.id = theirs.map_id " +
            "WHERE mine.map_id = @mapId AND o.workspace_id = @workspaceId " +
            "GROUP BY o.id, o.name ORDER BY shared DESC, o.id LIMIT 3",
            new { workspaceId, mapId },
            cancellationToken: ct));
        return rows.Select(row => new FeatureMapOverlap(row.Id, row.Name, (int)row.Shared)).ToList();
    }

    public async Task<IReadOnlyDictionary<long, FeatureMapIndex>> ListNamesAsync(int perWorkspace, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<NameRow>(new CommandDefinition(
            "SELECT m.id, m.workspace_id, m.name, m.summary, m.ordered, (SELECT COUNT(*) FROM feature_components c WHERE c.map_id = m.id) AS step_count " +
            "FROM feature_maps m ORDER BY m.updated_utc DESC, m.id DESC",
            cancellationToken: ct));
        var keep = Math.Max(1, perWorkspace);
        return rows
            .GroupBy(row => row.WorkspaceId)
            .ToDictionary(
                group => group.Key,
                group => new FeatureMapIndex(
                    group.Count(),
                    group.Take(keep).Select(row => new FeatureMapName(row.Id, row.Name, row.Summary, (int)row.StepCount, row.Ordered != 0)).ToList()));
    }

    public Task<long?> DeleteAsync(long workspaceId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return writes.ExecuteAsync<long?>(async (connection, tx, k) =>
        {
            var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k));
            if (id is null)
            {
                return null;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM feature_maps WHERE id = @id",
                new { id = id.Value },
                tx,
                cancellationToken: k));
            return id;
        }, ct);
    }

    private static async Task InsertStepsAsync(SqliteConnection connection, SqliteTransaction tx, long mapId, IReadOnlyList<FeatureMapStep> steps, CancellationToken ct)
    {
        for (var ordinal = 0; ordinal < steps.Count; ordinal++)
        {
            var step = steps[ordinal];
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO feature_components (map_id, ordinal, spec, note, depth, link_kind, path) VALUES (@mapId, @ordinal, @spec, @note, @depth, @link, @path)",
                new { mapId, ordinal, spec = step.Spec, note = step.Note, depth = step.Depth, link = step.Link, path = PathOf(step.Spec) },
                tx,
                cancellationToken: ct));
        }
    }

    /// <summary>The file part of a step's text, which is what a lookup by file compares.</summary>
    private static string PathOf(string spec) =>
        FeatureComponentSpec.TryParse(spec, out var component, out _) ? component.Path : spec;

    // Every word must be found in the name, the summary, or a step, its path or its note. The SQL holds fixed text only; the words travel as parameters.
    private static (string Where, DynamicParameters Parameters) Filter(long workspaceId, string? query)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ws", workspaceId);
        var where = new StringBuilder("m.workspace_id = @ws");
        var words = (query ?? string.Empty)
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueryWords)
            .ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var name = "w" + i;
            parameters.Add(name, "%" + EscapeLike(words[i]) + "%");
            where.Append(
                $" AND (m.name LIKE @{name} ESCAPE '!' OR m.summary LIKE @{name} ESCAPE '!' OR EXISTS (SELECT 1 FROM feature_components c WHERE c.map_id = m.id AND (c.spec LIKE @{name} ESCAPE '!' OR c.note LIKE @{name} ESCAPE '!')))");
        }

        return (where.ToString(), parameters);
    }

    private static string EscapeLike(string word) =>
        word.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal).Replace("_", "!_", StringComparison.Ordinal);

    private sealed class MapRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public long Version { get; set; }

        public long Ordered { get; set; }

        public string DistinctReason { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;
    }

    private sealed class VersionRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Version { get; set; }
    }

    private sealed class SummaryRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public long Version { get; set; }

        public long Ordered { get; set; }

        public string UpdatedUtc { get; set; } = string.Empty;

        public long StepCount { get; set; }
    }

    private sealed class StepRow
    {
        public long MapId { get; set; }

        public string Spec { get; set; } = string.Empty;

        public long Depth { get; set; }

        public string LinkKind { get; set; } = "call";

        public string Note { get; set; } = string.Empty;
    }

    private sealed class FlowRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;
    }

    private sealed class PathRow
    {
        public long FlowId { get; set; }

        public string FlowName { get; set; } = string.Empty;

        public long Ordinal { get; set; }

        public string Spec { get; set; } = string.Empty;
    }

    private sealed class OverlapRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Shared { get; set; }
    }

    private sealed class NameRow
    {
        public long Id { get; set; }

        public long WorkspaceId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public long Ordered { get; set; }

        public long StepCount { get; set; }
    }
}
