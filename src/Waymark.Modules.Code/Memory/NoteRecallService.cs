using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// recall(action='notes'): every active note of some flows or of the whole workspace, or the revisions of one note, each on one line with its anchor, author and staleness.
/// Bounded by the tool output limit.
/// </summary>
internal sealed class NoteRecallService(IPathGuard guard, INoteStore notes, INoteService service, ToolPipelineOptions pipeline)
{
    public const string WorkspaceIds = "workspace";
    private const int ReservedChars = 600;
    private const int MaxListed = 200;

    public async Task<string> NotesAsync(string? workspace, string? ids, long? history, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var budget = new OutputBudget(Math.Max(2000, pipeline.MaxOutputChars - ReservedChars));
        if (history is { } id)
        {
            var chain = await notes.HistoryAsync(found.Id, id, ct).ConfigureAwait(false);
            if (chain.Count == 0)
            {
                throw new KeyNotFoundException($"No note #{id} in workspace '{found.Name}'. recall(action='notes', ids='workspace') lists the workspace's notes.");
            }

            budget.TryAppendLine($"# Note #{id}: {chain.Count} revision(s), oldest first ({NoteLines.Anchor(chain[^1])})");
            budget.TryAppendLine();
            foreach (var revision in chain)
            {
                budget.TryAppendLine("- " + NoteLines.Line(revision, null));
            }

            budget.TryAppendLine();
            budget.TryAppendLine(NoteRules.HintLine);
            return budget.Build();
        }

        IReadOnlyList<Note> list;
        string title;
        var text = (ids ?? string.Empty).Trim();
        if (text.Length == 0 || string.Equals(text, WorkspaceIds, StringComparison.OrdinalIgnoreCase))
        {
            var page = await notes.ListWorkspaceAsync(found.Id, NoteFilter.All, 0, MaxListed, ct).ConfigureAwait(false);
            list = page.Items;
            title = $"# Notes: workspace {found.Name}, {page.Total} active";
        }
        else
        {
            var flowIds = FlowArgs.ParseIds(text);
            list = await notes.ListForFlowsAsync(found.Id, flowIds, ct).ConfigureAwait(false);
            title = $"# Notes: workspace {found.Name}, flow {string.Join(", ", flowIds)}: {list.Count} active";
        }

        if (list.Count == 0)
        {
            return $"{title}\n\nNo notes. Add one with memorise(action='add_note') when you learn something the code does not say (why, a gotcha, a convention).";
        }

        var staleness = await service.CheckAsync(found, list, ct).ConfigureAwait(false);
        budget.TryAppendLine(title);
        budget.TryAppendLine();
        foreach (var group in list.GroupBy(NoteLines.Anchor))
        {
            budget.TryAppendLine(group.Key + ":");
            foreach (var note in group)
            {
                budget.TryAppendLine("  " + NoteLines.Line(note, staleness.TryGetValue(note.Id, out var judged) ? judged : null));
            }
        }

        budget.TryAppendLine();
        budget.TryAppendLine(NoteRules.HintLine);
        budget.TryAppendLine("After checking a note marked may be stale against the code: memorise(action='confirm_note', noteId=...) if it still holds; edit_note or retire_note on your own notes if not.");
        return budget.Build();
    }
}
