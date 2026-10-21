using Dapper;
using Waymark.Core.Commands;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class CommandPolicyStore(ISqliteConnectionFactory connections, IWriteQueue writes) : ICommandPolicyStore
{
    public async Task<IReadOnlyList<CommandGateSetting>> ListGatesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<GateRow>(new CommandDefinition(
            "SELECT workspace_id, operation, gate, updated_utc FROM command_gates ORDER BY workspace_id, operation",
            cancellationToken: ct));
        return rows
            .Select(row => new CommandGateSetting(row.WorkspaceId, row.Operation, EnumText.FromDb<CommandGate>(row.Gate), Timestamp.Parse(row.UpdatedUtc)))
            .ToList();
    }

    public Task SetGateAsync(long workspaceId, string operation, CommandGate gate, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return writes.RunAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO command_gates (workspace_id, operation, gate, updated_utc) VALUES (@workspaceId, @operation, @gate, @now) " +
                "ON CONFLICT (workspace_id, operation) DO UPDATE SET gate = excluded.gate, updated_utc = excluded.updated_utc",
                new { workspaceId, operation, gate = EnumText.ToDb(gate), now = Timestamp.Format(now) },
                tx,
                cancellationToken: k)),
            ct);
    }

    public Task<bool> ClearGateAsync(long workspaceId, string operation, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM command_gates WHERE workspace_id = @workspaceId AND operation = @operation",
                new { workspaceId, operation },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    public async Task<CommandTrust?> GetTrustAsync(long workspaceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TrustRow>(new CommandDefinition(
            "SELECT workspace_id, fingerprint_json, approved_utc FROM command_trust WHERE workspace_id = @workspaceId",
            new { workspaceId },
            cancellationToken: ct));
        return row is null ? null : new CommandTrust(row.WorkspaceId, row.FingerprintJson, Timestamp.Parse(row.ApprovedUtc));
    }

    public Task SetTrustAsync(long workspaceId, string fingerprintJson, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fingerprintJson);
        return writes.RunAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO command_trust (workspace_id, fingerprint_json, approved_utc) VALUES (@workspaceId, @fingerprintJson, @now) " +
                "ON CONFLICT (workspace_id) DO UPDATE SET fingerprint_json = excluded.fingerprint_json, approved_utc = excluded.approved_utc",
                new { workspaceId, fingerprintJson, now = Timestamp.Format(now) },
                tx,
                cancellationToken: k)),
            ct);
    }

    public Task<bool> ClearTrustAsync(long workspaceId, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM command_trust WHERE workspace_id = @workspaceId",
                new { workspaceId },
                tx,
                cancellationToken: k)) > 0,
            ct);

    private sealed class GateRow
    {
        public long WorkspaceId { get; set; }

        public string Operation { get; set; } = string.Empty;

        public string Gate { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;
    }

    private sealed class TrustRow
    {
        public long WorkspaceId { get; set; }

        public string FingerprintJson { get; set; } = string.Empty;

        public string ApprovedUtc { get; set; } = string.Empty;
    }
}
