using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Settings;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class ConfigStore(ISqliteConnectionFactory connections, IWriteQueue writes, TimeProvider time) : IConfigStore
{
    private const string SelectSql =
        "SELECT value_json, version, updated_utc FROM settings WHERE scope_kind = @kind AND scope_id = @scopeId AND key = @key";

    private const string InsertSql =
        "INSERT INTO settings (scope_kind, scope_id, key, value_json, version, updated_utc) VALUES (@kind, @scopeId, @key, @value, 1, @now)";

    public async Task<SettingValue?> GetAsync(SettingScope scope, string key, CancellationToken ct)
    {
        ValidateKey(key);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<SettingRow>(
            new CommandDefinition(SelectSql, KeyParameters(scope, key), cancellationToken: ct));
        return row is null ? null : ToValue(scope, key, row);
    }

    public Task<SettingValue> SetAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct)
    {
        Validate(key, valueJson, changedBy);
        var stamp = Timestamp.Format(time.GetUtcNow());

        return writes.ExecuteAsync<SettingValue>(async (connection, tx, k) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<SettingRow>(
                new CommandDefinition(SelectSql, KeyParameters(scope, key), tx, cancellationToken: k));
            if (existing is not null && existing.ValueJson == valueJson)
            {
                return ToValue(scope, key, existing);
            }

            long version;
            if (existing is null)
            {
                version = 1;
                await connection.ExecuteAsync(new CommandDefinition(
                    InsertSql,
                    new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key, value = valueJson, now = stamp },
                    tx,
                    cancellationToken: k));
            }
            else
            {
                version = existing.Version + 1;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE settings SET value_json = @value, version = version + 1, updated_utc = @now WHERE scope_kind = @kind AND scope_id = @scopeId AND key = @key",
                    new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key, value = valueJson, now = stamp },
                    tx,
                    cancellationToken: k));
            }

            await InsertHistoryAsync(connection, tx, scope, key, existing?.ValueJson, valueJson, stamp, changedBy, k);
            return new SettingValue(scope, key, valueJson, version, Timestamp.Parse(stamp));
        }, ct);
    }

    public Task<bool> TryAddAsync(SettingScope scope, string key, string valueJson, string changedBy, CancellationToken ct)
    {
        Validate(key, valueJson, changedBy);
        var stamp = Timestamp.Format(time.GetUtcNow());

        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                InsertSql + " ON CONFLICT (scope_kind, scope_id, key) DO NOTHING",
                new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key, value = valueJson, now = stamp },
                tx,
                cancellationToken: k));
            if (inserted == 0)
            {
                return false;
            }

            await InsertHistoryAsync(connection, tx, scope, key, null, valueJson, stamp, changedBy, k);
            return true;
        }, ct);
    }

    public async Task<IReadOnlyList<SettingHistoryEntry>> GetHistoryAsync(SettingScope scope, string key, int limit, CancellationToken ct)
    {
        ValidateKey(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition(
            "SELECT id, old_json, new_json, changed_utc, changed_by FROM settings_history WHERE scope_kind = @kind AND scope_id = @scopeId AND key = @key ORDER BY id DESC LIMIT @limit",
            new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key, limit },
            cancellationToken: ct));

        return rows
            .Select(r => new SettingHistoryEntry(r.Id, scope, key, r.OldJson, r.NewJson, Timestamp.Parse(r.ChangedUtc), r.ChangedBy))
            .ToList();
    }

    private static Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        SettingScope scope,
        string key,
        string? oldJson,
        string newJson,
        string stamp,
        string changedBy,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings_history (scope_kind, scope_id, key, old_json, new_json, changed_utc, changed_by) VALUES (@kind, @scopeId, @key, @oldJson, @newJson, @now, @by)",
            new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key, oldJson, newJson, now = stamp, by = changedBy },
            tx,
            cancellationToken: ct));

    private static object KeyParameters(SettingScope scope, string key) =>
        new { kind = EnumText.ToDb(scope.Kind), scopeId = scope.Id, key };

    private static SettingValue ToValue(SettingScope scope, string key, SettingRow row) =>
        new(scope, key, row.ValueJson, row.Version, Timestamp.Parse(row.UpdatedUtc));

    private static void ValidateKey(string key) => ArgumentException.ThrowIfNullOrWhiteSpace(key);

    private static void Validate(string key, string valueJson, string changedBy)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(valueJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        try
        {
            using var document = JsonDocument.Parse(valueJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The value is not valid JSON.", nameof(valueJson), ex);
        }
    }

    private sealed class SettingRow
    {
        public string ValueJson { get; set; } = string.Empty;

        public long Version { get; set; }

        public string UpdatedUtc { get; set; } = string.Empty;
    }

    private sealed class HistoryRow
    {
        public long Id { get; set; }

        public string? OldJson { get; set; }

        public string NewJson { get; set; } = string.Empty;

        public string ChangedUtc { get; set; } = string.Empty;

        public string ChangedBy { get; set; } = string.Empty;
    }
}
