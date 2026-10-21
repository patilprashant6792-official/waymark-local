using Dapper;
using Waymark.Core.Vault;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class VaultStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IVaultStore
{
    public async Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<SecretRow>(new CommandDefinition(
            "SELECT id, name, hint, created_utc, updated_utc, last_used_utc FROM vault_secrets ORDER BY name",
            cancellationToken: ct));
        return rows.Select(r => new VaultSecret(
            r.Id,
            r.Name,
            r.Hint,
            Timestamp.Parse(r.CreatedUtc),
            Timestamp.Parse(r.UpdatedUtc),
            r.LastUsedUtc is null ? null : Timestamp.Parse(r.LastUsedUtc))).ToList();
    }

    public async Task<string?> GetProtectedAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT protected_value FROM vault_secrets WHERE name = @name",
            new { name },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ListProtectedAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<(string Name, string Protected)>(new CommandDefinition(
            "SELECT name AS Name, protected_value AS Protected FROM vault_secrets",
            cancellationToken: ct));
        return rows.Select(r => new KeyValuePair<string, string>(r.Name, r.Protected)).ToList();
    }

    public Task<bool> UpsertAsync(string name, string protectedValue, string hint, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        ArgumentNullException.ThrowIfNull(hint);
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var stamp = Timestamp.Format(now);
            var updated = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE vault_secrets SET protected_value = @protectedValue, hint = @hint, updated_utc = @stamp WHERE name = @name",
                new { name, protectedValue, hint, stamp },
                tx,
                cancellationToken: k));
            if (updated > 0)
            {
                return false;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO vault_secrets (name, protected_value, hint, created_utc, updated_utc) VALUES (@name, @protectedValue, @hint, @stamp, @stamp)",
                new { name, protectedValue, hint, stamp },
                tx,
                cancellationToken: k));
            return true;
        }, ct);
    }

    public Task<bool> MarkUsedAsync(string name, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return writes.ExecuteAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE vault_secrets SET last_used_utc = @stamp WHERE name = @name",
                new { name, stamp = Timestamp.Format(now) },
                tx,
                cancellationToken: k)) > 0, ct);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return writes.ExecuteAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM vault_secrets WHERE name = @name",
                new { name },
                tx,
                cancellationToken: k)) > 0, ct);
    }

    private sealed class SecretRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Hint { get; set; } = string.Empty;

        public string CreatedUtc { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;

        public string? LastUsedUtc { get; set; }
    }
}
