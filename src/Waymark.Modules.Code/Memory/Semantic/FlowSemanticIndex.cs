using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// <see cref="IFlowSemanticIndex"/> over the shared memory index, so anything written against the old flow-only contract keeps working.
/// Indexing is the sync's job (it follows every FlowChanged), so <see cref="IndexAsync"/> and <see cref="RebuildAsync"/> only make sure a pass is due;
/// <see cref="SearchAsync"/> is the meaning leg of <see cref="MemorySearch"/> limited to flows.
/// </summary>
internal sealed class FlowSemanticIndex(
    IMemorySettings settings,
    IMemoryEmbeddingStore store,
    MemoryVectorCache cache,
    QueryEmbedder queries,
    IMemoryIndexStatus status) : IFlowSemanticIndex
{
    public bool IsEnabled => status.Current.State != MemoryIndexState.Off;

    // The sync already listens to FlowChanged; nothing more to do here.
    public Task IndexAsync(FlowDocument flow, CancellationToken ct) => Task.CompletedTask;

    public async Task RemoveAsync(long workspaceId, long flowId, CancellationToken ct)
    {
        await store.DeleteItemAsync(EmbeddingItemKind.Flow, flowId, ct).ConfigureAwait(false);
        cache.Remove(workspaceId, EmbeddingItemKind.Flow, flowId);
    }

    public async Task<IReadOnlyList<FlowSemanticHit>> SearchAsync(long workspaceId, string query, int take, CancellationToken ct)
    {
        var active = await settings.GetAsync(ct).ConfigureAwait(false);
        if (!active.IsActive || await settings.IsExcludedAsync(workspaceId, ct).ConfigureAwait(false))
        {
            return [];
        }

        var vector = await queries.EmbedAsync(active, query, ct).ConfigureAwait(false);
        var hits = await cache.TopAsync(workspaceId, active.ModelId, vector, take * 4, ct).ConfigureAwait(false);
        return [.. hits.Where(hit => hit.Kind == EmbeddingItemKind.Flow).Take(take).Select(hit => new FlowSemanticHit(hit.ItemId, hit.Score))];
    }

    // The sync compares every flow's text hash with what is stored, so a rebuild is the next pass.
    public Task RebuildAsync(long workspaceId, CancellationToken ct) => Task.CompletedTask;
}
