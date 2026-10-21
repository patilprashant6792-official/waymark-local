using Dapper;
using Waymark.Core.Workspaces;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class WorkspaceStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IWorkspaceStore
{
    private const string Columns = "id, name, path, enabled, added_utc, last_indexed_utc";

    public Task<Workspace?> AddAsync(string name, string path, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return writes.ExecuteAsync<Workspace?>(async (connection, tx, k) =>
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT OR IGNORE INTO workspaces (name, path, enabled, added_utc) VALUES (@name, @path, 1, @added)",
                new { name, path, added = Timestamp.Format(now) },
                tx,
                cancellationToken: k));
            if (inserted == 0)
            {
                return null;
            }

            var row = await connection.QuerySingleAsync<WorkspaceRow>(new CommandDefinition(
                "SELECT " + Columns + " FROM workspaces WHERE id = last_insert_rowid()",
                transaction: tx,
                cancellationToken: k));
            return ToWorkspace(row);
        }, ct);
    }

    public async Task<Workspace?> GetByNameAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<WorkspaceRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM workspaces WHERE name = @name",
            new { name },
            cancellationToken: ct));
        return row is null ? null : ToWorkspace(row);
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkspaceRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM workspaces ORDER BY name",
            cancellationToken: ct));
        return rows.Select(ToWorkspace).ToList();
    }

    public Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE workspaces SET enabled = @enabled WHERE id = @id",
                new { enabled = enabled ? 1 : 0, id },
                tx,
                cancellationToken: k)) > 0,
            ct);

    public Task<bool> MarkIndexedAsync(long id, DateTimeOffset at, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE workspaces SET last_indexed_utc = @at WHERE id = @id",
                new { at = Timestamp.Format(at), id },
                tx,
                cancellationToken: k)) > 0,
            ct);

    public Task<bool> RemoveAsync(long id, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM workspaces WHERE id = @id",
                new { id },
                tx,
                cancellationToken: k)) > 0,
            ct);

    private static Workspace ToWorkspace(WorkspaceRow row) =>
        new(
            row.Id,
            row.Name,
            row.Path,
            row.Enabled != 0,
            Timestamp.Parse(row.AddedUtc),
            Timestamp.ParseNullable(row.LastIndexedUtc));

    private sealed class WorkspaceRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public long Enabled { get; set; }

        public string AddedUtc { get; set; } = string.Empty;

        public string? LastIndexedUtc { get; set; }
    }
}
