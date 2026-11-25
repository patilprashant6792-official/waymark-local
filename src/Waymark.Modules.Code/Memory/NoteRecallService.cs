using System.Globalization;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// memory_recall(action='notes'): the notes of some flows, or of one file or member, a page at a time; chosen notes in full by id (noteIds, after a list showed
/// their titles); or the revisions of one note. Each on one line with its anchor, author and staleness. Notes are never listed in bulk: a whole-workspace listing
/// is refused and the agent searches with memory_recall list instead. Only the notes shown are checked against the code and count as used.
/// </summary>
internal sealed class NoteRecallService(IPathGuard guard, INoteStore notes, INoteService service, IMemoryUsage usage, ToolPipelineOptions pipeline)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;
    public const int MaxNoteIds = 10;
    private const int ReservedChars = 600;

    /// <summary>The answer to a call without an anchor (or the old ids='workspace'): how to find notes instead.</summary>
    internal const string NoBulkListing =
        "Notes are not listed in bulk. Search them with memory_recall(action='list', query='...'), or ask for the notes of flows (ids='12'), " +
        "of a file or member (about='src/A.cs' or 'src/A.cs:Type.Member'), chosen notes in full (noteIds='41,57'), or one note's revisions (history=41).";

    public async Task<string> NotesAsync(string? workspace, string? ids, string? noteIds, string? about, long? history, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var budget = new OutputBudget(Math.Max(2000, pipeline.MaxOutputChars - ReservedChars));
        var flowIds = (ids ?? string.Empty).Trim();
        var anchor = (about ?? string.Empty).Trim();
        var chosen = (noteIds ?? string.Empty).Trim();
        if (chosen.Length > 0)
        {
            if (flowIds.Length > 0 || anchor.Length > 0 || history is not null)
            {
                throw new ArgumentException("Give noteIds alone: it opens those notes in full. ids (flows), about (a file or member) and history are other ways to ask.");
            }

            return await ChosenAsync(found, ParseNoteIds(chosen), budget, ct).ConfigureAwait(false);
        }

        if (history is { } id)
        {
            return await HistoryAsync(found.Id, found.Name, id, budget, ct).ConfigureAwait(false);
        }

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

    // noteIds: the notes a list pointed at, in full (title and the whole body), in the order asked. Ids not in this workspace are named, not guessed.
    private async Task<string> ChosenAsync(Workspace found, List<long> wanted, OutputBudget budget, CancellationToken ct)
    {
        var loaded = await notes.GetAsync(found.Id, wanted, ct).ConfigureAwait(false);
        var missing = wanted.Except(loaded.Select(note => note.Id)).ToList();
        if (loaded.Count == 0)
        {
            throw new KeyNotFoundException($"No note {string.Join(", ", missing.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture)))} in workspace '{found.Name}'. memory_recall(action='list', query='...') finds notes by their words.");
        }

        var staleness = await service.CheckAsync(found, [.. loaded.Where(note => note.IsActive)], ct).ConfigureAwait(false);
        budget.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"# Notes: workspace {found.Name}, {loaded.Count} chosen"));
        budget.TryAppendLine();
        var shown = new List<long>();
        foreach (var note in loaded)
        {
            if (!budget.TryAppendLine($"{NoteLines.Anchor(note)}:") ||
                !budget.TryAppendLine("  " + NoteLines.Line(note, staleness.TryGetValue(note.Id, out var judged) ? judged : null, bodyChars: NoteRules.MaxBodyChars)))
            {
                break;
            }

            if (note.IsActive)
            {
                shown.Add(note.Id);
            }
        }

        budget.TryAppendLine();
        if (missing.Count > 0)
        {
            budget.TryAppendLine($"Not in this workspace: {string.Join(", ", missing.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture)))}.");
        }

        if (loaded.Any(note => !note.IsActive))
        {
            budget.TryAppendLine("A note marked replaced or retired is history: memory_recall(action='notes', history=<id>) shows its newest revision.");
        }

        budget.TryAppendLine(NoteRules.HintLine);
        usage.Touch(found.Id, MemoryItemKind.Note, shown);
        return budget.Build();
    }

    private static List<long> ParseNoteIds(string text)
    {
        var result = new List<long>();
        foreach (var part in text.Trim('[', ']').Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var digits = part.TrimStart('#');
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            {
                throw new ArgumentException($"noteIds are the numbers after 'note #', separated by commas: '{part}' is not one.");
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count switch
        {
            0 => throw new ArgumentException("noteIds is empty: give the numbers after 'note #', e.g. noteIds='41,57'."),
            > MaxNoteIds => throw new ArgumentException($"noteIds accepts at most {MaxNoteIds} notes per call."),
            _ => result,
        };
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
