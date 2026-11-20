using System.Globalization;
using System.Text;
using Waymark.Core.Commands;
using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Tells an agent what is live in one workspace: the flows saved for it, the owner's pinned notes, how many other notes there are, and the command gates
/// the owner set for it alone. Names and switches only, like the overview's live setup: no paths, no secrets.
/// </summary>
public sealed class WorkspaceSetup(
    IFeatureMapStore featureMaps,
    ICommandPolicyStore policy,
    IPinnedNotes pinned,
    INoteStore notes,
    IMemorySettings memory,
    TimeProvider time) : IWorkspaceSetup
{
    internal const int MaxMapNamesPerWorkspace = 15;

    /// <summary>With search by meaning on, one recall list query finds the right flow, so the guide lists only the newest few.</summary>
    internal const int MaxMapNamesWithMeaning = 5;

    private const int MaxSummaryChars = 60;
    private const int MaxPinnedLineChars = 240;

    public async Task<string> DescribeAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = new StringBuilder();
        text.Append("Snapshot taken ").Append(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(". Call ").Append(GuideRules.OverviewTool).Append(" with workspace='").Append(workspace.Name).AppendLine("' for a fresh one.");
        text.AppendLine(workspace.Enabled
            ? "- State: on."
            : "- State: OFF. Every tool call in this workspace is refused until the owner switches it on in the admin UI.");

        var meaning = await MeaningOnAsync(workspace.Id, ct);
        var shown = meaning ? MaxMapNamesWithMeaning : MaxMapNamesPerWorkspace;
        var index = await featureMaps.ListNamesAsync(MaxMapNamesPerWorkspace, ct);
        text.Append("- Flows saved (a flow is one trigger to one outcome as ordered method steps; recall with action 'get' and an id describes one in a single call, with no code; action 'list' with a query searches flows and notes")
            .Append(meaning ? ", search by meaning is on" : string.Empty)
            .AppendLine("; memorise saves or changes one):");
        if (!index.TryGetValue(workspace.Id, out var entry) || entry.Total == 0)
        {
            text.AppendLine("  - none yet");
        }
        else
        {
            var newest = entry.Newest.Take(shown).ToList();
            foreach (var map in newest)
            {
                text.Append("  - ").AppendLine(FlowLine(map));
            }

            if (entry.Total > newest.Count)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - and {entry.Total - newest.Count} more (action 'list')"));
            }
        }

        await DescribeNotesAsync(text, workspace.Id, ct);

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
                $"- Notes: {more} {(pins.Count > 0 ? "more" : (more == 1 ? "note" : "notes"))}; recall(action='list', query=...) searches them, and recall get shows those on a flow's steps."));
        }
    }

    // One line per pinned note: id, kind, where it is anchored, then the note cut to fit. A note can never start a line of its own.
    internal static string PinnedLine(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var anchor = note.StepSpec is not null
            ? $"flow {note.FlowName ?? note.DetachedFrom} step {NoteEmbedText.Label(note.StepSpec)}"
            : note.FlowName is not null ? $"flow {note.FlowName}"
            : note.About is not null ? $"about {note.About}"
            : "workspace";
        return string.Create(CultureInfo.InvariantCulture, $"#{note.Id} {NoteKinds.ToText(note.Kind)} ({anchor}): {NoteRules.OneLine(note.Body, MaxPinnedLineChars)}");
    }

    // Search by meaning shortens the flow list only when it actually runs for this workspace; any doubt keeps the full list.
    private async Task<bool> MeaningOnAsync(long workspaceId, CancellationToken ct)
    {
        try
        {
            var settings = await memory.GetAsync(ct);
            return settings.IsActive && !await memory.IsExcludedAsync(workspaceId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    // One line on one flow: enough for an agent to pick it without listing first. The summary is cut short; recall get has the rest.
    internal static string FlowLine(FeatureMapName map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var steps = $"{map.StepCount} {(map.StepCount == 1 ? "step" : "steps")}{(map.Ordered ? string.Empty : ", unordered")}";
        var summary = map.Summary.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (summary.Length > MaxSummaryChars)
        {
            summary = summary[..(MaxSummaryChars - 3)] + "...";
        }

        return summary.Length == 0 ? $"{map.Id} {map.Name} ({steps})" : $"{map.Id} {map.Name} ({steps}): {summary}";
    }

    private static string GateWord(CommandGate gate) => gate switch
    {
        CommandGate.Off => "off (refused)",
        CommandGate.Ask => "asks the owner first",
        _ => "runs at once",
    };
}
