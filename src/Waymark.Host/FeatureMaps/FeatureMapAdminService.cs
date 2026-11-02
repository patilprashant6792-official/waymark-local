using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.FeatureMaps;

/// <summary>The maps of one workspace as the page shows them (the newest ones that match), and how many match in all.</summary>
public sealed record FeatureMapView(IReadOnlyList<FeatureMap> Maps, int Total);

/// <summary>
/// What the owner can do with the feature maps Claude saved: read them, check them against the code that is checked out, delete a map and remove a
/// component that is wrong. Claude creates and extends maps itself with memorise. Every change is written to the audit trail.
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

    /// <summary>Whether each component of the maps is in the code on disk now, by the component's text.</summary>
    public async Task<IReadOnlyDictionary<string, FeatureComponentStatus>> CheckAsync(Workspace workspace, IReadOnlyList<FeatureMap> maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(maps);
        var specs = maps.SelectMany(map => map.Components).Select(component => component.Spec).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (specs.Count == 0)
        {
            return new Dictionary<string, FeatureComponentStatus>();
        }

        var statuses = await checker.CheckAsync(workspace, specs, ct).ConfigureAwait(false);
        return statuses.ToDictionary(status => status.Spec, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> DeleteAsync(Workspace workspace, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var deleted = await store.DeleteAsync(workspace.Id, name, ct).ConfigureAwait(false);
        if (deleted)
        {
            await AuditAsync("config.feature_map_deleted", workspace.Name, name, ct).ConfigureAwait(false);
            await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>Takes one component out of a map. The rest of the map is kept.</summary>
    public async Task RemoveComponentAsync(Workspace workspace, string name, string spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await store.SaveAsync(workspace.Id, name, null, [], [spec], int.MaxValue, time.GetUtcNow(), ct).ConfigureAwait(false);
        await AuditAsync("config.feature_component_removed", workspace.Name, $"{name}: {spec}", ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
    }

    private Task AuditAsync(string action, string target, string? args, CancellationToken ct) =>
        audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target, AuditOutcome.Ok, args), ct);
}
