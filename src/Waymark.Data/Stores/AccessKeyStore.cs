using Dapper;
using Waymark.Core.Auth;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class AccessKeyStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IAccessKeyStore
{
    private const string Columns = "id, name, key_hint, scope, created_utc, expires_utc, last_used_utc, revoked_utc";

    public Task CreateAsync(NewAccessKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Name);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO api_keys (id, name, key_hash, key_hint, scope, created_utc, expires_utc) " +
                "VALUES (@id, @name, @hash, @hint, @scope, @created, @expires)",
                new
                {
                    id = key.Id,
                    name = key.Name,
                    hash = key.Hash,
                    hint = key.Hint,
                    scope = key.Scope,
                    created = Timestamp.Format(key.CreatedUtc),
                    expires = Timestamp.FormatNullable(key.ExpiresUtc)
                },
                tx,
                cancellationToken: k));
        }, ct);
    }

    public async Task<AccessKey?> FindActiveAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hash);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<KeyRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM api_keys WHERE key_hash = @hash AND revoked_utc IS NULL AND (expires_utc IS NULL OR expires_utc > @now)",
            new { hash, now = Timestamp.Format(now) },
            cancellationToken: ct));
        return row is null ? null : ToKey(row);
    }

    public async Task<IReadOnlyList<AccessKey>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<KeyRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM api_keys ORDER BY created_utc DESC, id",
            cancellationToken: ct));
        return rows.Select(ToKey).ToList();
    }

    public Task<bool> RevokeAsync(string id, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE api_keys SET revoked_utc = @now WHERE id = @id AND revoked_utc IS NULL",
                new { now = Timestamp.Format(now), id },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    public Task TouchAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE api_keys SET last_used_utc = @at WHERE id = @id",
                new { at = Timestamp.Format(at), id },
                tx,
                cancellationToken: k));
        }, ct);
    }

    private static AccessKey ToKey(KeyRow row) =>
        new(
            row.Id,
            row.Name,
            row.KeyHint,
            row.Scope,
            Timestamp.Parse(row.CreatedUtc),
            Timestamp.ParseNullable(row.ExpiresUtc),
            Timestamp.ParseNullable(row.LastUsedUtc),
            Timestamp.ParseNullable(row.RevokedUtc));

    private sealed class KeyRow
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string KeyHint { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;

        public string CreatedUtc { get; set; } = string.Empty;

        public string? ExpiresUtc { get; set; }

        public string? LastUsedUtc { get; set; }

        public string? RevokedUtc { get; set; }
    }
}
