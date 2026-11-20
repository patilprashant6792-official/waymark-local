using System.Globalization;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Notes;

/// <summary>A note drafted from pasted instruction text, before the owner saves it.</summary>
public sealed record NoteDraft(string Body, NoteKind Kind, string? About);

/// <summary>What an import of drafts did: how many were saved, and why the others were not.</summary>
public sealed record NoteImportResult(int Saved, IReadOnlyList<string> Problems);

/// <summary>
/// What the owner does with notes in the admin UI: read them, add and edit their own, retire or confirm any, pin and unpin, and import drafts from a pasted
/// CLAUDE.md. Every change goes through <see cref="INoteService"/> as the human author (the admin actor), so the same rules and secret check apply as for Claude,
/// and every change is written to the audit trail. The service publishes NoteChanged; the pages reload on it.
/// </summary>
public sealed class NoteAdminService(INoteService notes, INoteStore store, IMemorySearch search, IAuditStore audit, TimeProvider time)
{
    public const int PageSize = 100;

    /// <summary>The owner as the author of a note.</summary>
    public static NoteAuthor Owner { get; } = new(NoteAuthorKind.Human, AuthDefaults.AdminActor);

    public Task<NotePage> ListAsync(long workspaceId, NoteFilter filter, int page, CancellationToken ct) =>
        store.ListWorkspaceAsync(workspaceId, filter, Math.Max(0, page) * PageSize, PageSize, ct);


    public Task<IReadOnlyList<Note>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct) => store.GetAsync(workspaceId, ids, ct);
    public Task<IReadOnlyList<Note>> ForFlowsAsync(long workspaceId, IReadOnlyList<long> mapIds, CancellationToken ct) =>
        store.ListForFlowsAsync(workspaceId, mapIds, ct);

    public Task<IReadOnlyList<Note>> HistoryAsync(long workspaceId, long id, CancellationToken ct) => store.HistoryAsync(workspaceId, id, ct);

    public Task<IReadOnlyList<Note>> PinnedAsync(long workspaceId, CancellationToken ct) => store.ListPinnedAsync(workspaceId, ct);

    public Task<IReadOnlyDictionary<long, NoteStaleness>> CheckAsync(Workspace workspace, IReadOnlyList<Note> list, CancellationToken ct) =>
        notes.CheckAsync(workspace, list, ct);

    public Task<MemorySearchResult> SearchAsync(long workspaceId, string query, CancellationToken ct) => search.SearchAsync(workspaceId, query, 30, ct);

    public async Task<NoteSaved> AddAsync(Workspace workspace, string? flow, string? step, string? about, NoteKind kind, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var saved = await notes.AddAsync(workspace, Owner, flow, step, about, NoteKinds.ToText(kind), body, ct).ConfigureAwait(false);
        await AuditAsync("config.note_added", workspace, saved.Note, ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> EditAsync(Workspace workspace, long id, string body, NoteKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var saved = await notes.EditAsync(workspace, Owner, id, body, NoteKinds.ToText(kind), ct).ConfigureAwait(false);
        await AuditAsync("config.note_edited", workspace, saved, ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> RetireAsync(Workspace workspace, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var saved = await notes.RetireAsync(workspace, Owner, id, ct).ConfigureAwait(false);
        await AuditAsync("config.note_retired", workspace, saved, ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> ConfirmAsync(Workspace workspace, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var saved = await notes.ConfirmAsync(workspace, Owner, id, ct).ConfigureAwait(false);
        await AuditAsync("config.note_confirmed", workspace, saved, ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> SetPinnedAsync(Workspace workspace, long id, bool pinned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var saved = await notes.PinAsync(workspace, Owner, id, pinned, ct).ConfigureAwait(false);
        await AuditAsync(pinned ? "config.note_pinned" : "config.note_unpinned", workspace, saved, ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>Saves the drafts the owner kept as human notes, one by one; a draft that is refused (a secret, too long) is reported and the rest are saved.</summary>
    public async Task<NoteImportResult> ImportAsync(Workspace workspace, IReadOnlyList<NoteDraft> drafts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(drafts);
        var saved = 0;
        var problems = new List<string>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            try
            {
                await notes.AddAsync(workspace, Owner, null, null, draft.About, NoteKinds.ToText(draft.Kind), draft.Body, ct).ConfigureAwait(false);
                saved++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"Draft {i + 1}: {ex.Message}"));
            }
        }

        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, "config.note_imported", AuthDefaults.AdminActor, workspace.Name, AuditOutcome.Ok, string.Create(CultureInfo.InvariantCulture, $"{saved} saved, {problems.Count} refused")),
            ct).ConfigureAwait(false);
        return new NoteImportResult(saved, problems);
    }

    // The audit row names the note and its anchor, never its text: the text is in the database already and may be long.
    private Task AuditAsync(string action, Workspace workspace, Note note, CancellationToken ct) =>
        audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, workspace.Name, AuditOutcome.Ok, string.Create(CultureInfo.InvariantCulture, $"note #{note.Id} ({NoteKinds.ToText(note.Kind)})")),
            ct);
}
