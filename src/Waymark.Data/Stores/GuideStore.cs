using Dapper;
using Microsoft.Data.Sqlite;
using Waymark.Core.Guides;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class GuideStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IGuideStore
{
    private const string UpsertOverrideSql =
        "INSERT INTO tool_guides (module, content, changed_utc, changed_by) VALUES (@module, @content, @changed, @changedBy) " +
        "ON CONFLICT(module) DO UPDATE SET content = excluded.content, changed_utc = excluded.changed_utc, changed_by = excluded.changed_by";

    private const string UpsertDeliverySql =
        "INSERT INTO tool_guide_deliveries (client_id, module, content_hash, delivered_utc) VALUES (@clientId, @module, @hash, @delivered) " +
        "ON CONFLICT(client_id, module) DO UPDATE SET content_hash = excluded.content_hash, delivered_utc = excluded.delivered_utc";

    public async Task<IReadOnlyList<GuideOverride>> ListOverridesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<OverrideRow>(new CommandDefinition(
            "SELECT module, content, changed_utc, changed_by FROM tool_guides ORDER BY module",
            cancellationToken: ct));
        return rows.Select(r => new GuideOverride(r.Module, r.Content, Timestamp.Parse(r.ChangedUtc), r.ChangedBy)).ToList();
    }

    public Task SetOverrideAsync(string module, string content, string changedBy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        return writes.RunAsync(
            (connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
                UpsertOverrideSql,
                new { module, content, changed = Timestamp.Format(now), changedBy },
                tx,
                cancellationToken: k)),
            ct);
    }

    public async Task<bool> DeleteOverrideAsync(string module, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        var removed = await writes.ExecuteAsync(
            (connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tool_guides WHERE module = @module",
                new { module },
                tx,
                cancellationToken: k)),
            ct);
        return removed > 0;
    }

    public async Task<IReadOnlyList<GuideDelivery>> ListDeliveriesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<DeliveryRow>(new CommandDefinition(
            "SELECT client_id, module, content_hash, delivered_utc FROM tool_guide_deliveries ORDER BY client_id, module",
            cancellationToken: ct));
        return rows.Select(r => new GuideDelivery(r.ClientId, r.Module, r.ContentHash, Timestamp.Parse(r.DeliveredUtc))).ToList();
    }

    public Task RecordDeliveryAsync(string clientId, string module, string contentHash, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return writes.RunAsync(
            (connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
                UpsertDeliverySql,
                new { clientId, module, hash = contentHash, delivered = Timestamp.Format(now) },
                tx,
                cancellationToken: k)),
            ct);
    }

    private sealed class OverrideRow
    {
        public string Module { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public string ChangedUtc { get; set; } = string.Empty;

        public string ChangedBy { get; set; } = string.Empty;
    }

    private sealed class DeliveryRow
    {
        public string ClientId { get; set; } = string.Empty;

        public string Module { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string DeliveredUtc { get; set; } = string.Empty;
    }
}
