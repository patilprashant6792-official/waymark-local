using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// Lets the admin pages ask whether the steps of saved flows are in the checked-out code and what the code says about their links, with the same inspector that recall and memorise use,
/// so the page and Claude always agree.
/// </summary>
internal sealed class FeatureComponentChecker(FlowInspector inspector) : IFeatureComponentChecker
{
    public async Task<IReadOnlyList<FeatureComponentStatus>> CheckAsync(Workspace workspace, FeatureMap map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(map);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        return await StatusesAsync(workspace, map, files, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>>> CheckManyAsync(Workspace workspace, IReadOnlyList<FeatureMap> maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(maps);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<long, IReadOnlyList<FeatureComponentStatus>>();
        foreach (var map in maps)
        {
            result[map.Id] = await StatusesAsync(workspace, map, files, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<IReadOnlyList<FeatureComponentStatus>> StatusesAsync(Workspace workspace, FeatureMap map, Dictionary<string, FileView> files, CancellationToken ct)
    {
        var report = await inspector.InspectAsync(workspace, map, files, ct).ConfigureAwait(false);
        var statuses = new List<FeatureComponentStatus>(report.Steps.Count);
        for (var i = 0; i < report.Steps.Count; i++)
        {
            var view = report.Views[i];
            var link = report.Links[i];
            statuses.Add(new FeatureComponentStatus(report.Steps[i].Spec, view.Found, view.Problem, link?.State, link?.Detail, report.Moved[i]));
        }

        return statuses;
    }
}
