using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Transfer;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class WorkspaceBundleStore(IWriteQueue writes) : IWorkspaceBundleStore
{
    public Task<IReadOnlyList<WorkspaceApplied>> ApplyAsync(IReadOnlyList<WorkspaceApply> items, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var stamp = Timestamp.Format(now);
        return writes.ExecuteAsync<IReadOnlyList<WorkspaceApplied>>(async (connection, tx, k) =>
        {
            var applied = new List<WorkspaceApplied>();
            foreach (var item in items)
            {
                var rules = await AddRulesAsync(connection, tx, item, stamp, k);
                var maps = await AddMapsAsync(connection, tx, item, stamp, k);
                applied.Add(new WorkspaceApplied(item.WorkspaceId, rules, maps.Created, maps.Extended, maps.Components));
            }

            return applied;
        }, ct);
    }

    private static async Task<int> AddRulesAsync(SqliteConnection connection, SqliteTransaction tx, WorkspaceApply item, string stamp, CancellationToken ct)
    {
        if (item.Plan.RulesToAdd.Count == 0)
        {
            return 0;
        }

        var workspaceId = item.WorkspaceId;
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM workspace_access_rules WHERE workspace_id = @workspaceId", new { workspaceId }, tx, cancellationToken: ct));
        var added = 0;
        foreach (var rule in item.Plan.RulesToAdd)
        {
            if (count + added >= AccessRuleRules.MaxRulesPerWorkspace)
            {
                break;
            }

            // A rule that is there already is ignored, never changed.
            added += await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO workspace_access_rules (workspace_id, kind, pattern, note, enabled, created_utc) VALUES (@workspaceId, @kind, @pattern, @note, @enabled, @created)",
                new
                {
                    workspaceId,
                    kind = rule.Kind.ToString(),
                    pattern = rule.Pattern,
                    note = rule.Note,
                    enabled = rule.Enabled ? 1 : 0,
                    created = rule.CreatedUtc is { } created ? Timestamp.Format(created) : stamp,
                },
                tx,
                cancellationToken: ct));
        }

        return added;
    }

    private static async Task<(int Created, int Extended, int Components)> AddMapsAsync(SqliteConnection connection, SqliteTransaction tx, WorkspaceApply item, string stamp, CancellationToken ct)
    {
        int created = 0, extended = 0, components = 0;
        var workspaceId = item.WorkspaceId;
        foreach (var map in item.Plan.Maps)
        {
            var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name", new { workspaceId, name = map.Name }, tx, cancellationToken: ct));
            var isNew = id is null;
            if (isNew)
            {
                var changed = map.UpdatedUtc is { } updated ? Timestamp.Format(updated) : stamp;
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO feature_maps (workspace_id, name, summary, created_utc, updated_utc) VALUES (@workspaceId, @name, @summary, @changed, @changed)",
                    new { workspaceId, name = map.Name, summary = map.Summary, changed },
                    tx,
                    cancellationToken: ct));
                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: ct));
            }

            var mapId = id!.Value;
            var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM feature_components WHERE map_id = @mapId", new { mapId }, tx, cancellationToken: ct));
            var next = (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT MAX(ordinal) FROM feature_components WHERE map_id = @mapId", new { mapId }, tx, cancellationToken: ct)) ?? -1) + 1;
            var added = 0;
            foreach (var component in map.Components)
            {
                if (total + added >= FeatureMapRules.MaxComponentsPerMap)
                {
                    break;
                }

                var inserted = await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO feature_components (map_id, ordinal, spec, note) VALUES (@mapId, @ordinal, @spec, @note)",
                    new { mapId, ordinal = next, spec = component.Spec, note = component.Note },
                    tx,
                    cancellationToken: ct));
                if (inserted > 0)
                {
                    next++;
                    added++;
                }
            }

            if (isNew && added == 0)
            {
                // Never leave an empty map behind.
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM feature_maps WHERE id = @mapId", new { mapId }, tx, cancellationToken: ct));
                continue;
            }

            if (added == 0)
            {
                continue;
            }

            if (!isNew)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE feature_maps SET updated_utc = @stamp WHERE id = @mapId", new { stamp, mapId }, tx, cancellationToken: ct));
            }

            components += added;
            if (isNew)
            {
                created++;
            }
            else
            {
                extended++;
            }
        }

        return (created, extended, components);
    }
}
