using Dapper;
using Waymark.Core.Security;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class AccessRuleStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IAccessRuleStore
{
    private const string Columns = "id, workspace_id, kind, pattern, note, enabled, created_utc";

    public async Task<IReadOnlyList<AccessRule>> ListAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM workspace_access_rules WHERE workspace_id = @workspaceId ORDER BY kind, pattern",
            new { workspaceId },
            cancellationToken: ct));
        return rows.Select(ToRule).ToList();
    }

    public async Task<AccessRule?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RuleRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM workspace_access_rules WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return row is null ? null : ToRule(row);
    }

    public Task<AccessRule?> AddAsync(long workspaceId, AccessRuleKind kind, string pattern, string note, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(note);
        return writes.ExecuteAsync<AccessRule?>(async (connection, tx, k) =>
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO workspace_access_rules (workspace_id, kind, pattern, note, enabled, created_utc) VALUES (@workspaceId, @kind, @pattern, @note, 1, @created)",
                new { workspaceId, kind = kind.ToString(), pattern, note, created = Timestamp.Format(now) },
                tx,
                cancellationToken: k));
            if (inserted == 0)
            {
                return null;
            }

            var row = await connection.QuerySingleAsync<RuleRow>(new CommandDefinition(
                "SELECT " + Columns + " FROM workspace_access_rules WHERE id = last_insert_rowid()",
                transaction: tx,
                cancellationToken: k));
            return ToRule(row);
        }, ct);
    }

    public Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE workspace_access_rules SET enabled = @enabled WHERE id = @id",
                new { enabled = enabled ? 1 : 0, id },
                tx,
                cancellationToken: k)) > 0,
            ct);

    public Task<bool> RemoveAsync(long id, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM workspace_access_rules WHERE id = @id",
                new { id },
                tx,
                cancellationToken: k)) > 0,
            ct);

    private static AccessRule ToRule(RuleRow row) =>
        new(
            row.Id,
            row.WorkspaceId,
            Enum.Parse<AccessRuleKind>(row.Kind),
            row.Pattern,
            row.Enabled != 0,
            row.Note,
            Timestamp.Parse(row.CreatedUtc));

    private sealed class RuleRow
    {
        public long Id { get; set; }

        public long WorkspaceId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Pattern { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        public long Enabled { get; set; }

        public string CreatedUtc { get; set; } = string.Empty;
    }
}
