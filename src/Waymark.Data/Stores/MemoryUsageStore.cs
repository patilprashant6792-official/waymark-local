using Dapper;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>
/// The last_used_utc columns of feature_maps and notes. Writes are one job on the write queue per batch; the guard in the WHERE clause keeps a time from moving back,
/// so a late or repeated batch changes nothing. updated_utc is never touched: it orders the guide's flow index and the lists.
/// </summary>
public sealed class MemoryUsageStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IMemoryUsageStore
{
    private const int MaxIdsPerQuery = 500;

    private const string TouchFlow =
        "UPDATE feature_maps SET last_used_utc = @Stamp WHERE id = @Id AND workspace_id = @WorkspaceId AND (last_used_utc IS NULL OR last_used_utc < @Stamp)";

    private const string TouchNote =
        "UPDATE notes SET last_used_utc = @Stamp WHERE id = @Id AND workspace_id = @WorkspaceId AND retired_utc IS NULL AND superseded_utc IS NULL " +
        "AND (last_used_utc IS NULL OR last_used_utc < @Stamp)";

    public Task TouchAsync(IReadOnlyList<MemoryUse> uses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uses);
        if (uses.Count == 0)
        {
            return Task.CompletedTask;
        }

        var flows = uses.Where(use => use.Kind == MemoryItemKind.Flow).Select(Row).ToArray();
        var notes = uses.Where(use => use.Kind == MemoryItemKind.Note).Select(Row).ToArray();
        return writes.RunAsync(async (connection, tx, k) =>
        {
            if (flows.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(TouchFlow, flows, tx, cancellationToken: k));
            }

            if (notes.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(TouchNote, notes, tx, cancellationToken: k));
            }
        }, ct);
    }

    public async Task<IReadOnlyDictionary<long, DateTimeOffset>> GetAsync(long workspaceId, MemoryItemKind kind, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<long, DateTimeOffset>();
        if (ids.Count == 0)
        {
            return result;
        }

        var table = kind == MemoryItemKind.Flow ? "feature_maps" : "notes";
        await using var connection = await connections.OpenAsync(ct);
        foreach (var chunk in ids.Distinct().Chunk(MaxIdsPerQuery))
        {
            var rows = await connection.QueryAsync<UsedRow>(new CommandDefinition(
                $"SELECT id, last_used_utc FROM {table} WHERE workspace_id = @workspaceId AND id IN @ids AND last_used_utc IS NOT NULL",
                new { workspaceId, ids = chunk },
                cancellationToken: ct));
            foreach (var row in rows)
            {
                result[row.Id] = Timestamp.Parse(row.LastUsedUtc);
            }
        }

        return result;
    }

    private static TouchRow Row(MemoryUse use) => new(use.Id, use.WorkspaceId, Timestamp.Format(use.UsedUtc));

    private sealed record TouchRow(long Id, long WorkspaceId, string Stamp);

    private sealed class UsedRow
    {
        public long Id { get; set; }

        public string LastUsedUtc { get; set; } = string.Empty;
    }
}
