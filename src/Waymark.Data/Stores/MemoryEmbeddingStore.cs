using Dapper;
using Waymark.Core.Memory;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

/// <summary>The vectors of notes and flows (memory_embeddings). Writes go through the write queue; reads open their own connection.</summary>
public sealed class MemoryEmbeddingStore(ISqliteConnectionFactory connections, IWriteQueue writes) : IMemoryEmbeddingStore
{
    public Task UpsertAsync(IReadOnlyList<StoredEmbedding> rows, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        var stamp = Timestamp.Format(now);
        return writes.RunAsync(async (connection, tx, k) =>
        {
            foreach (var row in rows)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO memory_embeddings (workspace_id, item_kind, item_id, model_id, dimension, text_hash, vector, created_utc) " +
                    "VALUES (@workspaceId, @kind, @itemId, @modelId, @dimension, @textHash, @vector, @stamp) " +
                    "ON CONFLICT (item_kind, item_id, model_id) DO UPDATE SET workspace_id = excluded.workspace_id, dimension = excluded.dimension, " +
                    "text_hash = excluded.text_hash, vector = excluded.vector, created_utc = excluded.created_utc",
                    new
                    {
                        workspaceId = row.WorkspaceId,
                        kind = KindText(row.Kind),
                        itemId = row.ItemId,
                        modelId = row.ModelId,
                        dimension = row.Dimension,
                        textHash = row.TextHash,
                        vector = EmbeddingVectors.ToBlob(row.Vector),
                        stamp,
                    },
                    tx,
                    cancellationToken: k));
            }
        }, ct);
    }

    public Task DeleteItemAsync(EmbeddingItemKind kind, long itemId, CancellationToken ct) =>
        writes.RunAsync((connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM memory_embeddings WHERE item_kind = @kind AND item_id = @itemId",
            new { kind = KindText(kind), itemId },
            tx,
            cancellationToken: k)), ct);

    public Task<int> DeleteWorkspaceAsync(long workspaceId, CancellationToken ct) =>
        writes.ExecuteAsync((connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM memory_embeddings WHERE workspace_id = @workspaceId",
            new { workspaceId },
            tx,
            cancellationToken: k)), ct);

    public Task<int> DeleteAllAsync(CancellationToken ct) =>
        writes.ExecuteAsync((connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM memory_embeddings",
            transaction: tx,
            cancellationToken: k)), ct);

    public Task<int> DeleteOtherModelsAsync(long workspaceId, string keepModelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keepModelId);
        return writes.ExecuteAsync((connection, tx, k) => connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM memory_embeddings WHERE workspace_id = @workspaceId AND model_id <> @keepModelId",
            new { workspaceId, keepModelId },
            tx,
            cancellationToken: k)), ct);
    }

    public Task<int> DeleteMissingAsync(long workspaceId, string modelId, IReadOnlyCollection<(EmbeddingItemKind Kind, long ItemId)> keep, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(keep);
        var wanted = keep.Select(item => (KindText(item.Kind), item.ItemId)).ToHashSet();
        return writes.ExecuteAsync(async (connection, tx, k) =>
        {
            var stored = await connection.QueryAsync<KeyRow>(new CommandDefinition(
                "SELECT item_kind, item_id FROM memory_embeddings WHERE workspace_id = @workspaceId AND model_id = @modelId",
                new { workspaceId, modelId },
                tx,
                cancellationToken: k));
            var removed = 0;
            foreach (var row in stored)
            {
                if (wanted.Contains((row.ItemKind, row.ItemId)))
                {
                    continue;
                }

                removed += await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM memory_embeddings WHERE item_kind = @kind AND item_id = @itemId AND model_id = @modelId",
                    new { kind = row.ItemKind, itemId = row.ItemId, modelId },
                    tx,
                    cancellationToken: k));
            }

            return removed;
        }, ct);
    }

    public async Task<IReadOnlyList<EmbeddingStamp>> ListStampsAsync(long workspaceId, string modelId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<StampRow>(new CommandDefinition(
            "SELECT item_kind, item_id, text_hash FROM memory_embeddings WHERE workspace_id = @workspaceId AND model_id = @modelId",
            new { workspaceId, modelId },
            cancellationToken: ct));
        return [.. rows.Select(row => new EmbeddingStamp(KindOf(row.ItemKind), row.ItemId, row.TextHash))];
    }

    public async Task<IReadOnlyList<StoredEmbedding>> LoadAsync(long workspaceId, string modelId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VectorRow>(new CommandDefinition(
            "SELECT workspace_id, item_kind, item_id, model_id, dimension, text_hash, vector FROM memory_embeddings WHERE workspace_id = @workspaceId AND model_id = @modelId",
            new { workspaceId, modelId },
            cancellationToken: ct));
        return [.. rows.Select(row => new StoredEmbedding(
            row.WorkspaceId,
            KindOf(row.ItemKind),
            row.ItemId,
            row.ModelId,
            (int)row.Dimension,
            row.TextHash,
            EmbeddingVectors.FromBlob(row.Vector)))];
    }

    public async Task<int> CountAsync(string modelId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM memory_embeddings WHERE model_id = @modelId",
            new { modelId },
            cancellationToken: ct));
    }

    private static string KindText(EmbeddingItemKind kind) => kind == EmbeddingItemKind.Flow ? "flow" : "note";

    private static EmbeddingItemKind KindOf(string text) => text == "flow" ? EmbeddingItemKind.Flow : EmbeddingItemKind.Note;

    private sealed class KeyRow
    {
        public string ItemKind { get; set; } = string.Empty;

        public long ItemId { get; set; }
    }

    private sealed class StampRow
    {
        public string ItemKind { get; set; } = string.Empty;

        public long ItemId { get; set; }

        public string TextHash { get; set; } = string.Empty;
    }

    private sealed class VectorRow
    {
        public long WorkspaceId { get; set; }

        public string ItemKind { get; set; } = string.Empty;

        public long ItemId { get; set; }

        public string ModelId { get; set; } = string.Empty;

        public long Dimension { get; set; }

        public string TextHash { get; set; } = string.Empty;

        public byte[] Vector { get; set; } = [];
    }
}
