using System.Data;
using Dapper;
using Waymark.Core.NuGetIndex;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class NuGetCacheStore(ISqliteConnectionFactory connections, IWriteQueue writes) : INuGetCacheStore
{
    public async Task<NuGetCacheOverview> GetOverviewAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var packages = (await connection.QueryAsync<PackageRow>(new CommandDefinition(
            "SELECT id, package_id, version, tfm, state, schema_version, symbol_count, type_count, started_utc, indexed_utc, error " +
            "FROM nuget_packages ORDER BY package_id, version, tfm",
            cancellationToken: ct))).ToList();
        var sizes = (await connection.QueryAsync<SizeRow>(new CommandDefinition(
            "SELECT package_row_id, SUM(length(name) + length(full_name) + length(namespace) + IFNULL(length(declaring_type), 0) + length(declaration) + length(card) + " +
            "length(tokens) + length(summary) + length(related) + length(d_sig) + length(d_methods) + length(d_props) + length(d_data) + length(d_doc)) AS bytes " +
            "FROM nuget_symbols GROUP BY package_row_id",
            cancellationToken: ct))).ToDictionary(row => row.PackageRowId, row => row.Bytes);
        var cached = (await connection.QueryAsync<CachedRow>(new CommandDefinition(
            "SELECT cache_key, resolved_version, length(payload) AS bytes, stored_utc FROM nuget_metadata_cache ORDER BY cache_key",
            cancellationToken: ct))).ToList();

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<NuGetCacheEntry>(packages.Count);
        foreach (var row in packages)
        {
            var baseKey = NuGetCacheKeys.Base(row.PackageId, row.Version, row.Tfm);
            var own = cached
                .Where(item => item.CacheKey == baseKey || item.CacheKey.StartsWith(baseKey + NuGetCacheKeys.ExtrasMarker, StringComparison.Ordinal))
                .ToList();
            foreach (var item in own)
            {
                claimed.Add(item.CacheKey);
            }

            var extras = own.Select(item => NuGetCacheKeys.ExtrasOf(item.CacheKey)).FirstOrDefault(found => found.Count > 0) ?? [];
            entries.Add(new NuGetCacheEntry(
                ToIndex(row),
                sizes.GetValueOrDefault(row.Id),
                own.Count,
                own.Sum(item => item.Bytes),
                own.Count == 0 ? null : own.Max(item => Timestamp.Parse(item.StoredUtc)),
                extras));
        }

        var other = cached
            .Where(item => !claimed.Contains(item.CacheKey))
            .Select(item => new NuGetCachedResult(item.CacheKey, item.ResolvedVersion, item.Bytes, Timestamp.Parse(item.StoredUtc)))
            .ToList();

        var stats = new NuGetCacheStats(
            entries.Count(entry => entry.Package.State == NuGetIndexState.Ready),
            entries.Count(entry => entry.Package.State == NuGetIndexState.Failed),
            entries.Count(entry => entry.Package.State == NuGetIndexState.Building),
            entries.Where(entry => entry.Package.State == NuGetIndexState.Ready).Sum(entry => (long)entry.Package.TypeCount),
            entries.Where(entry => entry.Package.State == NuGetIndexState.Ready).Sum(entry => (long)entry.Package.SymbolCount),
            entries.Sum(entry => entry.IndexBytes),
            cached.Count,
            cached.Sum(item => item.Bytes),
            entries.Select(entry => entry.Package.IndexedUtc).Max());
        return new NuGetCacheOverview(stats, entries, other);
    }

    public Task<NuGetClearResult> ClearPackageAsync(NuGetPackageKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        return writes.ExecuteAsync<NuGetClearResult>(async (connection, tx, k) => await ClearOneAsync(connection, tx, key, k), ct);
    }

    public Task<NuGetClearResult> ClearByStateAsync(NuGetIndexState state, CancellationToken ct) =>
        writes.ExecuteAsync<NuGetClearResult>(async (connection, tx, k) =>
        {
            var keys = (await connection.QueryAsync<PackageRow>(new CommandDefinition(
                "SELECT id, package_id, version, tfm, state, schema_version, symbol_count, type_count, started_utc, indexed_utc, error FROM nuget_packages WHERE state = @state",
                new { state = state.ToString().ToLowerInvariant() },
                tx,
                cancellationToken: k))).ToList();
            var packages = 0;
            var results = 0;
            foreach (var row in keys)
            {
                var one = await ClearOneAsync(connection, tx, new NuGetPackageKey(row.PackageId, row.Version, row.Tfm), k);
                packages += one.Packages;
                results += one.CachedResults;
            }

            return new NuGetClearResult(packages, results);
        }, ct);

    public Task<NuGetClearResult> ClearAllAsync(CancellationToken ct) =>
        writes.ExecuteAsync<NuGetClearResult>(async (connection, tx, k) =>
        {
            var packages = await connection.ExecuteAsync(new CommandDefinition("DELETE FROM nuget_packages", transaction: tx, cancellationToken: k));
            var results = await connection.ExecuteAsync(new CommandDefinition("DELETE FROM nuget_metadata_cache", transaction: tx, cancellationToken: k));
            return new NuGetClearResult(packages, results);
        }, ct);

    public Task<bool> RemoveCachedResultAsync(string cacheKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM nuget_metadata_cache WHERE cache_key = @cacheKey",
                new { cacheKey },
                tx,
                cancellationToken: k)) > 0, ct);
    }

    // The substr comparison (not LIKE) keeps '_' in a package id from acting as a wildcard.
    private static async Task<NuGetClearResult> ClearOneAsync(IDbConnection connection, IDbTransaction tx, NuGetPackageKey key, CancellationToken ct)
    {
        var baseKey = NuGetCacheKeys.Base(key.PackageId, key.Version, key.TargetFramework);
        var prefix = baseKey + NuGetCacheKeys.ExtrasMarker;
        var latestKey = NuGetCacheKeys.Base(key.PackageId, null, key.TargetFramework);

        var packages = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM nuget_packages WHERE package_id = @PackageId AND version = @Version AND tfm = @TargetFramework",
            new { key.PackageId, key.Version, key.TargetFramework },
            tx,
            cancellationToken: ct));
        var results = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM nuget_metadata_cache WHERE cache_key = @baseKey OR substr(cache_key, 1, @prefixLength) = @prefix " +
            "OR (cache_key = @latestKey AND resolved_version = @Version COLLATE NOCASE)",
            new { baseKey, prefix, prefixLength = prefix.Length, latestKey, key.Version },
            tx,
            cancellationToken: ct));
        return new NuGetClearResult(packages, results);
    }

    private static NuGetPackageIndex ToIndex(PackageRow row) =>
        new(
            row.Id,
            new NuGetPackageKey(row.PackageId, row.Version, row.Tfm),
            Enum.Parse<NuGetIndexState>(row.State, ignoreCase: true),
            (int)row.SchemaVersion,
            (int)row.SymbolCount,
            (int)row.TypeCount,
            Timestamp.Parse(row.StartedUtc),
            Timestamp.ParseNullable(row.IndexedUtc),
            row.Error);

    private sealed class PackageRow
    {
        public long Id { get; set; }

        public string PackageId { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Tfm { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public long SchemaVersion { get; set; }

        public long SymbolCount { get; set; }

        public long TypeCount { get; set; }

        public string StartedUtc { get; set; } = string.Empty;

        public string? IndexedUtc { get; set; }

        public string? Error { get; set; }
    }

    private sealed class SizeRow
    {
        public long PackageRowId { get; set; }

        public long Bytes { get; set; }
    }

    private sealed class CachedRow
    {
        public string CacheKey { get; set; } = string.Empty;

        public string ResolvedVersion { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public string StoredUtc { get; set; } = string.Empty;
    }
}
