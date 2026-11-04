namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The feature maps (flows) behind recall and memorise: a thin layer that hands each call to the service that does the work.
/// Reading is <see cref="FlowRecallService"/>; creating, replacing and deleting is <see cref="FlowSaveService"/>.
/// </summary>
internal sealed class FeatureMemoryService(FlowRecallService recall, FlowSaveService save) : IFeatureMemoryService
{
    public Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct) =>
        recall.ListAsync(workspace, query, page, pageSize, ct);

    public Task<string> GetAsync(string? workspace, string? ids, CancellationToken ct) =>
        recall.GetAsync(workspace, ids, ct);

    public Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct) =>
        save.SaveAsync(workspace, name, summary, steps, expectedVersion, newName, distinct, ct);

    public Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct) =>
        save.DeleteAsync(workspace, name, ct);
}
