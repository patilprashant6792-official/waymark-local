using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>The steps of a flow as the code shows them: one view, one link check and one "moved?" suggestion per step, in step order.</summary>
internal sealed record StepsInspection(IReadOnlyList<StepView> Views, IReadOnlyList<LinkCheck?> Links, IReadOnlyList<string?> Moved);

/// <summary>A saved flow with what the code says about each of its steps and links.</summary>
internal sealed record FlowReport(FeatureMap Map, IReadOnlyList<FlowStep> Steps, StepsInspection Inspection)
{
    public IReadOnlyList<StepView> Views => Inspection.Views;

    public IReadOnlyList<LinkCheck?> Links => Inspection.Links;

    public IReadOnlyList<string?> Moved => Inspection.Moved;
}

/// <summary>
/// The one way flows are checked against the code: recall, memorise, the hint after a file change and the owner's "Check against the code" all go through here,
/// so they always agree. It only reads; nothing is repaired, moved or dropped.
/// </summary>
internal sealed class FlowInspector(FlowStepReader reader, FlowLinkChecker links, FlowDriftFinder drift)
{
    /// <param name="files">The files read so far in this call, shared between flows so a file is parsed once.</param>
    public async Task<FlowReport> InspectAsync(Workspace workspace, FeatureMap map, Dictionary<string, FileView> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        var steps = FlowStepText.FromStored(map.Steps);
        var inspection = await InspectStepsAsync(workspace, steps, files, ct).ConfigureAwait(false);
        return new FlowReport(map, steps, inspection);
    }

    /// <summary>The same check for steps that are not stored yet (a flow about to be saved).</summary>
    public async Task<StepsInspection> InspectStepsAsync(Workspace workspace, IReadOnlyList<FlowStep> steps, Dictionary<string, FileView> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(steps);
        var views = new List<StepView>(steps.Count);
        var moved = new List<string?>(steps.Count);
        foreach (var step in steps)
        {
            var view = await reader.DescribeAsync(workspace, step, files, ct).ConfigureAwait(false);
            views.Add(view);
            moved.Add(view.Found || !step.Valid ? null : await drift.FindAsync(workspace.Id, step.Component, ct).ConfigureAwait(false));
        }

        var checks = await links.CheckAsync(workspace.Id, steps, views, ct).ConfigureAwait(false);
        return new StepsInspection(views, checks, moved);
    }
}
