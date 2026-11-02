using System.Text;
using Dapper;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class FeatureMapStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IFeatureMapStore
{
    private const int MaxQueryWords = 6;
    private const int MaxPageSize = 200;

    public async Task<FeatureMapPage> ListAsync(long workspaceId, string? query, int skip, int take, CancellationToken ct)
    {
        var (where, parameters) = Filter(workspaceId, query);
        parameters.Add("skip", Math.Max(0, skip));
        parameters.Add("take", Math.Clamp(take, 1, MaxPageSize));
        await using var connection = await connections.OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM feature_maps m WHERE {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            "SELECT m.id, m.name, m.summary, m.updated_utc, (SELECT COUNT(*) FROM feature_components c WHERE c.map_id = m.id) AS component_count " +
            $"FROM feature_maps m WHERE {where} ORDER BY m.updated_utc DESC, m.id DESC LIMIT @take OFFSET @skip",
            parameters,
            cancellationToken: ct));
        var items = rows
            .Select(row => new FeatureMapSummary(row.Id, row.Name, row.Summary, (int)row.ComponentCount, Timestamp.Parse(row.UpdatedUtc)))
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
            "SELECT id, name, summary, updated_utc FROM feature_maps WHERE workspace_id = @workspaceId AND id IN @ids",
            new { workspaceId, ids = ids.ToArray() },
            cancellationToken: ct))).ToDictionary(row => row.Id);
        if (maps.Count == 0)
        {
            return [];
        }

        var components = (await connection.QueryAsync<ComponentRow>(new CommandDefinition(
            "SELECT map_id, spec, note FROM feature_components WHERE map_id IN @mapIds ORDER BY map_id, ordinal",
            new { mapIds = maps.Keys.ToArray() },
            cancellationToken: ct))).ToLookup(row => row.MapId);

        var result = new List<FeatureMap>();
        foreach (var id in ids.Distinct())
        {
            if (maps.TryGetValue(id, out var map))
            {
                var own = components[id].Select(row => new FeatureMapComponent(row.Spec, row.Note)).ToList();
                result.Add(new FeatureMap(map.Id, map.Name, map.Summary, Timestamp.Parse(map.UpdatedUtc), own));
            }
        }

        return result;
    }

    public Task<FeatureMapSaveResult> SaveAsync(
        long workspaceId,
        string name,
        string? summary,
        IReadOnlyList<FeatureMapComponent> add,
        IReadOnlyList<string> remove,
        int maxComponents,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<FeatureMapSaveResult>(async (connection, tx, k) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<MapRow>(new CommandDefinition(
                "SELECT id, name, summary, updated_utc FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k));
            if (existing is null && add.Count == 0)
            {
                throw new InvalidOperationException("A new feature map needs at least one component.");
            }
            long id;
            if (existing is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO feature_maps (workspace_id, name, summary, created_utc, updated_utc) VALUES (@workspaceId, @name, @summary, @stamp, @stamp)",
                    new { workspaceId, name, summary = summary ?? string.Empty, stamp },
                    tx,
                    cancellationToken: k));
                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: k));
            }
            else
            {
                id = existing.Id;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE feature_maps SET summary = @summary, updated_utc = @stamp WHERE id = @id",
                    new { id, summary = summary ?? existing.Summary, stamp },
                    tx,
                    cancellationToken: k));
            }

            var removed = 0;
            foreach (var spec in remove)
            {
                removed += await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM feature_components WHERE map_id = @id AND spec = @spec",
                    new { id, spec },
                    tx,
                    cancellationToken: k));
            }

            var nextOrdinal = (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT MAX(ordinal) FROM feature_components WHERE map_id = @id", new { id }, tx, cancellationToken: k)) ?? -1) + 1;
            var added = 0;
            var updated = 0;
            foreach (var item in add)
            {
                var found = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM feature_components WHERE map_id = @id AND spec = @spec",
                    new { id, spec = item.Spec },
                    tx,
                    cancellationToken: k));
                if (found is null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO feature_components (map_id, ordinal, spec, note) VALUES (@id, @ordinal, @spec, @note)",
                        new { id, ordinal = nextOrdinal++, spec = item.Spec, note = item.Note },
                        tx,
                        cancellationToken: k));
                    added++;
                }
                else if (item.Note.Length > 0)
                {
                    updated += await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE feature_components SET note = @note WHERE id = @found AND note <> @note",
                        new { found, note = item.Note },
                        tx,
                        cancellationToken: k));
                }
            }

            var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM feature_components WHERE map_id = @id", new { id }, tx, cancellationToken: k));
            if (total > maxComponents)
            {
                // Thrown inside the job, so the whole save is rolled back.
                throw new InvalidOperationException(
                    $"A feature map holds at most {maxComponents} components and this one would have {total}. Remove some with the remove parameter, or split the feature in two.");
            }

            return new FeatureMapSaveResult(id, existing?.Name ?? name, existing is null, added, updated, removed, total);
        }, ct);
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
            "SELECT id, workspace_id, name FROM feature_maps ORDER BY updated_utc DESC, id DESC", cancellationToken: ct));
        var keep = Math.Max(1, perWorkspace);
        return rows
            .GroupBy(row => row.WorkspaceId)
            .ToDictionary(
                group => group.Key,
                group => new FeatureMapIndex(group.Count(), group.Take(keep).Select(row => new FeatureMapName(row.Id, row.Name)).ToList()));
    }

    public Task<bool> DeleteAsync(long workspaceId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name",
                new { workspaceId, name },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    // Every word must be found in the name, the summary, or a component or note. The SQL holds fixed text only; the words travel as parameters.
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

        public string UpdatedUtc { get; set; } = string.Empty;
    }

    private sealed class SummaryRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;

        public long ComponentCount { get; set; }
    }

    private sealed class ComponentRow
    {
        public long MapId { get; set; }

        public string Spec { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;
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
    }
}
