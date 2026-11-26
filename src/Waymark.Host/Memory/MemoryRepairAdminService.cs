using System.Globalization;
using System.Text.Json;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Memory;

/// <summary>
/// What the owner does with memory repairs in the admin UI: see the queue and what Waymark fixed itself, undo an automatic fix, ask for a fresh check,
/// and choose the mode, automatic fixes and the per-session budget. Every change is audited as the admin actor.
/// </summary>
public sealed class MemoryRepairAdminService(
    IMemoryRepairStore repairs,
    IMemoryRepairSettings settings,
    IFeatureMapStore maps,
    INoteStore notes,
    IEventBus events,
    IAuditStore audit,
    TimeProvider time)
{
    public const int ListSize = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<MemoryRepairOptions> GetOptionsAsync(CancellationToken ct) => settings.GetAsync(ct);

    public async Task SaveOptionsAsync(MemoryRepairOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        await settings.SetAsync(options, AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        await audit.AppendAsync(
            AuditEntries.Create(
                time,
                AuditCategory.Config,
                "config.memory_repairs",
                AuthDefaults.AdminActor,
                null,
                AuditOutcome.Ok,
                string.Create(CultureInfo.InvariantCulture, $"mode {MemoryRepairOptions.ModeText(options.Mode)}, auto-fix {(options.AutoFix ? "on" : "off")}, budget {options.Budget}")),
            ct).ConfigureAwait(false);
    }

    public Task<RepairCounts> CountsAsync(long workspaceId, CancellationToken ct) => repairs.CountsAsync(workspaceId, time.GetUtcNow().AddDays(-7), ct);

    public Task<IReadOnlyList<MemoryRepair>> OpenAsync(long workspaceId, CancellationToken ct) => repairs.ListOpenAsync(workspaceId, null, 0, ListSize, ct);

    public Task<IReadOnlyList<MemoryRepair>> AutoFixedAsync(long workspaceId, CancellationToken ct) => repairs.ListAutoFixedAsync(workspaceId, ListSize, ct);

    /// <summary>Asks the drift scanner to check the whole workspace again now (it runs within a few seconds).</summary>
    public async Task RecheckAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await events.PublishAsync(new CodeFilesChanged(workspace.Id, [], Whole: true)).ConfigureAwait(false);
        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, "config.memory_recheck", AuthDefaults.AdminActor, workspace.Name), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts back the step an automatic fix moved, as a new flow version, and moves its notes back. Refused when the flow changed since the fix (someone saved it):
    /// then the owner edits the flow instead. The same fix is never made again.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not an automatic fix of this workspace, already undone, or the flow changed since.</exception>
    public async Task UndoAsync(Workspace workspace, long repairId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var item = await repairs.GetAsync(workspace.Id, repairId, ct).ConfigureAwait(false);
        if (item is not { State: MemoryRepairState.AutoFixed, UndoJson: { } undoJson })
        {
            throw new InvalidOperationException("Only an automatic fix that is not undone yet can be undone.");
        }

        var undo = JsonSerializer.Deserialize<AutoFixUndo>(undoJson, Json)
            ?? throw new InvalidOperationException("The record of this fix cannot be read, so it cannot be undone. Edit the flow instead.");
        var map = (await maps.GetAsync(workspace.Id, [undo.FlowId], ct).ConfigureAwait(false)).FirstOrDefault();

        // A deleted flow's id can be given to a new flow: the name must match too.
        if (map is null || !string.Equals(map.Name, undo.FlowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Flow '{undo.FlowName}' no longer exists.");
        }

        if (map.Version != undo.VersionAfter)
        {
            throw new InvalidOperationException(
                $"Flow '{map.Name}' changed since the fix (v{undo.VersionAfter}, now v{map.Version}), so it is not undone automatically. Edit the flow on the Flows page instead.");
        }

        // The step keeps the branch it was last seen on: a step with no branch counts as on another branch and would never be queued again.
        var steps = map.Steps
            .Select(step => string.Equals(step.Spec, undo.NewSpec, StringComparison.OrdinalIgnoreCase) ? step with { Spec = undo.OldSpec } : step)
            .ToList();
        var saved = await maps.ReplaceAsync(workspace.Id, map.Name, map.Version, null, null, steps, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (saved.Outcome != FlowWrite.Saved)
        {
            throw new InvalidOperationException($"Flow '{map.Name}' changed while undoing. Reload and try again.");
        }

        await notes.RebaseStepAsync(workspace.Id, map.Id, undo.NewSpec, undo.OldSpec, ct).ConfigureAwait(false);
        await notes.RebaseFlowVersionAsync(workspace.Id, map.Id, map.Version, saved.Version, ct).ConfigureAwait(false);
        await repairs.MarkUndoneAsync(workspace.Id, repairId, time.GetUtcNow(), ct).ConfigureAwait(false);
        await audit.AppendAsync(
            AuditEntries.Create(
                time,
                AuditCategory.Config,
                "config.memory_repair_undone",
                AuthDefaults.AdminActor,
                workspace.Name,
                AuditOutcome.Ok,
                $"flow '{map.Name}' v{saved.Version}: step back to {undo.OldSpec}"),
            ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(workspace.Id)).ConfigureAwait(false);
        await events.PublishAsync(new FlowChanged(workspace.Id, map.Id, FlowChange.Saved)).ConfigureAwait(false);
    }
}
