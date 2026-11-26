namespace Waymark.Core.Memory;

/// <summary>
/// A note to add. The anchor is the workspace (all of MapId, StepSpec, About null), a file or member of it (About), a flow (MapId) or one step of a flow (MapId and StepSpec).
/// <paramref name="Title"/> is already cleaned by the caller; null derives one from the body (<see cref="NoteRules.DeriveTitle"/>).
/// </summary>
public sealed record NewNote(
    long WorkspaceId,
    long? MapId,
    string? StepSpec,
    string? About,
    NoteKind Kind,
    string Body,
    NoteAuthor Author,
    string? CodeHash,
    int? FlowVersion,
    string? Title = null);

/// <summary>Which notes of a workspace a list shows. Everything but <see cref="Retired"/> is active notes only.</summary>
public enum NoteFilter
{
    All,
    Pinned,
    FromModels,
    Detached,
    Retired,
}

/// <param name="Total">All notes that match the filter, not only this page.</param>
public sealed record NotePage(IReadOnlyList<Note> Items, int Total);

/// <summary>A note found by its words. Higher <paramref name="Score"/> is better (the negated bm25 rank).</summary>
public sealed record NoteWordHit(long NoteId, double Score);

/// <summary>
/// The notes of the workspaces. Every write is one transaction that also keeps the full-text row (note_fts) in step: only active notes have one.
/// Writer rules are enforced in SQL as well as by the caller: an agent never changes the owner's notes.
/// </summary>
public interface INoteStore
{
    /// <summary>Stores a new active note and its full-text row.</summary>
    Task<Note> AddAsync(NewNote note, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Supersedes the note and inserts the new revision (same anchor, pin and last-used time, <see cref="Note.ReplacesId"/> set) in one transaction; the full-text row moves to the new id.
    /// The revision is written by <paramref name="author"/>. The owner may revise any note; an agent only an agent's note.
    /// </summary>
    Task<NoteWriteResult> ReviseAsync(long workspaceId, long id, NoteAuthor author, string title, string body, NoteKind kind, string? codeHash, int? flowVersion, DateTimeOffset now, CancellationToken ct);

    /// <summary>Retires the note (kept, no longer active, unpinned, full-text row removed). The owner may retire any note; an agent only an agent's note.</summary>
    Task<NoteWriteResult> RetireAsync(long workspaceId, long id, NoteAuthor author, DateTimeOffset now, CancellationToken ct);

    /// <summary>Stores the code hash and flow version the note now agrees with. The owner may confirm any note; an agent only an agent's note.</summary>
    Task<NoteWriteResult> ConfirmAsync(long workspaceId, long id, NoteAuthor author, string? codeHash, int? flowVersion, DateTimeOffset now, CancellationToken ct);

    /// <summary>Pins or unpins an active note. Who may pin and the pin budget are the caller's rules.</summary>
    Task<NoteWriteResult> SetPinnedAsync(long workspaceId, long id, bool pinned, DateTimeOffset now, CancellationToken ct);

    /// <summary>The notes of this workspace with these ids (any state), in the order asked; an id that is not here is left out.</summary>
    Task<IReadOnlyList<Note>> GetAsync(long workspaceId, IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>The active notes attached to these flows (flow and step notes), newest first.</summary>
    Task<IReadOnlyList<Note>> ListForFlowsAsync(long workspaceId, IReadOnlyList<long> mapIds, CancellationToken ct);

    /// <summary>A page of the workspace's notes, newest first (or last used first).</summary>
    Task<NotePage> ListWorkspaceAsync(long workspaceId, NoteFilter filter, int skip, int take, CancellationToken ct, MemoryOrder order = MemoryOrder.Changed);

    /// <summary>A page of the active notes attached to these flows (flow and step notes), last changed first, with the total.</summary>
    Task<NotePage> PageForFlowsAsync(long workspaceId, IReadOnlyList<long> mapIds, int skip, int take, CancellationToken ct);

    /// <summary>
    /// A page of the active notes anchored in a file ('path': notes about it or a member of it, step notes on it) or on one member ('path:Type.Member'),
    /// last changed first, with the total.
    /// </summary>
    Task<NotePage> PageByPathAsync(long workspaceId, string path, int skip, int take, CancellationToken ct);

    /// <summary>Every revision of the note, oldest first (following <see cref="Note.ReplacesId"/> both ways). Empty when the note is not in this workspace.</summary>
    Task<IReadOnlyList<Note>> HistoryAsync(long workspaceId, long id, CancellationToken ct);

    /// <summary>
    /// Active notes whose words match <paramref name="ftsQuery"/>, an FTS5 expression the caller built from sanitized words only (never user text as is), best first.
    /// Column weights: title and body 4, anchor 1.
    /// </summary>
    Task<IReadOnlyList<NoteWordHit>> SearchWordsAsync(long workspaceId, string ftsQuery, int take, CancellationToken ct);

    /// <summary>The ids of the workspaces that have at least one pinned active note.</summary>
    Task<IReadOnlyList<long>> ListPinnedWorkspaceIdsAsync(CancellationToken ct);

    /// <summary>The pinned active notes, oldest pin first.</summary>
    Task<IReadOnlyList<Note>> ListPinnedAsync(long workspaceId, CancellationToken ct);

    /// <summary>Every active note of the workspace (for the embedding index and the bundle export).</summary>
    Task<IReadOnlyList<Note>> ListActiveAsync(long workspaceId, CancellationToken ct);

    /// <summary>How many active notes the workspace has.</summary>
    Task<int> CountActiveAsync(long workspaceId, CancellationToken ct);

    /// <summary>Ids of active notes anchored in this file: a workspace note about it (or a member of it), or a step note whose step is in it. At most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<long>> FindActiveByPathAsync(long workspaceId, string path, int limit, CancellationToken ct);

    /// <summary>
    /// Moves the active step notes of a flow from one step to another (a step that was moved in the code and repaired in the flow), in place: no new revision,
    /// because the note says the same about the same code. The full-text rows follow. Returns how many notes moved.
    /// </summary>
    Task<int> RebaseStepAsync(long workspaceId, long mapId, string oldSpec, string newSpec, CancellationToken ct);

    /// <summary>
    /// Moves the active flow notes of a flow that were current at <paramref name="fromVersion"/> to <paramref name="toVersion"/>, in place: a change Waymark made
    /// itself (an automatic fix or its undo) gives a note no reason to be re-read. Notes already stale stay stale. Returns how many notes moved.
    /// </summary>
    Task<int> RebaseFlowVersionAsync(long workspaceId, long mapId, int fromVersion, int toVersion, CancellationToken ct);
}
