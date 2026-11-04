using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// Search by meaning over flows: NOT BUILT YET. Every method does nothing and returns a safe default, so everything that saves, changes, deletes, imports or lists flows is already wired to it.
/// The real index replaces this one class (register it instead in <c>CodeModule</c>) and nothing else has to change.
/// <para>
/// Where it is called from: <see cref="FlowSemanticSync"/> (after every save, delete and import, from the <see cref="Waymark.Core.Events.FlowChanged"/> event) and
/// <see cref="FlowRecallService"/> (recall list with a query, when <see cref="IsEnabled"/> is true).
/// What to embed is composed by <see cref="FlowSemanticText"/>: the flow's name, summary, the reason it is kept next to another flow, and each step as Type.Member with its note.
/// </para>
/// </summary>
internal sealed class NoFlowSemanticIndex : IFlowSemanticIndex
{
    /// <summary>False: callers skip the index entirely, so nothing changes for the owner or for the agent.</summary>
    public bool IsEnabled => false;

    public Task IndexAsync(FlowDocument flow, CancellationToken ct)
    {
        // FUTURE: embed flow.Text and upsert it by (WorkspaceId, FlowId). Skip when the stored Version is already >= flow.Version (a late event must not overwrite a newer flow).
        // Output when built: one row in a flow_embeddings table (workspace_id, flow_id, version, vector, model, updated_utc). Returns nothing.
        return Task.CompletedTask;
    }

    public Task RemoveAsync(long workspaceId, long flowId, CancellationToken ct)
    {
        // FUTURE: delete the embedding row of the flow.
        // Output when built: zero or one row removed. Returns nothing.
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FlowSemanticHit>> SearchAsync(long workspaceId, string query, int take, CancellationToken ct)
    {
        // FUTURE: embed the query, take the nearest vectors of the workspace by cosine similarity, keep those above a threshold, at most `take`.
        // Output when built: [ new FlowSemanticHit(FlowId: 41, Score: 0.83), new FlowSemanticHit(FlowId: 12, Score: 0.71) ], best first.
        return Task.FromResult<IReadOnlyList<FlowSemanticHit>>([]);
    }

    public Task RebuildAsync(long workspaceId, CancellationToken ct)
    {
        // FUTURE: read every flow of the workspace from IFeatureMapStore, compose each with FlowSemanticText and embed it again (a new model, or an import that brought many flows).
        // Output when built: the number of flows embedded, written to the log at Information. Returns nothing.
        return Task.CompletedTask;
    }
}
