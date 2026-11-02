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
                applied.Add(new WorkspaceApplied(item.WorkspaceId, rules, maps.Created, maps.Steps));
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

    // Only creates: a map whose name is taken (it was not when the plan was made) is skipped, never merged.
    private static async Task<(int Created, int Steps)> AddMapsAsync(SqliteConnection connection, SqliteTransaction tx, WorkspaceApply item, string stamp, CancellationToken ct)
    {
        int created = 0, steps = 0;
        var workspaceId = item.WorkspaceId;
        foreach (var map in item.Plan.Maps)
        {
            var taken = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT id FROM feature_maps WHERE workspace_id = @workspaceId AND name = @name", new { workspaceId, name = map.Name }, tx, cancellationToken: ct));
            if (taken is not null || map.Steps.Count == 0)
            {
                continue;
            }

            var changed = map.UpdatedUtc is { } updated ? Timestamp.Format(updated) : stamp;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO feature_maps (workspace_id, name, summary, created_utc, updated_utc, version, ordered, distinct_reason) VALUES (@workspaceId, @name, @summary, @changed, @changed, 1, @ordered, '')",
                new { workspaceId, name = map.Name, summary = map.Summary, changed, ordered = map.Ordered ? 1 : 0 },
                tx,
                cancellationToken: ct));
            var mapId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: ct));
            var ordinal = 0;
            foreach (var step in map.Steps.Take(FeatureMapRules.MaxComponentsPerMap))
            {
                var inserted = await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT OR IGNORE INTO feature_components (map_id, ordinal, spec, note, depth, link_kind, path) VALUES (@mapId, @ordinal, @spec, @note, @depth, @link, @path)",
                    new
                    {
                        mapId,
                        ordinal,
                        spec = step.Spec,
                        note = step.Note,
                        depth = map.Ordered ? step.Depth : 0,
                        link = map.Ordered ? step.Link : "call",
                        path = PathOf(step.Spec),
                    },
                    tx,
                    cancellationToken: ct));
                if (inserted > 0)
                {
                    ordinal++;
                    steps++;
                }
            }

            if (ordinal == 0)
            {
                // Never leave an empty map behind.
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM feature_maps WHERE id = @mapId", new { mapId }, tx, cancellationToken: ct));
                continue;
            }

            created++;
        }

        return (created, steps);
    }

    private static string PathOf(string spec) =>
        FeatureComponentSpec.TryParse(spec, out var component, out _) ? component.Path : spec;
}
