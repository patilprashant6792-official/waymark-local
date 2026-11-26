using Microsoft.Extensions.Logging;
using Waymark.Core.Audit;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// Repairs the one kind of drift that needs no judgement: a method step that moved to another file. It acts only when all of these hold, and otherwise leaves
/// the item open for an agent:
/// <list type="bullet">
/// <item>exactly one other place has a method of that name (and type);</item>
/// <item>the flow does not already have a step there;</item>
/// <item>the owner did not undo this same fix before;</item>
/// <item>at the new place the step is found, the link into it holds and the links out of it to the steps it calls hold;</item>
/// <item>the flow is still at the version that was read (a save in between wins).</item>
/// </list>
/// The fix is saved like an agent's save (a new flow version), the step notes move with the step, and an auto-fixed item with an undo record goes to the queue.
/// </summary>
internal sealed class MemoryAutoRepair(
    IFeatureMapStore maps,
    INoteStore notes,
    FlowInspector inspector,
    FlowDriftFinder drift,
    IMemoryRepairStore repairs,
    IEventBus events,
    IAuditStore audit,
    TimeProvider time,
    ILogger<MemoryAutoRepair> logger)
{
    internal const string Actor = "waymark (auto-fix)";

    /// <summary>Moves the step <paramref name="oldSpec"/> of flow <paramref name="mapId"/> to its one new place. Returns the undo record, or null when it did not act.</summary>
    public async Task<AutoFixUndo?> TryMoveStepAsync(Workspace workspace, long mapId, string oldSpec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var map = (await maps.GetAsync(workspace.Id, [mapId], ct).ConfigureAwait(false)).FirstOrDefault();
        if (map is null || !map.Ordered)
        {
            return null;
        }

        var steps = FlowStepText.FromStored(map.Steps);
        var index = IndexOf(steps, oldSpec);
        if (index < 0 || !steps[index].Valid)
        {
            return null;
        }

        var candidates = await drift.FindAllAsync(workspace.Id, steps[index].Component, ct).ConfigureAwait(false);
        if (candidates.Count != 1)
        {
            return null;
        }

        var newSpec = candidates[0];
        if (IndexOf(steps, newSpec) >= 0 || await repairs.WasUndoneAsync(workspace.Id, map.Id, oldSpec, newSpec, ct).ConfigureAwait(false))
        {
            return null;
        }

        if (!FeatureComponentSpec.TryParse(newSpec, out var component, out _))
        {
            return null;
        }

        var moved = steps.ToList();
        moved[index] = steps[index] with { Component = component, Spec = FeatureComponentSpec.Format(component) };
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var inspection = await inspector.InspectStepsAsync(workspace, moved, files, ct).ConfigureAwait(false);
        if (!inspection.Views[index].Found || !LinksHold(moved, inspection, index))
        {
            return null;
        }

        var head = inspector.HeadOf(workspace);
        var stored = FlowStepText.ToStored(moved);
        if (head is { IsGit: true, Branch: { } branch })
        {
            stored = [.. stored.Select((step, i) => inspection.Views[i].Found ? step with { Branch = branch } : step)];
        }

        var saved = await maps.ReplaceAsync(workspace.Id, map.Name, map.Version, null, null, stored, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (saved.Outcome != FlowWrite.Saved)
        {
            // The flow changed since it was read (an agent saved it): its next check decides again.
            return null;
        }

        var newText = moved[index].Spec;
        var notesMoved = await notes.RebaseStepAsync(workspace.Id, map.Id, oldSpec, newText, ct).ConfigureAwait(false);

        // The fix changes no meaning: flow notes that were current stay current instead of asking an agent to re-read them.
        await notes.RebaseFlowVersionAsync(workspace.Id, map.Id, map.Version, saved.Version, ct).ConfigureAwait(false);
        var undo = new AutoFixUndo(map.Id, saved.Name, saved.Version, index, oldSpec, newText, notesMoved);
        var finding = new RepairFinding(
            MemoryRepairKind.StepMoved,
            MemoryRepairTarget.Flow,
            map.Id,
            saved.Name,
            index,
            newText,
            component.Path,
            $"step {index + 1} moved from {oldSpec} to {newText}",
            oldSpec,
            false);
        await repairs.RecordAutoFixAsync(workspace.Id, finding, undo, time.GetUtcNow(), ct).ConfigureAwait(false);
        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, "memory.repair_auto_fixed", Actor, workspace.Name, AuditOutcome.Ok, $"flow '{saved.Name}' v{saved.Version}: step {index + 1} {oldSpec} -> {newText}"),
            ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(workspace.Id, map.Id, FlowChange.Saved)).ConfigureAwait(false);
        if (notesMoved > 0)
        {
            await events.PublishAsync(new NoteChanged(workspace.Id, 0, NoteChange.Saved)).ConfigureAwait(false);
        }

        logger.LogInformation("Memory repaired itself in {Workspace}: flow '{Flow}' step {Step} moved to {Spec}.", workspace.Name, saved.Name, index + 1, newText);
        return undo;
    }

    // The link into the moved step and the links from it to the steps directly under it must hold where the step is now.
    private static bool LinksHold(IReadOnlyList<FlowStep> steps, StepsInspection inspection, int index)
    {
        if (index > 0 && inspection.Links[index] is { State: FlowLinkState.DoesNotCall })
        {
            return false;
        }

        for (var i = index + 1; i < steps.Count; i++)
        {
            if (FlowStepText.ParentIndex(steps, i) == index && inspection.Links[i] is { State: FlowLinkState.DoesNotCall })
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<FlowStep> steps, string spec)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (string.Equals(steps[i].Spec, spec, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
