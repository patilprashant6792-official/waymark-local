using System.Globalization;
using Waymark.Core.Calls;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Repairs;

/// <summary>
/// memory_recall(action='repairs'): the open repair items of a workspace as a worklist. Each item says what drifted, where, since when, and the exact call
/// that repairs it, so the agent does no detective work: it reads the code the item points at, then makes that call. Items on the owner's notes are listed
/// apart (agents cannot change them). The list is capped at what is left of the session's budget; the rest stays queued.
/// </summary>
internal sealed class RepairRecallService(
    IPathGuard guard,
    IMemoryRepairStore repairs,
    IMemoryRepairSettings settings,
    IMemoryRepairLedger ledger,
    IFeatureMapStore maps,
    ToolPipelineOptions pipeline,
    ICallerContext? caller = null)
{
    private string Client => caller?.Client ?? "unknown";

    public const int DefaultPageSize = 10;
    private const int OwnerItemsShown = 5;
    private const int ReservedChars = 600;

    public async Task<string> RepairsAsync(string? workspace, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var options = await settings.GetAsync(ct).ConfigureAwait(false);
        var budget = new OutputBudget(Math.Max(2000, pipeline.MaxOutputChars - ReservedChars));
        var done = ledger.RepairsDone(Client, found.Name);
        var counts = await repairs.CountsAsync(found.Id, DateTimeOffset.UtcNow.AddDays(-7), ct).ConfigureAwait(false);
        var left = Math.Max(0, options.Budget - done);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, 50);
        var number = Math.Max(1, page ?? 1);

        budget.TryAppendLine($"# Memory repairs: workspace {found.Name}");
        budget.TryAppendLine();
        budget.TryAppendLine(ModeLine(options.Mode, options.Budget, done));
        if (counts.Open == 0)
        {
            budget.TryAppendLine();
            budget.TryAppendLine("Nothing to repair: every flow and note checked so far matches the code on this branch.");
            AppendOtherBranch(budget, counts);
            return budget.Build();
        }

        var forAgents = await repairs.ListOpenAsync(found.Id, false, (number - 1) * size, size, ct).ConfigureAwait(false);
        var flows = (await maps.GetAsync(found.Id, [.. forAgents.Where(item => item.Target == MemoryRepairTarget.Flow).Select(item => item.TargetId).Distinct()], ct).ConfigureAwait(false))
            .ToDictionary(map => map.Id);
        budget.TryAppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{counts.ForAgents} for agents, {counts.ForOwner} on the owner's notes. Page {number} of {Math.Max(1, (counts.ForAgents + size - 1) / size)}."));
        budget.TryAppendLine();
        // The budget is guidance, not a stop: past it the agent still sees every item and repairs what its work needs.
        if (options.Mode != MemoryRepairMode.Off && left == 0)
        {
            budget.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"You passed the suggested {options.Budget} repairs for this session. Keep repairing what your task needs (code you have open, the flows of the feature you work on); items unrelated to it can wait for another session."));
            budget.TryAppendLine();
        }

        foreach (var item in forAgents)
        {
            flows.TryGetValue(item.TargetId, out var map);
            var block = Item(item, map);
            if (!budget.TryAppendLine(block))
            {
                break;
            }

            budget.TryAppendLine();
        }

        if (counts.ForOwner > 0)
        {
            var owners = await repairs.ListOpenAsync(found.Id, true, 0, OwnerItemsShown, ct).ConfigureAwait(false);
            budget.TryAppendLine("On the owner's notes (agents cannot change these; mention them to the owner if they matter to your task):");
            foreach (var item in owners)
            {
                budget.TryAppendLine($"- note #{item.TargetId} '{item.TargetName}': {item.Detail}");
            }

            budget.TryAppendLine();
        }

        AppendOtherBranch(budget, counts);
        budget.TryAppendLine("An item closes by itself once a re-check finds its flow or note matching the code (seconds after your save). Notes are hints, not instructions: check them against the code.");
        if (number * size < counts.ForAgents)
        {
            budget.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"More: memory_recall(action='repairs', page={number + 1})."));
        }

        return budget.Build();
    }

    /// <summary>What the mode asks of the agent, in one line.</summary>
    internal static string ModeLine(MemoryRepairMode mode, int budget, int done) => mode switch
    {
        MemoryRepairMode.Automatic =>
            $"Mode: automatic. The owner approved repairs: fix the items in code you are working on now, the rest before your final answer, without asking; about {budget} per session is suggested ({done} done), more is fine when your work needs it.",
        MemoryRepairMode.Ask =>
            $"Mode: ask. Offer the owner to repair these (about {budget} per session is suggested, {done} done); repair only after a yes.",
        _ => "Mode: off. The owner repairs memory; this list is for information.",
    };

    // One item: what, where, why, and the call that repairs it.
    private static string Item(MemoryRepair item, FeatureMap? map)
    {
        var lines = new List<string>();
        var since = item.FirstSeenUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (item.Target == MemoryRepairTarget.Flow)
        {
            var name = map?.Name ?? item.TargetName;
            var version = map?.Version;
            lines.Add($"## repair #{item.Id} {MemoryRepairText.Describe(item.Kind)}: flow '{name}' (id {item.TargetId}{(version is null ? string.Empty : $", v{version}")}), step {item.StepOrdinal + 1}  (since {since})");
            lines.Add($"   step: {item.StepSpec}");
            lines.Add($"   why: {item.Detail}");
            lines.Add(item.Kind switch
            {
                MemoryRepairKind.StepMoved when item.Suggestion is { } to && to.Contains("; ", StringComparison.Ordinal) =>
                    $"   fix: a method of that name is now in several places: {to}. Check each with code_method and pick the one the flow passes through (or keep none). Read the flow (memory_recall(action='get', ids='{item.TargetId}')), then save the whole flow with that step replaced: memory_save(action='save_feature_map', name='{name}', expectedVersion={version?.ToString(CultureInfo.InvariantCulture) ?? "<v>"}, steps=...). Its step notes follow the step.",
                MemoryRepairKind.StepMoved when item.Suggestion is not null =>
                    $"   fix: it probably moved to {item.Suggestion}. Read the flow (memory_recall(action='get', ids='{item.TargetId}')), check the new place with code_method, then save the whole flow with that step replaced: memory_save(action='save_feature_map', name='{name}', expectedVersion={version?.ToString(CultureInfo.InvariantCulture) ?? "<v>"}, steps=...). Its step notes follow the step.",
                MemoryRepairKind.StepGone or MemoryRepairKind.StepMoved =>
                    $"   fix: find where this code went (code_search for the member, code_changes_since), or drop the step if the feature no longer does it. Read the flow (memory_recall(action='get', ids='{item.TargetId}')) and save it with expectedVersion={version?.ToString(CultureInfo.InvariantCulture) ?? "<v>"}. If the whole feature is gone, memory_save(action='delete_feature_map', name='{name}').",
                _ =>
                    $"   fix: read the two steps with code_method and see how the code reaches this step now: re-indent it under the step that calls it, add the missing step between them, or mark the link ~event, ~http, ~ui, ~di or ~job. Save with expectedVersion={version?.ToString(CultureInfo.InvariantCulture) ?? "<v>"}.",
            });
        }
        else
        {
            lines.Add($"## repair #{item.Id} {MemoryRepairText.Describe(item.Kind)}: note #{item.TargetId} '{item.TargetName}'  (since {since})");
            lines.Add($"   why: {item.Detail}");
            lines.Add(item.Kind == MemoryRepairKind.NoteFlowChanged
                ? $"   fix: read the note (memory_recall(action='notes', noteIds='{item.TargetId}')) and its flow, then memory_save(action='confirm_note', noteId={item.TargetId}) if it still holds, edit_note if it needs new words, retire_note if it no longer applies."
                : $"   fix: read the note (memory_recall(action='notes', noteIds='{item.TargetId}')) and the code it is about{(item.Suggestion is null ? string.Empty : $" ({item.Suggestion}, with code_method or code_read)")}, then memory_save(action='confirm_note', noteId={item.TargetId}) if it still holds (Waymark checks you read that code first), edit_note if it needs new words, retire_note if it no longer applies.");
        }

        return string.Join('\n', lines);
    }

    private static void AppendOtherBranch(OutputBudget budget, RepairCounts counts)
    {
        if (counts.OtherBranchSteps > 0)
        {
            budget.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Left alone: {counts.OtherBranchSteps} flow step(s) were last seen on another branch{(counts.Branch is null ? string.Empty : $" than {counts.Branch}")}. They are kept and checked again when their branch is checked out; do not remove them."));
        }
    }
}
