using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Tools;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class ToolSettingsStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IToolSettingsStore
{
    private const string UpsertSql =
        "INSERT INTO tool_settings (tool_name, enabled, changed_utc, changed_by) VALUES (@toolName, @enabled, @changed, @changedBy) " +
        "ON CONFLICT(tool_name) DO UPDATE SET enabled = excluded.enabled, changed_utc = excluded.changed_utc, changed_by = excluded.changed_by";

    public async Task<IReadOnlyList<ToolSetting>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<SettingRow>(new CommandDefinition(
            "SELECT tool_name, enabled, changed_utc, changed_by FROM tool_settings ORDER BY tool_name",
            cancellationToken: ct));
        return rows.Select(r => new ToolSetting(r.ToolName, r.Enabled != 0, Timestamp.Parse(r.ChangedUtc), r.ChangedBy)).ToList();
    }

    public Task SetAsync(string toolName, bool enabled, string changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        return writes.RunAsync((connection, tx, k) => UpsertAsync(connection, tx, toolName, enabled, changedBy, now, k), ct);
    }

    public Task SetManyAsync(IReadOnlyCollection<string> toolNames, bool enabled, string changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        if (toolNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Tool names must not be empty.", nameof(toolNames));
        }

        return writes.RunAsync(async (connection, tx, k) =>
        {
            foreach (var toolName in toolNames)
            {
                await UpsertAsync(connection, tx, toolName, enabled, changedBy, now, k);
            }
        }, ct);
    }

    private static Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string toolName,
        bool enabled,
        string changedBy,
        DateTimeOffset now,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            UpsertSql,
            new { toolName, enabled = enabled ? 1 : 0, changed = Timestamp.Format(now), changedBy },
            tx,
            cancellationToken: ct));

    private sealed class SettingRow
    {
        public string ToolName { get; set; } = string.Empty;

        public long Enabled { get; set; }

        public string ChangedUtc { get; set; } = string.Empty;

        public string ChangedBy { get; set; } = string.Empty;
    }
}
