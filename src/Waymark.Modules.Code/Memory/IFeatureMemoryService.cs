using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The flows and notes behind the memory_recall and memory_save tools. Every path goes through the path guard, and every answer is text ready to show.
/// </summary>
/// <exception cref="ArgumentException">Missing or invalid input, or a step that is not in the code on this branch.</exception>
public interface IFeatureMemoryService
{
    /// <summary>
    /// Without a query: the flows of a workspace, newest first, paged. With a query: a ranked search over flows and notes (words, and meaning when the owner switched it on).
    /// </summary>
    Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct);

    /// <summary>
    /// The steps of the flows with these ids, each with its place in the code as it is now, the status of its link and the notes anchored to it. No code.
    /// A flow too long for one answer comes in parts; <paramref name="from"/> (1-based, one id only) continues it.
    /// </summary>
    Task<string> GetAsync(string? workspace, string? ids, int? from, CancellationToken ct);

    /// <summary>
    /// Creates the flow (without <paramref name="expectedVersion"/>) or replaces all its steps (with the version that was read). New or changed steps must exist now and their links must hold;
    /// steps that were stored already stay whatever branch they came from.
    /// </summary>
    Task<string> SaveAsync(string? workspace, string? name, string? summary, string? steps, int? expectedVersion, string? newName, string? distinct, CancellationToken ct);

    Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct);

    /// <summary>
    /// A page of the active notes of some flows (ids) or of a file or member (about); chosen notes in full by id (noteIds, after a list showed their titles);
    /// or the revisions of one note (history). Never the whole workspace.
    /// </summary>
    Task<string> NotesAsync(string? workspace, string? ids, string? noteIds, string? about, long? history, int? page, int? pageSize, CancellationToken ct);

    /// <summary>Adds a note with its title: on the workspace (optionally about a file or member), on a flow (flow), or on one of its steps (flow and step).</summary>
    Task<string> AddNoteAsync(string? workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? title, string? note, CancellationToken ct);

    /// <summary>A new revision; a null title, note or kind keeps the current one.</summary>
    Task<string> EditNoteAsync(string? workspace, NoteAuthor author, long? noteId, string? title, string? note, string? kind, CancellationToken ct);

    Task<string> RetireNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct);

    Task<string> ConfirmNoteAsync(string? workspace, NoteAuthor author, long? noteId, CancellationToken ct);
}
