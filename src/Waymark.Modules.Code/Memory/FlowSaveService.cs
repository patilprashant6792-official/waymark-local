using System.Text;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// memorise: creates a flow, replaces its steps, or deletes it.
/// <para>
/// Replacing needs the version that was read, so an agent cannot change a flow it has not read, and two sessions cannot overwrite each other.
/// Only what is new or changed has to be in the code on this branch: a step that was stored already stays, whatever branch it came from.
/// A flow may not claim a call the code does not make; a link the index cannot see is declared with ~event, ~http, ~ui, ~di or ~job.
/// </para>
/// </summary>
internal sealed class FlowSaveService(
    IPathGuard guard,
    IFeatureMapStore store,
    FlowInspector inspector,
    IContentInspector contentInspector,
    IEventBus events,
    TimeProvider time)
{
    private const int MaxProblemsListed = 6;

    public async Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var cleanName = FlowArgs.CleanName(name);
        var cleanSummary = FlowArgs.CleanSummary(summary);
        var reason = FlowArgs.CleanDistinct(distinct);
        var cleanNewName = string.IsNullOrWhiteSpace(newName) ? null : FlowArgs.CleanName(newName);
        if (!FlowStepText.TryParse(steps, out var parsed, out var errors))
        {
            throw new ArgumentException(string.Join(" ", errors));
        }

        var everything = string.Join("\n", cleanName, cleanNewName ?? string.Empty, cleanSummary ?? string.Empty, reason, steps ?? string.Empty);
        var findings = await contentInspector.InspectAsync(everything, ct).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new ArgumentException("The text looks like it holds a secret or a redaction marker, so nothing was saved. Take it out and call again.");
        }

        var stored = await store.GetByNameAsync(found.Id, cleanName, ct).ConfigureAwait(false);
        return expectedVersion is null
            ? await CreateAsync(found, cleanName, cleanSummary, reason, cleanNewName, stored, parsed, ct).ConfigureAwait(false)
            : await ReplaceAsync(found, cleanName, cleanSummary, cleanNewName, expectedVersion.Value, stored, parsed, ct).ConfigureAwait(false);
    }

    public async Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var cleanName = FlowArgs.CleanName(name);
        var id = await store.DeleteAsync(found.Id, cleanName, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No flow named '{cleanName}' in workspace '{found.Name}'. recall(action='list') shows the names.");
        await events.PublishAsync(new FeatureMapsChanged(found.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(found.Id, id, FlowChange.Deleted)).ConfigureAwait(false);
        return $"Deleted flow '{cleanName}' from workspace '{found.Name}'.";
    }

    private async Task<string> CreateAsync(
        Workspace found,
        string name,
        string? summary,
        string reason,
        string? newName,
        FeatureMap? stored,
        IReadOnlyList<FlowStep> steps,
        CancellationToken ct)
    {
        if (newName is not null)
        {
            throw new ArgumentException("newName renames a flow that exists: read it with recall, then save it with expectedVersion and newName. To create a flow, leave newName out.");
        }

        if (stored is not null)
        {
            throw new ArgumentException(
                $"A flow named '{name}' exists (id {stored.Id}, v{stored.Version}). Read it with recall(action='get', ids='{stored.Id}'), then save the changed flow with expectedVersion={stored.Version}. For a different flow, choose another name.");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("summary is required for a new flow: one or two sentences on what it does, from its trigger to its outcome.");
        }

        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var inspection = await inspector.InspectStepsAsync(found, steps, files, ct).ConfigureAwait(false);
        var problems = Problems(steps, inspection, _ => true, _ => true);
        if (problems.Count > 0)
        {
            throw new ArgumentException(Refusal(problems));
        }

        var same = await store.FindByRootAsync(found.Id, steps[0].Spec, null, ct).ConfigureAwait(false);
        if (same.Count > 0 && reason.Length == 0)
        {
            var list = string.Join("; ", same.Select(flow => $"'{flow.Name}' (id {flow.Id}): {FlowArgs.Cell(flow.Summary)}"));
            throw new ArgumentException(
                $"Nothing was saved: {(same.Count == 1 ? "another flow starts" : "other flows start")} at the same step: {list}. " +
                "Same trigger and same outcome: update that flow (read it with recall, save it with expectedVersion). Two flows that already say the same thing: delete one with memorise(action='delete_feature_map'). " +
                "A different outcome from the same trigger: save again with distinct='how this one differs'.");
        }

        var saved = await store.CreateAsync(found.Id, name, summary, reason, StepsToStore(steps), time.GetUtcNow(), ct).ConfigureAwait(false);
        if (saved.Outcome != FlowWrite.Saved)
        {
            throw new ArgumentException($"A flow named '{name}' exists now. Read it with recall and save it with expectedVersion.");
        }

        return await ReplyAsync(found, saved, steps, inspection, true, [], ct).ConfigureAwait(false);
    }

    private async Task<string> ReplaceAsync(
        Workspace found,
        string name,
        string? summary,
        string? newName,
        int expectedVersion,
        FeatureMap? stored,
        IReadOnlyList<FlowStep> steps,
        CancellationToken ct)
    {
        if (stored is null)
        {
            throw new KeyNotFoundException($"No flow named '{name}' in workspace '{found.Name}'. To create it, leave expectedVersion out. recall(action='list') shows the names.");
        }

        if (stored.Version != expectedVersion)
        {
            throw new InvalidOperationException(await ConflictAsync(found, stored, expectedVersion, ct).ConfigureAwait(false));
        }

        var before = Before(stored);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var inspection = await inspector.InspectStepsAsync(found, steps, files, ct).ConfigureAwait(false);

        // Only what is new or changed has to hold here. What was stored before stays as it is, so a flow saved on another branch can be updated on this one.
        var problems = Problems(
            steps,
            inspection,
            index => !before.Specs.Contains(steps[index].Spec),
            index => !before.IsKnown(steps, index));
        if (problems.Count > 0)
        {
            throw new ArgumentException(Refusal(problems));
        }

        var saved = await store.ReplaceAsync(found.Id, name, expectedVersion, newName, summary, StepsToStore(steps), time.GetUtcNow(), ct).ConfigureAwait(false);
        switch (saved.Outcome)
        {
            case FlowWrite.NotFound:
                throw new KeyNotFoundException($"No flow named '{name}' in workspace '{found.Name}'.");
            case FlowWrite.NameTaken:
                throw new ArgumentException($"newName '{newName}' is the name of another flow. Choose another name, or delete that flow first.");
            case FlowWrite.VersionConflict:
                var current = await store.GetByNameAsync(found.Id, name, ct).ConfigureAwait(false) ?? stored;
                throw new InvalidOperationException(await ConflictAsync(found, current, expectedVersion, ct).ConfigureAwait(false));
        }

        var stillBroken = new List<string>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (inspection.Links[i] is { State: FlowLinkState.DoesNotCall } broken && before.IsKnown(steps, i))
            {
                stillBroken.Add($"step {i + 1} {steps[i].Spec}: {broken.Detail}");
            }
        }

        return await ReplyAsync(found, saved, steps, inspection, true, stillBroken, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What is wrong with the steps that count: a step that is new or changed and not in the code here, and a link that is new or changed and not made by the code.
    /// </summary>
    private static List<string> Problems(IReadOnlyList<FlowStep> steps, StepsInspection inspection, Func<int, bool> mustExist, Func<int, bool> linkMustHold)
    {
        var problems = new List<string>();
        for (var i = 0; i < steps.Count; i++)
        {
            var view = inspection.Views[i];
            if (!view.Found && mustExist(i))
            {
                var moved = inspection.Moved[i] is { } to ? $" It may have moved: {to}." : string.Empty;
                problems.Add($"- step {i + 1} `{steps[i].Spec}`: {view.Problem}.{moved}");
                continue;
            }

            if (inspection.Links[i] is { State: FlowLinkState.DoesNotCall } link && linkMustHold(i))
            {
                var parent = FlowStepText.ParentIndex(steps, i);
                problems.Add(
                    $"- step {i + 1} `{steps[i].Spec}` is indented under step {(parent ?? 0) + 1}, but {link.Detail}. " +
                    "Indent it under the step that calls it, add the missing step between them, or mark the link ~event, ~http, ~ui, ~di or ~job when the code reaches it another way.");
            }
        }

        return problems;
    }

    private static string Refusal(List<string> problems) =>
        $"Nothing was saved: {problems.Count} problem(s).\n{string.Join("\n", problems.Take(MaxProblemsListed))}\nFix these and call again.";

    private async Task<string> ConflictAsync(Workspace found, FeatureMap current, int expectedVersion, CancellationToken ct)
    {
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var report = await inspector.InspectAsync(found, current, files, ct).ConfigureAwait(false);
        return $"Nothing was saved: flow '{current.Name}' is at v{current.Version}, not v{expectedVersion}: it changed since you read it. Here it is now. " +
            $"Merge your changes into it and save again with expectedVersion={current.Version}.\n\n{FlowRecallRenderer.Flow(report)}";
    }

    private async Task<string> ReplyAsync(
        Workspace found,
        FlowWriteResult saved,
        IReadOnlyList<FlowStep> steps,
        StepsInspection inspection,
        bool ordered,
        List<string> stillBroken,
        CancellationToken ct)
    {
        await events.PublishAsync(new FeatureMapsChanged(found.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(found.Id, saved.Id, FlowChange.Saved)).ConfigureAwait(false);
        var message = new StringBuilder();
        message.Append($"Saved flow '{saved.Name}' (id {saved.Id}, v{saved.Version}) in workspace '{found.Name}'. {FlowRecallRenderer.Counts(steps.Count, inspection, ordered)}");
        message.Append($"\nRead it back with recall(action='get', ids='{saved.Id}').");
        foreach (var line in stillBroken)
        {
            message.Append($"\nStill not matching the code (it was already stored like this): {line}. Fix it in this task.");
        }

        if (steps.Count > FeatureMapRules.SplitHintSteps)
        {
            message.Append($"\nThis flow has {steps.Count} steps. Consider splitting it by outcome: a flow is one trigger to one outcome.");
        }

        var overlaps = (await store.FindOverlapsAsync(found.Id, saved.Id, ct).ConfigureAwait(false)).Where(o => o.Shared * 2 >= steps.Count).ToList();
        foreach (var overlap in overlaps)
        {
            message.Append(
                $"\nNote: '{overlap.Name}' (id {overlap.Id}) shares {overlap.Shared} of these {steps.Count} steps. If it describes the same trigger and outcome, keep the better one and delete the other with memorise(action='delete_feature_map', name='{overlap.Name}').");
        }

        return message.ToString();
    }

    private static IReadOnlyList<FeatureMapStep> StepsToStore(IReadOnlyList<FlowStep> steps) => FlowStepText.ToStored(steps);

    private static StoredShape Before(FeatureMap stored) => new(stored);

    /// <summary>
    /// What the stored flow had: its steps, and each step with the step it was under and how it was reached. A new step or link that matches one of these is not a change.
    /// An unordered map (saved before flows) has no meaningful parents, so only its steps count.
    /// </summary>
    private sealed class StoredShape
    {
        private readonly HashSet<string> _links = new(StringComparer.OrdinalIgnoreCase);

        public StoredShape(FeatureMap stored)
        {
            Specs = stored.Steps.Select(step => step.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!stored.Ordered)
            {
                return;
            }

            var steps = FlowStepText.FromStored(stored.Steps);
            for (var i = 0; i < steps.Count; i++)
            {
                _links.Add(LinkKey(steps, i));
            }
        }

        public HashSet<string> Specs { get; }

        /// <summary>True when the stored flow had this step under the same parent with the same kind of link.</summary>
        public bool IsKnown(IReadOnlyList<FlowStep> steps, int index) => _links.Contains(LinkKey(steps, index));

        private static string LinkKey(IReadOnlyList<FlowStep> steps, int index)
        {
            var parent = FlowStepText.ParentIndex(steps, index);
            return $"{(parent is { } p ? steps[p].Spec : string.Empty)}>{steps[index].Spec}~{steps[index].Link}";
        }
    }
}
