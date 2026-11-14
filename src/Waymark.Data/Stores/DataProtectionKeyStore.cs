using Dapper;
using Waymark.Core.Vault;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class DataProtectionKeyStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IDataProtectionKeyStore
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT xml FROM data_protection_keys ORDER BY id",
            cancellationToken: ct));
        return rows.ToList();
    }

    public Task AddAsync(string friendlyName, string xml, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        return writes.RunAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO data_protection_keys (friendly_name, xml, created_utc) VALUES (@friendlyName, @xml, @stamp)",
                new { friendlyName, xml, stamp = Timestamp.Format(now) },
                tx,
                cancellationToken: k)), ct);
    }
}
