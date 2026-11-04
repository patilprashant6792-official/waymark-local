using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// After a file tool changed a file, tells the agent when that touches a step of a saved flow and the flow no longer matches the code. It only reads and never fails the file change:
/// everything is caught, and it gives up after <see cref="FlowOptions.EditHintTimeout"/>.
/// <para>
/// It speaks rarely on purpose. Most edits touch no step (one indexed lookup says so and it stays silent). An edit inside a step that leaves every link as it was is silent too.
/// It speaks when a link no longer holds, when a step is gone from its file, and when a file that held steps was moved or deleted.
/// </para>
/// </summary>
internal sealed class FlowEditAdvisor(IPathGuard guard, IFeatureMapStore store, FlowInspector inspector, FlowOptions options, ILogger<FlowEditAdvisor> logger) : IFlowEditAdvisor
{
    private const int MaxFlows = 3;
    private const int MaxPaths = 10;
    private const int MaxHitsPerPath = 60;
    private const int MaxProblemsPerFlow = 3;

    public async Task<string?> AdviseAsync(string workspace, IReadOnlyList<string> relativePaths, FlowEditKind kind, CancellationToken ct)
    {
        if (!options.EditHints || relativePaths is not { Count: > 0 })
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.EditHintTimeout);
        try
        {
            return await AdviseCoreAsync(workspace, relativePaths, kind, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("The flow hint after a file change took longer than {Timeout} and was skipped.", options.EditHintTimeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The flow hint after a file change failed; the file change itself is not affected.");
            return null;
        }
    }

    private async Task<string?> AdviseCoreAsync(string workspace, IReadOnlyList<string> relativePaths, FlowEditKind kind, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var paths = relativePaths.Select(Normalize).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPaths).ToList();
        var hits = new List<(string Path, FlowStepRef Step)>();
        foreach (var path in paths)
        {
            hits.AddRange((await store.FindByPathAsync(found.Id, path, MaxHitsPerPath, ct).ConfigureAwait(false)).Select(step => (path, step)));
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var flows = hits.GroupBy(hit => hit.Step.FlowId).Take(MaxFlows).ToList();
        return kind == FlowEditKind.Edited
            ? await EditedAsync(found, paths, flows.Select(group => group.Key).ToList(), ct).ConfigureAwait(false)
            : Gone(flows, kind);
    }

    // The path the way the flows store it: '/' as the separator, no leading './' or '/'.
    private static string Normalize(string? path)
    {
        var text = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text.TrimStart('/');
    }
    // The file was moved or deleted: every step in it is no longer at that path, and nothing needs reading to say so.
    private static string Gone(List<IGrouping<long, (string Path, FlowStepRef Step)>> flows, FlowEditKind kind)
    {
        var verb = kind == FlowEditKind.Moved ? "was moved" : "was deleted";
        var text = new StringBuilder();
        foreach (var flow in flows)
        {
            var first = flow.First();
            var steps = string.Join(", ", flow.Select(hit => hit.Step.Ordinal + 1));
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append($"🧭 {first.Path} {verb}: step {steps} of flow '{first.Step.FlowName}' (id {flow.Key}) is no longer there. Update the flow if the path changed; if the file only moved on this branch, leave it.");
        }

        return text.ToString();
    }

    private async Task<string?> EditedAsync(Workspace found, List<string> editedPaths, List<long> flowIds, CancellationToken ct)
    {
        var maps = await store.GetAsync(found.Id, flowIds, ct).ConfigureAwait(false);
        var edited = editedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        foreach (var map in maps)
        {
            var report = await inspector.InspectAsync(found, map, files, ct).ConfigureAwait(false);
            var problems = Problems(report, edited);
            if (problems.Count == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append($"🧭 flow '{map.Name}' (id {map.Id}, v{map.Version}): {string.Join("; ", problems.Take(MaxProblemsPerFlow))}. Update the flow if the path changed (memorise with expectedVersion={map.Version}).");
        }

        return text.Length == 0 ? null : text.ToString();
    }

    // Only what the edit can have caused: a step of an edited file that is gone, and a link that no longer holds where one of its two steps is in an edited file.
    private static List<string> Problems(FlowReport report, HashSet<string> edited)
    {
        var problems = new List<string>();
        for (var i = 0; i < report.Steps.Count; i++)
        {
            var step = report.Steps[i];
            if (!report.Views[i].Found && edited.Contains(step.Component.Path))
            {
                var moved = report.Moved[i] is { } to ? $", it may have moved to {to}" : string.Empty;
                problems.Add($"step {i + 1} {step.Spec} is no longer in the file{moved}");
                continue;
            }

            if (report.Links[i] is not { State: FlowLinkState.DoesNotCall } link)
            {
                continue;
            }

            var parent = FlowStepText.ParentIndex(report.Steps, i);
            var touched = edited.Contains(step.Component.Path) || (parent is { } p && edited.Contains(report.Steps[p].Component.Path));
            if (touched)
            {
                problems.Add($"link {(parent ?? 0) + 1} -> {i + 1} no longer holds ({link.Detail})");
            }
        }

        return problems;
    }
}
