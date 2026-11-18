using Dapper;
using Waymark.Core.Guides;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class WorkspaceGuideStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IWorkspaceGuideStore
{
    // INSERT ... SELECT so a workspace that was removed meanwhile inserts nothing instead of failing the foreign key.
    private const string UpsertSql =
        "INSERT INTO workspace_guides (workspace_id, summary, instructions, changed_utc, changed_by) " +
        "SELECT @workspaceId, @summary, @instructions, @changed, @changedBy WHERE EXISTS (SELECT 1 FROM workspaces WHERE id = @workspaceId) " +
        "ON CONFLICT(workspace_id) DO UPDATE SET summary = excluded.summary, instructions = excluded.instructions, " +
        "changed_utc = excluded.changed_utc, changed_by = excluded.changed_by";

    public async Task<IReadOnlyList<WorkspaceGuideRow>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<GuideRow>(new CommandDefinition(
            "SELECT workspace_id, summary, instructions, changed_utc, changed_by FROM workspace_guides ORDER BY workspace_id",
            cancellationToken: ct));
        return rows.Select(r => new WorkspaceGuideRow(r.WorkspaceId, r.Summary, r.Instructions, Timestamp.Parse(r.ChangedUtc), r.ChangedBy)).ToList();
    }

    public Task<bool> UpsertAsync(long workspaceId, string summary, string instructions, string changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertSql,
                new { workspaceId, summary, instructions, changed = Timestamp.Format(now), changedBy },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    public Task<bool> DeleteAsync(long workspaceId, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM workspace_guides WHERE workspace_id = @workspaceId",
                new { workspaceId },
                tx,
                cancellationToken: k)) > 0,
            ct);

    private sealed class GuideRow
    {
        public long WorkspaceId { get; set; }

        public string Summary { get; set; } = string.Empty;

        public string Instructions { get; set; } = string.Empty;

        public string ChangedUtc { get; set; } = string.Empty;

        public string ChangedBy { get; set; } = string.Empty;
    }
}
