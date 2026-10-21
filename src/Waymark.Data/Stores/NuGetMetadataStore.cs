using Dapper;
using Waymark.Core.NuGetIndex;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class NuGetMetadataStore(ISqliteConnectionFactory connections, IWriteQueue writes) : INuGetMetadataStore
{
    public async Task<NuGetMetadataEntry?> GetAsync(string cacheKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT cache_key, resolved_version, payload, stored_utc FROM nuget_metadata_cache WHERE cache_key = @cacheKey",
            new { cacheKey },
            cancellationToken: ct));
        return row is null ? null : new NuGetMetadataEntry(row.CacheKey, row.ResolvedVersion, row.Payload, Timestamp.Parse(row.StoredUtc));
    }

    public Task SetAsync(string cacheKey, string resolvedVersion, string payload, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedVersion);
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return writes.RunAsync(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO nuget_metadata_cache (cache_key, resolved_version, payload, stored_utc) VALUES (@cacheKey, @resolvedVersion, @payload, @stored) " +
                "ON CONFLICT (cache_key) DO UPDATE SET resolved_version = excluded.resolved_version, payload = excluded.payload, stored_utc = excluded.stored_utc",
                new { cacheKey, resolvedVersion, payload, stored = Timestamp.Format(now) },
                tx,
                cancellationToken: k)),
            ct);
    }

    public Task<bool> RemoveAsync(string cacheKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nuget_metadata_cache WHERE cache_key = @cacheKey",
                new { cacheKey },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    private sealed class Row
    {
        public string CacheKey { get; set; } = string.Empty;

        public string ResolvedVersion { get; set; } = string.Empty;

        public string Payload { get; set; } = string.Empty;

        public string StoredUtc { get; set; } = string.Empty;
    }
}
