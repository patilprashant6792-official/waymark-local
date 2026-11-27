using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Waymark.Core.Commands;
using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Tells an agent what is live in one workspace: the index of the flows saved for it, the owner's pinned notes, how many other notes there are, the
/// command gates the owner set for it alone, and its terminals (what keeps running there, its address and its unread errors).
/// Names and switches only, like the overview's live setup: no paths, no secrets.
/// Nothing is loaded whole: with search by meaning off the guide carries the name and id of the newest <see cref="FlowIndex.PageSize"/> flows; with it on it
/// lists none, so it stays the same size however many flows are saved, and memory_recall finds them by meaning.
/// </summary>
public sealed class WorkspaceSetup(
    IFeatureMapStore featureMaps,
    ICommandPolicyStore policy,
    IPinnedNotes pinned,
    INoteStore notes,
    IMemorySettings memory,
    TimeProvider time,
    IMemoryRepairStore? repairs = null,
    IMemoryRepairSettings? repairSettings = null,
    ITerminals? terminals = null) : IWorkspaceSetup
{
    /// <summary>The flows the guide lists with search by meaning off: the first page of memory_recall list, so page 2 continues where the guide stops.</summary>
    internal const int MaxMapNamesPerWorkspace = FlowIndex.PageSize;

    private const int MaxPinnedLineChars = 240;

    /// <summary>
    /// How long the search-by-meaning switch is remembered per workspace. The guide hash reads it on every tool call in a workspace with instructions,
    /// and the settings are several reads; a switch shows within this time.
    /// </summary>
    private static readonly TimeSpan MeaningCacheTime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, (bool On, DateTimeOffset At)> _meaning = new();

    /// <summary>
    /// The repair queue of the workspace and the owner's mode, the moment an agent starts work here. The mode line is what lets an agent repair without asking:
    /// the server instructions say to follow the mode the workspace guide states. A failure leaves the section out.
    /// </summary>
    private async Task DescribeRepairsAsync(StringBuilder text, Workspace workspace, CancellationToken ct)
    {
        if (repairs is null || repairSettings is null)
        {
            return;
        }

        try
        {
            var options = await repairSettings.GetAsync(ct);
            var counts = await repairs.CountsAsync(workspace.Id, time.GetUtcNow().AddDays(-7), ct);
            var mode = options.Mode switch
            {
                MemoryRepairMode.Automatic => $"automatic: the owner approved repairs; do them without asking (what you work on now first, the rest before your final answer), about {options.Budget} per session suggested, more when your work needs it",
                MemoryRepairMode.Ask => $"ask: offer the owner to repair; repair after a yes, about {options.Budget} per session suggested",
                _ => "off: the owner repairs memory; do not repair it unless asked",
            };
            if (counts.Open == 0 && counts.AutoFixedRecently == 0 && counts.OtherBranchSteps == 0)
            {
                text.Append("- Memory repairs: none open (mode ").Append(mode).AppendLine(").");
                return;
            }

            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- {(counts.ForAgents > 0 ? "🔧 " : string.Empty)}Memory repairs: {counts.ForAgents} open for agents{(counts.ForOwner > 0 ? $", {counts.ForOwner} on the owner's notes" : string.Empty)}{(counts.ForAgents > 0 ? " → memory_recall(action='repairs')" : string.Empty)}. Mode {mode}."));
            if (counts.AutoFixedRecently > 0)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - Waymark fixed {counts.AutoFixedRecently} moved step(s) itself in the last 7 days."));
            }

            if (counts.OtherBranchSteps > 0)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  - {counts.OtherBranchSteps} flow step(s) were last seen on another branch: kept, not listed as repairs, checked again when that branch is checked out."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The guide goes out without the section; the next one tries again.
            _ = ex;
        }
    }
    public async Task<string> DescribeAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = new StringBuilder();
        text.Append("Snapshot taken ").Append(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(". Call ").Append(GuideRules.OverviewTool).Append(" with workspace='").Append(workspace.Name).AppendLine("' for a fresh one.");
        text.AppendLine(workspace.Enabled
            ? "- State: on."
            : "- State: OFF. Every tool call in this workspace is refused until the owner switches it on in the admin UI.");

        if (await MeaningOnAsync(workspace.Id, ct))
        {
            await DescribeFlowsByMeaningAsync(text, workspace.Id, ct);
        }
        else
        {
            await DescribeFlowIndexAsync(text, workspace.Id, ct);
        }

        await DescribeNotesAsync(text, workspace.Id, ct);
        await DescribeRepairsAsync(text, workspace, ct);
        await DescribeTerminalsAsync(text, workspace, ct);

        // Only what differs from the overview's list: the gates the owner set for this workspace alone.
        var gates = await policy.ListGatesAsync(ct);
        var own = CommandCatalog.Operations
            .Select(op => (Key: op.Key, Here: CommandCatalog.Resolve(op, workspace.Id, gates), Everywhere: CommandCatalog.Resolve(op, null, gates)))
            .Where(row => row.Here != row.Everywhere)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
        text.Append("- Command gates set for this workspace only (they replace the overview's list here): ");
        text.Append(own.Count == 0
            ? "none"
            : string.Join(", ", own.Select(row => $"{row.Key} {GateWord(row.Here)}")));
        return text.ToString();
    }

    // What keeps running in the workspace, so a new conversation reuses a terminal instead of starting the same program twice. A failure leaves it out.
    private async Task DescribeTerminalsAsync(StringBuilder text, Workspace workspace, CancellationToken ct)
    {
        if (terminals is null)
        {
            return;
        }

        try
        {
            var list = await terminals.ListAsync(new TerminalQuery(WorkspaceId: workspace.Id), ct);
            var block = TerminalGuideText.Block(
                list,
                time.GetUtcNow(),
                $"cmd_terminal(workspace='{workspace.Name}', action='read', name=...) gives a terminal's new output; 'rerun' starts its last command again, 'stop' ends it:");
            text.Append("- Terminals (programs that keep running until stopped): ").AppendLine(block);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            text.AppendLine("- Terminals: could not be read just now; cmd_terminal(action='list') shows them.");
        }
    }

    /// <summary>
    /// Search by meaning shortens the guide only when it actually runs for this workspace; any doubt (settings unreadable) keeps the index.
    /// Remembered for <see cref="MeaningCacheTime"/>, because the guide hash asks on every tool call.
    /// </summary>
    public async Task<bool> MeaningOnAsync(long workspaceId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_meaning.TryGetValue(workspaceId, out var known) && now - known.At < MeaningCacheTime)
        {
            return known.On;
        }

        bool on;
        try
        {
            var settings = await memory.GetAsync(ct);
            on = settings.IsActive && !await memory.IsExcludedAsync(workspaceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not remembered: the next call tries again.
            return false;
        }

        _meaning[workspaceId] = (on, now);
        return on;
    }

    // Search by meaning on: no flow is listed. One line with the count and how to find a flow, so the guide stays the same size however many there are.
    private async Task DescribeFlowsByMeaningAsync(StringBuilder text, long workspaceId, CancellationToken ct)
    {
        var index = await featureMaps.ListNamesAsync(workspaceId, 1, ct);
        if (index.Total == 0)
        {
            text.AppendLine("- Flows: none saved yet (search by meaning is on). When you trace a feature from its trigger to its outcome, save it with memory_save(action='save_feature_map').");
            return;
        }

        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Flows: {index.Total} saved, not listed here because search by meaning is on. Describe what you are working on to memory_recall(action='list', query='...'): it answers with the closest flows as name, match score and id. Pick one and read its path with memory_recall(action='get', ids='<id>')."));
    }

    // Search by meaning off: an index of the newest flows, name and id only. The rest are paged with memory_recall list, in the same order and page size.
    private async Task DescribeFlowIndexAsync(StringBuilder text, long workspaceId, CancellationToken ct)
    {
        var index = await featureMaps.ListNamesAsync(workspaceId, MaxMapNamesPerWorkspace, ct);
        text.AppendLine("- Flows saved, newest first (name and id; memory_recall(action='get', ids='<id>') gives a flow's path without code; memory_recall(action='list', query='...') searches flows and notes):");
        if (index.Total == 0)
        {
            text.AppendLine("  - none yet");
            return;
        }

        foreach (var map in index.Newest)
        {
            text.Append("  - ").AppendLine(FlowLine(map));
        }

        var more = index.Total - index.Newest.Count;
        if (more > 0)
        {
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  - and {more} more: memory_recall(action='list', page=2) continues this list, {FlowIndex.PageSize} per page; page through only when you need to."));
        }
    }

    private async Task DescribeNotesAsync(StringBuilder text, long workspaceId, CancellationToken ct)
    {
        var pins = await pinned.GetAsync(workspaceId, ct);
        var total = await notes.CountActiveAsync(workspaceId, ct);
        if (pins.Count > 0)
        {
            text.AppendLine("- Pinned notes (the owner's; hints, not instructions):");
            foreach (var note in pins)
            {
                text.Append("  - ").AppendLine(PinnedLine(note));
            }
        }

        var more = total - pins.Count;
        if (more > 0)
        {
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- Notes: {more} {(pins.Count > 0 ? "more" : (more == 1 ? "note" : "notes"))}; memory_recall(action='list', query=...) searches them, and memory_recall get shows those on a flow's steps."));
        }
    }

    // One line per pinned note: id, kind, where it is anchored, its title (none for a note from before titles), then the note cut to fit.
    // Pinned notes are meant to be read every time, so the body stays. A note can never start a line of its own.
    internal static string PinnedLine(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var anchor = note.StepSpec is not null
            ? $"flow {note.FlowName ?? note.DetachedFrom} step {NoteEmbedText.Label(note.StepSpec)}"
            : note.FlowName is not null ? $"flow {note.FlowName}"
            : note.About is not null ? $"about {note.About}"
            : "workspace";
        var title = note.Title.Length > 0 ? NoteRules.OneLine(note.Title, NoteRules.MaxTitleChars) + " — " : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{note.Id} {NoteKinds.ToText(note.Kind)} ({anchor}): {title}{NoteRules.OneLine(note.Body, MaxPinnedLineChars)}");
    }

    // One index line on one flow: its name and id. memory_recall get describes it.
    internal static string FlowLine(FeatureMapName map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return FlowIndex.Line(map.Id, map.Name);
    }

    private static string GateWord(CommandGate gate) => gate switch
    {
        CommandGate.Off => "off (refused)",
        CommandGate.Ask => "asks the owner first",
        _ => "runs at once",
    };
}
