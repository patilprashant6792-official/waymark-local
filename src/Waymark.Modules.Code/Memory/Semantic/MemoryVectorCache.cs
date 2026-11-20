using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>One item close to a query, best first. The score is the cosine similarity (vectors are normalised).</summary>
internal sealed record VectorHit(EmbeddingItemKind Kind, long ItemId, double Score);

/// <summary>
/// The vectors of one workspace and model in memory, loaded from the store on first use and kept in step by the sync (upsert, remove, forget).
/// Search is a brute-force dot product: a workspace has hundreds of items, not millions, and this needs no extra index or package.
/// </summary>
internal sealed class MemoryVectorCache(IMemoryEmbeddingStore store)
{
    private readonly Dictionary<(long WorkspaceId, string ModelId), Dictionary<(EmbeddingItemKind, long), float[]>> _loaded = [];
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly Lock _gate = new();

    public async Task<IReadOnlyList<VectorHit>> TopAsync(long workspaceId, string modelId, float[] query, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var vectors = await GetAsync(workspaceId, modelId, ct).ConfigureAwait(false);
        List<VectorHit> hits;
        lock (_gate)
        {
            hits = new List<VectorHit>(vectors.Count);
            foreach (var ((kind, id), vector) in vectors)
            {
                hits.Add(new VectorHit(kind, id, EmbeddingVectors.Dot(query, vector)));
            }
        }

        return Rank(hits, take);
    }

    /// <summary>The best <paramref name="take"/> hits, highest score first; ties by kind then id so the order is stable.</summary>
    internal static IReadOnlyList<VectorHit> Rank(IEnumerable<VectorHit> hits, int take) =>
        [.. hits.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Kind).ThenBy(hit => hit.ItemId).Take(Math.Max(0, take))];

    /// <summary>Adds or replaces vectors in a workspace that is loaded; one that is not loaded reads them from the store when first searched.</summary>
    public void Upsert(IReadOnlyList<StoredEmbedding> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_gate)
        {
            foreach (var row in rows)
            {
                if (_loaded.TryGetValue((row.WorkspaceId, row.ModelId), out var vectors))
                {
                    vectors[(row.Kind, row.ItemId)] = row.Vector;
                }
            }
        }
    }

    public void Remove(long workspaceId, EmbeddingItemKind kind, long itemId)
    {
        lock (_gate)
        {
            foreach (var ((ws, _), vectors) in _loaded)
            {
                if (ws == workspaceId)
                {
                    vectors.Remove((kind, itemId));
                }
            }
        }
    }

    public void ForgetWorkspace(long workspaceId)
    {
        lock (_gate)
        {
            foreach (var key in _loaded.Keys.Where(key => key.WorkspaceId == workspaceId).ToList())
            {
                _loaded.Remove(key);
            }
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            _loaded.Clear();
        }
    }

    private async Task<Dictionary<(EmbeddingItemKind, long), float[]>> GetAsync(long workspaceId, string modelId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue((workspaceId, modelId), out var found))
            {
                return found;
            }
        }

        await _loading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_loaded.TryGetValue((workspaceId, modelId), out var found))
                {
                    return found;
                }
            }

            var rows = await store.LoadAsync(workspaceId, modelId, ct).ConfigureAwait(false);
            var vectors = rows.ToDictionary(row => (row.Kind, row.ItemId), row => row.Vector);
            lock (_gate)
            {
                _loaded[(workspaceId, modelId)] = vectors;
            }

            return vectors;
        }
        finally
        {
            _loading.Release();
        }
    }
}
