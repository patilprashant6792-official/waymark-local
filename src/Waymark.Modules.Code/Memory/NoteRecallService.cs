using System.Globalization;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// memory_recall(action='notes'): the notes of some flows, or of one file or member, a page at a time, or the revisions of one note; each on one line with its anchor,
/// author and staleness. Notes are never listed in bulk: a whole-workspace listing is refused and the agent searches with memory_recall list instead.
/// Only the notes on the page are checked against the code and count as used.
/// </summary>
internal sealed class NoteRecallService(IPathGuard guard, INoteStore notes, INoteService service, IMemoryUsage usage, ToolPipelineOptions pipeline)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;
    private const int ReservedChars = 600;

    /// <summary>The answer to a call without an anchor (or the old ids='workspace'): how to find notes instead.</summary>
    internal const string NoBulkListing =
        "Notes are not listed in bulk. Search them with memory_recall(action='list', query='...'), or ask for the notes of flows (ids='12'), " +
        "of a file or member (about='src/A.cs' or 'src/A.cs:Type.Member'), or one note's revisions (history=41).";

    public async Task<string> NotesAsync(string? workspace, string? ids, string? about, long? history, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var budget = new OutputBudget(Math.Max(2000, pipeline.MaxOutputChars - ReservedChars));
        if (history is { } id)
        {
            return await HistoryAsync(found.Id, found.Name, id, budget, ct).ConfigureAwait(false);
        }

        var flowIds = (ids ?? string.Empty).Trim();
        var anchor = (about ?? string.Empty).Trim();
        if (flowIds.Length > 0 && anchor.Length > 0)
        {
            throw new ArgumentException("Give either ids (flows) or about (a file or member), not both.");
        }

        if (anchor.Length == 0 && (flowIds.Length == 0 || string.Equals(flowIds, "workspace", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(NoBulkListing);
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var skip = (number - 1) * size;
        NotePage result;
        string title;
        string next;
        if (anchor.Length > 0)
        {
            var clean = NoteAnchors.CleanAbout(anchor);
            result = await notes.PageByPathAsync(found.Id, clean, skip, size, ct).ConfigureAwait(false);
            title = $"# Notes: workspace {found.Name}, about {clean}";
            next = $"memory_recall(action='notes', about='{clean}', page={number + 1})";
        }
        else
        {
            var parsed = FlowArgs.ParseIds(flowIds);
            result = await notes.PageForFlowsAsync(found.Id, parsed, skip, size, ct).ConfigureAwait(false);
            var joined = string.Join(",", parsed);
            title = $"# Notes: workspace {found.Name}, flow {string.Join(", ", parsed)}";
            next = $"memory_recall(action='notes', ids='{joined}', page={number + 1})";
        }

        var pages = Math.Max(1, (result.Total + size - 1) / size);
        if (result.Total == 0)
        {
            return $"{title}\n\nNo notes. Add one with memory_save(action='add_note') when you learn something the code does not say (why, a gotcha, a convention).";
        }

        budget.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{title}: {result.Total} active, page {number} of {pages}"));
        budget.TryAppendLine();
        if (result.Items.Count == 0)
        {
            budget.TryAppendLine("This page is past the last one.");
            return budget.Build();
        }

        var staleness = await service.CheckAsync(found, result.Items, ct).ConfigureAwait(false);
        var shown = new List<long>();
        foreach (var group in result.Items.GroupBy(NoteLines.Anchor))
        {
            if (!budget.TryAppendLine(group.Key + ":"))
            {
                break;
            }

            foreach (var note in group)
            {
                if (!budget.TryAppendLine("  " + NoteLines.Line(note, staleness.TryGetValue(note.Id, out var judged) ? judged : null)))
                {
                    break;
                }

                shown.Add(note.Id);
            }
        }

        budget.TryAppendLine();
        if (number < pages)
        {
            budget.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"Page {number} of {pages}. Next: {next}"));
        }

        budget.TryAppendLine(NoteRules.HintLine);
        budget.TryAppendLine("After checking a note marked may be stale against the code: memory_save(action='confirm_note', noteId=...) if it still holds; edit_note or retire_note if it is an agent's note.");
        usage.Touch(found.Id, MemoryItemKind.Note, shown);
        return budget.Build();
    }

    private async Task<string> HistoryAsync(long workspaceId, string workspaceName, long id, OutputBudget budget, CancellationToken ct)
    {
        var chain = await notes.HistoryAsync(workspaceId, id, ct).ConfigureAwait(false);
        if (chain.Count == 0)
        {
            throw new KeyNotFoundException($"No note #{id} in workspace '{workspaceName}'. memory_recall(action='list', query='...') finds notes by their words.");
        }

        budget.TryAppendLine($"# Note #{id}: {chain.Count} revision(s), oldest first ({NoteLines.Anchor(chain[^1])})");
        budget.TryAppendLine();
        foreach (var revision in chain)
        {
            budget.TryAppendLine("- " + NoteLines.Line(revision, null));
        }

        budget.TryAppendLine();
        budget.TryAppendLine(NoteRules.HintLine);

        // Reading a note's history is a use of the note as it is now (its active revision).
        usage.Touch(workspaceId, MemoryItemKind.Note, [.. chain.Where(note => note.IsActive).Select(note => note.Id)]);
        return budget.Build();
    }
}
