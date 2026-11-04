namespace Waymark.Core.Memory;

/// <summary>
/// A saved step as stored: its canonical component text, how deep it is (0 is the trigger), how it is reached from the step above
/// ("call", "http", "event", "ui", "di" or "job") and the optional note on why it matters.
/// </summary>
public sealed record FeatureMapStep(string Spec, int Depth, string Link, string Note);

/// <summary>
/// A feature map, which is a flow: one trigger to one outcome as ordered steps. <paramref name="Version"/> goes up on every change;
/// <paramref name="Ordered"/> is false for a map saved before flows existed (its steps are an unordered list);
/// <paramref name="DistinctReason"/> says why it exists next to another flow with the same trigger.
/// </summary>
public sealed record FeatureMap(
    long Id,
    string Name,
    string Summary,
    int Version,
    bool Ordered,
    string DistinctReason,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<FeatureMapStep> Steps);

public sealed record FeatureMapSummary(long Id, string Name, string Summary, int StepCount, int Version, bool Ordered, DateTimeOffset UpdatedUtc);

/// <param name="Total">All maps that match the filter, not only the ones on this page.</param>
public sealed record FeatureMapPage(IReadOnlyList<FeatureMapSummary> Items, int Total);

/// <summary>Another map of the same workspace that holds some of the same steps.</summary>
public sealed record FeatureMapOverlap(long Id, string Name, int Shared);

/// <summary>A map as the live setup lists it for agents: enough to choose a flow without listing first.</summary>
public sealed record FeatureMapName(long Id, string Name, string Summary, int StepCount, bool Ordered);

/// <param name="Total">All maps of the workspace.</param>
/// <param name="Newest">The most recently changed ones, newest first.</param>
public sealed record FeatureMapIndex(int Total, IReadOnlyList<FeatureMapName> Newest);

/// <summary>A flow that has the same trigger as the one being saved.</summary>
public sealed record FlowRef(long Id, string Name, string Summary);

/// <summary>A step of some flow, found by the file it is in.</summary>
public sealed record FlowStepRef(long FlowId, string FlowName, int Ordinal, string Spec);

public enum FlowWrite
{
    Saved,

    /// <summary>Creating: a flow of this name exists. Replacing: the new name is taken by another flow.</summary>
    NameTaken,

    /// <summary>Replacing: there is no flow of this name.</summary>
    NotFound,

    /// <summary>Replacing: the flow changed since the version that was read. <see cref="FlowWriteResult.Version"/> is the current one.</summary>
    VersionConflict,
}

public sealed record FlowWriteResult(FlowWrite Outcome, long Id, string Name, int Version);

/// <summary>
/// The feature maps of the workspaces: flows of steps (places in the code) Claude needed for a feature, saved under the feature's name.
/// Steps are names and paths only. A step that is not on the branch that is checked out stays stored; the reader reports it as not there.
/// </summary>
public interface IFeatureMapStore
{
    /// <summary>Newest first. The query is split into words and a map matches when every word is in its name, summary, or one step, path or note.</summary>
    Task<FeatureMapPage> ListAsync(long workspaceId, string? query, int skip, int take, CancellationToken ct);

    /// <summary>The maps of this workspace with these ids, in the order asked; an id that is not here is left out.</summary>
    Task<IReadOnlyList<FeatureMap>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>The map of this name, or null.</summary>
    Task<FeatureMap?> GetByNameAsync(long workspaceId, string name, CancellationToken ct);

    /// <summary>
    /// Creates an ordered flow at version 1, in one transaction. <see cref="FlowWrite.NameTaken"/> when the name exists; nothing is changed then.
    /// </summary>
    Task<FlowWriteResult> CreateAsync(
        long workspaceId,
        string name,
        string summary,
        string distinctReason,
        IReadOnlyList<FeatureMapStep> steps,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// Replaces all steps of the flow in one transaction when its version is <paramref name="expectedVersion"/>, and raises the version by one.
    /// The map becomes ordered. The summary changes only when one is given; the name changes only when <paramref name="newName"/> is given.
    /// </summary>
    Task<FlowWriteResult> ReplaceAsync(
        long workspaceId,
        string name,
        int expectedVersion,
        string? newName,
        string? summary,
        IReadOnlyList<FeatureMapStep> steps,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// Removes one step (by its text) in one transaction when the flow is at <paramref name="expectedVersion"/>: the steps after it move up, the version rises by one,
    /// and the map stays ordered or unordered as it was. <see cref="FlowWrite.NotFound"/> when the flow or the step is not there. It does not judge the flow: whether the step may go is the caller's rule.
    /// </summary>
    Task<FlowWriteResult> RemoveStepAsync(long workspaceId, string name, int expectedVersion, string spec, DateTimeOffset now, CancellationToken ct);

    /// <summary>The ordered flows of the workspace whose first step (the trigger) is this component, optionally leaving one out.</summary>
    Task<IReadOnlyList<FlowRef>> FindByRootAsync(long workspaceId, string rootSpec, long? exceptId, CancellationToken ct);

    /// <summary>The steps of the workspace's flows that are in this file (path from the workspace root), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<FlowStepRef>> FindByPathAsync(long workspaceId, string path, int limit, CancellationToken ct);

    /// <summary>The other maps of the workspace that share steps with this one, most shared first (at most 3).</summary>
    Task<IReadOnlyList<FeatureMapOverlap>> FindOverlapsAsync(long workspaceId, long mapId, CancellationToken ct);

    /// <summary>Per workspace id: how many maps it has and the newest <paramref name="perWorkspace"/>; a workspace with none is absent.</summary>
    Task<IReadOnlyDictionary<long, FeatureMapIndex>> ListNamesAsync(int perWorkspace, CancellationToken ct);

    /// <summary>Deletes the map of this name with its steps. The id that it had, or null when there is none.</summary>
    Task<long?> DeleteAsync(long workspaceId, string name, CancellationToken ct);
}
