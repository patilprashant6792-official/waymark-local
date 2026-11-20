using Microsoft.Extensions.Logging;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>What one pass over a workspace did.</summary>
internal sealed record IndexPass(int Embedded, int Pending, int Skipped);

/// <summary>
/// One pass of the index over one workspace with the active model: compose every item, compare its hash with what is stored, embed what is pending in batches,
/// store the vectors, drop the rows of items that are gone, and only when nothing is left pending drop the rows of other models (a model switch keeps the old vectors
/// until the new ones are complete). Provider failures other than a refused single item propagate to the sync, which decides on backoff.
/// </summary>
internal sealed class MemoryWorkspaceIndexer(
    MemoryItems items,
    IMemoryEmbeddingStore store,
    MemoryVectorCache cache,
    TimeProvider time,
    ILogger<MemoryWorkspaceIndexer> logger)
{
    // Items the provider refused (a 400 for that text alone), by kind, id and text hash: not sent again until the text changes.
    private readonly HashSet<(EmbeddingItemKind, long, string)> _refused = [];
    private readonly Lock _gate = new();

    public async Task<IReadOnlyList<MemoryItem>> PendingAsync(Workspace workspace, string modelId, CancellationToken ct)
    {
        var all = await items.ListAsync(workspace, ct).ConfigureAwait(false);
        var stamps = (await store.ListStampsAsync(workspace.Id, modelId, ct).ConfigureAwait(false))
            .ToDictionary(stamp => (stamp.Kind, stamp.ItemId), stamp => stamp.TextHash);
        return [.. all.Where(item => !stamps.TryGetValue((item.Kind, item.ItemId), out var hash) || !string.Equals(hash, item.Hash, StringComparison.Ordinal))];
    }

    /// <param name="progress">Called after every stored batch with the number embedded so far.</param>
    public async Task<IndexPass> RunAsync(
        Workspace workspace,
        MemorySettings active,
        IEmbeddingProvider provider,
        string apiKey,
        Func<int, ValueTask> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(progress);
        var modelId = active.ModelId;
        var all = await items.ListAsync(workspace, ct).ConfigureAwait(false);
        var keep = all.Select(item => (item.Kind, item.ItemId)).ToHashSet();
        var removed = await store.DeleteMissingAsync(workspace.Id, modelId, keep, ct).ConfigureAwait(false);
        if (removed > 0)
        {
            cache.ForgetWorkspace(workspace.Id);
        }

        var stamps = (await store.ListStampsAsync(workspace.Id, modelId, ct).ConfigureAwait(false))
            .ToDictionary(stamp => (stamp.Kind, stamp.ItemId), stamp => stamp.TextHash);
        var pending = all
            .Where(item => !stamps.TryGetValue((item.Kind, item.ItemId), out var hash) || !string.Equals(hash, item.Hash, StringComparison.Ordinal))
            .Where(item => !IsRefused(item))
            .ToList();
        var embedded = 0;
        var skipped = 0;
        foreach (var batch in pending.Chunk(provider.MaxBatchTexts))
        {
            ct.ThrowIfCancellationRequested();
            var (rows, refused) = await EmbedAsync(workspace.Id, batch, active, provider, apiKey, ct).ConfigureAwait(false);
            skipped += refused;
            if (rows.Count > 0)
            {
                await store.UpsertAsync(rows, time.GetUtcNow(), ct).ConfigureAwait(false);
                cache.Upsert(rows);
                embedded += rows.Count;
                await progress(embedded).ConfigureAwait(false);
            }
        }

        // Every item now has a vector of the active model (or was refused), so the rows of an earlier model are no longer needed.
        if (await store.DeleteOtherModelsAsync(workspace.Id, modelId, ct).ConfigureAwait(false) > 0)
        {
            cache.ForgetWorkspace(workspace.Id);
        }

        return new IndexPass(embedded, 0, skipped);
    }

    public void ForgetRefusals()
    {
        lock (_gate)
        {
            _refused.Clear();
        }
    }

    private bool IsRefused(MemoryItem item)
    {
        lock (_gate)
        {
            return _refused.Contains((item.Kind, item.ItemId, item.Hash));
        }
    }

    // A refused batch is split into single items, so one text the provider will not take does not hold back the others.
    private async Task<(List<StoredEmbedding> Rows, int Refused)> EmbedAsync(
        long workspaceId,
        IReadOnlyList<MemoryItem> batch,
        MemorySettings active,
        IEmbeddingProvider provider,
        string apiKey,
        CancellationToken ct)
    {
        try
        {
            var result = await provider.EmbedAsync([.. batch.Select(item => item.Text)], EmbeddingPurpose.Document, active.Dimension, active.Model, apiKey, ct).ConfigureAwait(false);
            return ([.. batch.Select((item, i) => Row(workspaceId, item, active, result.Vectors[i]))], 0);
        }
        catch (EmbeddingException ex) when (ex.Kind == EmbeddingFailure.BadRequest && batch.Count > 1)
        {
            var rows = new List<StoredEmbedding>();
            var refused = 0;
            foreach (var item in batch)
            {
                var (one, failed) = await EmbedAsync(workspaceId, [item], active, provider, apiKey, ct).ConfigureAwait(false);
                rows.AddRange(one);
                refused += failed;
            }

            return (rows, refused);
        }
        catch (EmbeddingException ex) when (ex.Kind == EmbeddingFailure.BadRequest)
        {
            var item = batch[0];
            lock (_gate)
            {
                _refused.Add((item.Kind, item.ItemId, item.Hash));
            }

            logger.LogWarning("Search by meaning skipped {Kind} {Id}: the provider refused it ({Reason}). It is tried again when its text changes.", item.Kind, item.ItemId, ex.Message);
            return ([], 1);
        }
    }

    private static StoredEmbedding Row(long workspaceId, MemoryItem item, MemorySettings active, float[] vector) =>
        new(workspaceId, item.Kind, item.ItemId, active.ModelId, active.Dimension, item.Hash, vector);
}
