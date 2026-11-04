using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.FeatureMaps;

/// <summary>The maps of one workspace as the page shows them (the newest ones that match), and how many match in all.</summary>
public sealed record FeatureMapView(IReadOnlyList<FeatureMap> Maps, int Total);

/// <summary>
/// What the owner can do with the feature maps (flows) Claude saved: read them, check them against the code that is checked out, delete a map and remove a
/// step that is wrong. Claude creates and changes flows itself with memorise. Every change is written to the audit trail.
/// </summary>
public sealed class FeatureMapAdminService(IFeatureMapStore store, IFeatureComponentChecker checker, IAuditStore audit, IEventBus events, TimeProvider time)
{
    /// <summary>How many maps one page lists.</summary>
    public const int PageSize = 50;

    public async Task<FeatureMapView> LoadAsync(long workspaceId, string? query, CancellationToken ct)
    {
        var page = await store.ListAsync(workspaceId, query, 0, PageSize, ct).ConfigureAwait(false);
        var maps = await store.GetAsync(workspaceId, page.Items.Select(item => item.Id).ToList(), ct).ConfigureAwait(false);
        return new FeatureMapView(maps, page.Total);
    }

    /// <summary>
    /// Whether each step of the maps is in the code on disk now and what the code says about its link, by the id of the map and then in the order of its steps.
    /// It is the check recall and memorise use, so the page and Claude agree.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>>> CheckAsync(Workspace workspace, IReadOnlyList<FeatureMap> maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(maps);
        if (maps.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<FeatureComponentStatus>>();
        }

        return await checker.CheckManyAsync(workspace, maps, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Workspace workspace, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var id = await store.DeleteAsync(workspace.Id, name, ct).ConfigureAwait(false);
        if (id is null)
        {
            return false;
        }

        await AuditAsync("config.feature_map_deleted", workspace.Name, name, ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(workspace.Id, id.Value, FlowChange.Deleted)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Why a step cannot be taken out of the map, or null when it can. The trigger of a flow cannot (delete the flow), and neither can a step that has steps under it
    /// (they would be left without the step that calls them). A map saved before flows is an unordered list, where any one component may go except the last.
    /// </summary>
    public static string? WhyNotRemovable(FeatureMap map, int index)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (index < 0 || index >= map.Steps.Count)
        {
            return "There is no such step.";
        }

        if (map.Steps.Count == 1)
        {
            return "It is the only step: delete the map instead.";
        }

        if (!map.Ordered)
        {
            return null;
        }

        if (index == 0)
        {
            return "It is the trigger of the flow: delete the flow instead.";
        }

        return index + 1 < map.Steps.Count && map.Steps[index + 1].Depth > map.Steps[index].Depth
            ? "Steps are under it: remove those first."
            : null;
    }

    /// <summary>Takes one step out of a map. The rest of the map is kept, and the map is saved as a new version.</summary>
    /// <exception cref="InvalidOperationException">The step may not be removed, or the map changed since it was shown.</exception>
    public async Task RemoveStepAsync(Workspace workspace, FeatureMap map, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(map);
        if (WhyNotRemovable(map, index) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var spec = map.Steps[index].Spec;
        var result = await store.RemoveStepAsync(workspace.Id, map.Name, map.Version, spec, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (result.Outcome == FlowWrite.VersionConflict)
        {
            throw new InvalidOperationException("The map changed since this page showed it. It is shown again now; try again.");
        }

        if (result.Outcome != FlowWrite.Saved)
        {
            throw new InvalidOperationException("The step is not in the map any more.");
        }

        await AuditAsync("config.feature_component_removed", workspace.Name, $"{map.Name}: {spec}", ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(workspace.Id, result.Id, FlowChange.Saved)).ConfigureAwait(false);
    }

    private Task AuditAsync(string action, string target, string? args, CancellationToken ct) =>
        audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target, AuditOutcome.Ok, args), ct);
}
