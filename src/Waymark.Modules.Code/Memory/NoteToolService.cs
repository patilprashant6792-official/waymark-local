using System.Text;
using Waymark.Core.Memory;
using Waymark.Core.Security;

namespace Waymark.Modules.Code.Memory;

/// <summary>memory_save's note actions: add, edit, retire and confirm through <see cref="INoteService"/>, with a reply that says what was saved and what to do next.</summary>
internal sealed class NoteToolService(IPathGuard guard, INoteService notes, IMemoryUsage usage)
{
    public async Task<string> AddAsync(string? workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? note, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var saved = await notes.AddAsync(found, author, flow, step, about, kind, note, ct).ConfigureAwait(false);
        var reply = new StringBuilder();
        reply.Append($"Saved note #{saved.Note.Id} ({NoteKinds.ToText(saved.Note.Kind)}) on {NoteLines.Anchor(saved.Note)} in workspace '{found.Name}', written by {(author.IsHuman ? "the owner" : "an agent")}.");
        if (saved.Note.CodeHash is not null)
        {
            reply.Append(" It shows as may be stale when that code changes; after checking, memory_save(action='confirm_note') re-baselines it.");
        }

        if (saved.Similar.Count > 0)
        {
            reply.Append("\nSimilar notes on the same anchor (confirm or edit one of those instead of keeping a duplicate; retire this one if it repeats them):");
            foreach (var similar in saved.Similar)
            {
                reply.Append("\n- ").Append(NoteLines.Line(similar, null));
            }

            reply.Append('\n').Append(NoteRules.HintLine);
        }

        return reply.ToString();
    }

    public async Task<string> EditAsync(string? workspace, NoteAuthor author, long? noteId, string? note, string? kind, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var id = Require(noteId, "edit_note");
        if (note is null && kind is null)
        {
            throw new ArgumentException("edit_note needs note (the new text) or kind.");
        }

        var saved = await notes.EditAsync(found, author, id, note, kind, ct).ConfigureAwait(false);
        TouchIfAgent(found.Id, author, saved.Id);
        return $"Saved note #{saved.Id}, the new revision of #{id} (kept as history: memory_recall(action='notes', history={saved.Id})).";
    }

    public async Task<string> RetireAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var id = Require(noteId, "retire_note");
        await notes.RetireAsync(found, author, id, ct).ConfigureAwait(false);
        return $"Retired note #{id}. It is kept as history and no longer shown or searched.";
    }

    public async Task<string> ConfirmAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var id = Require(noteId, "confirm_note");
        await notes.ConfirmAsync(found, author, id, ct).ConfigureAwait(false);
        TouchIfAgent(found.Id, author, id);
        return $"Confirmed note #{id} against the code as it is now; it is no longer marked may be stale.";
    }

    private static long Require(long? noteId, string action) =>
        noteId is > 0 ? noteId.Value : throw new ArgumentException($"{action} needs noteId: the number after 'note #' in memory_recall.");

    // An agent that edits or confirms a note read it and acted on it: that is a use.
    private void TouchIfAgent(long workspaceId, NoteAuthor author, long noteId)
    {
        if (author.IsAgent)
        {
            usage.Touch(workspaceId, MemoryItemKind.Note, [noteId]);
        }
    }
}
