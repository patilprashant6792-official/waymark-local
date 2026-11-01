namespace Waymark.Core.Memory;

/// <summary>A saved component as stored: its canonical text and the optional note on why it matters.</summary>
public sealed record FeatureMapComponent(string Spec, string Note);

/// <summary>A feature map with its components in the order they were saved.</summary>
public sealed record FeatureMap(long Id, string Name, string Summary, DateTimeOffset UpdatedUtc, IReadOnlyList<FeatureMapComponent> Components);

public sealed record FeatureMapSummary(long Id, string Name, string Summary, int ComponentCount, DateTimeOffset UpdatedUtc);

/// <param name="Total">All maps that match the filter, not only the ones on this page.</param>
public sealed record FeatureMapPage(IReadOnlyList<FeatureMapSummary> Items, int Total);

public sealed record FeatureMapSaveResult(long Id, string Name, bool Created, int Added, int Updated, int Removed, int Total);

/// <summary>Another map of the same workspace that holds some of the same components.</summary>
public sealed record FeatureMapOverlap(long Id, string Name, int Shared);

/// <summary>
/// The feature maps of the workspaces: the code components Claude needed for a feature, saved under the feature's name.
/// Components are names and paths only. A component that is not on the branch that is checked out stays stored; the reader reports it as missing.
/// </summary>
public interface IFeatureMapStore
{
    /// <summary>Newest first. The query is split into words and a map matches when every word is in its name, summary, or one component or note.</summary>
    Task<FeatureMapPage> ListAsync(long workspaceId, string? query, int skip, int take, CancellationToken ct);

    /// <summary>The maps of this workspace with these ids, in the order asked; an id that is not here is left out.</summary>
    Task<IReadOnlyList<FeatureMap>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>
    /// Creates the map or merges into it, in one transaction: removes the listed components, adds new ones, updates the note of an existing one when a note is given,
    /// and keeps every other component. The summary changes only when one is given.
    /// </summary>
    /// <exception cref="InvalidOperationException">The map would hold more than <paramref name="maxComponents"/> components; nothing is changed.</exception>
    Task<FeatureMapSaveResult> SaveAsync(long workspaceId, string name, string? summary, IReadOnlyList<FeatureMapComponent> add, IReadOnlyList<string> remove, int maxComponents, DateTimeOffset now, CancellationToken ct);

    /// <summary>The other maps of the workspace that share components with this one, most shared first (at most 3).</summary>
    Task<IReadOnlyList<FeatureMapOverlap>> FindOverlapsAsync(long workspaceId, long mapId, CancellationToken ct);

    /// <summary>The number of maps per workspace id; a workspace with none is absent.</summary>
    Task<IReadOnlyDictionary<long, int>> CountByWorkspaceAsync(CancellationToken ct);

    /// <summary>Deletes the map of this name with its components. False when there is none.</summary>
    Task<bool> DeleteAsync(long workspaceId, string name, CancellationToken ct);
}
