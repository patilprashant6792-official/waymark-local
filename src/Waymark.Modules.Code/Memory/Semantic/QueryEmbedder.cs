using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// Embeds a search query with the active provider (purpose Query), keeping the last 64 answers by provider, model, dimension and query text, so asking the same thing twice costs
/// one request. Throws <see cref="EmbeddingException"/> for any provider failure; the caller falls back to words.
/// </summary>
internal sealed class QueryEmbedder(IEmbeddingProviders providers, IMemorySettings settings)
{
    private const int Capacity = 64;

    private readonly LinkedList<(string Key, float[] Vector)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, float[] Vector)>> _byKey = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public async Task<float[]> EmbedAsync(MemorySettings active, string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(query);
        var key = $"{active.ModelId}\n{query}";
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Vector;
            }
        }

        var provider = providers.Find(active.Provider) ?? throw new EmbeddingException(EmbeddingFailure.BadRequest, $"Unknown provider '{active.Provider}'.");
        var apiKey = await settings.GetKeyAsync(active.Provider, ct).ConfigureAwait(false)
            ?? throw new EmbeddingException(EmbeddingFailure.Unauthorized, $"No {provider.DisplayName} key is saved.");
        var batch = await provider.EmbedAsync([query], EmbeddingPurpose.Query, active.Dimension, active.Model, apiKey, ct).ConfigureAwait(false);
        var vector = batch.Vectors[0];
        lock (_gate)
        {
            if (!_byKey.ContainsKey(key))
            {
                _byKey[key] = _order.AddFirst((key, vector));
                while (_order.Count > Capacity)
                {
                    _byKey.Remove(_order.Last!.Value.Key);
                    _order.RemoveLast();
                }
            }
        }

        return vector;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _byKey.Clear();
        }
    }
}
