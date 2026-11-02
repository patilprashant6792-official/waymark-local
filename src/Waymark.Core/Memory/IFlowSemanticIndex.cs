namespace Waymark.Core.Memory;

/// <summary>A flow as the semantic index sees it: the text to embed and the version it was made from.</summary>
public sealed record FlowDocument(long WorkspaceId, long FlowId, int Version, string Name, string Text);

/// <summary>One flow that is close in meaning to a query. Higher is closer; the scale belongs to the index.</summary>
public sealed record FlowSemanticHit(long FlowId, double Score);

/// <summary>
/// Search by meaning over the saved flows. Not built yet: the host registers a version that does nothing, so that everything that saves, changes, deletes or lists flows is already wired to it,
/// and the real index can replace that one class without touching them.
/// </summary>
public interface IFlowSemanticIndex
{
    /// <summary>False while there is no real index. Callers skip the index entirely then, so nothing changes for the owner.</summary>
    bool IsEnabled { get; }

    /// <summary>Adds the flow, or replaces what the index has for it. Called after every save and import.</summary>
    Task IndexAsync(FlowDocument flow, CancellationToken ct);

    /// <summary>Forgets a deleted flow.</summary>
    Task RemoveAsync(long workspaceId, long flowId, CancellationToken ct);

    /// <summary>The flows of the workspace closest in meaning to the query, best first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<FlowSemanticHit>> SearchAsync(long workspaceId, string query, int take, CancellationToken ct);

    /// <summary>Builds the index of the workspace again from its flows (a changed model, or an import that brought many flows).</summary>
    Task RebuildAsync(long workspaceId, CancellationToken ct);
}
