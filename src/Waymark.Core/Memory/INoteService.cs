using Waymark.Core.Workspaces;

namespace Waymark.Core.Memory;

/// <summary>A saved note and, for a new one, up to three active notes on the same anchor that look alike (a hint to confirm or edit instead of adding a duplicate).</summary>
public sealed record NoteSaved(Note Note, IReadOnlyList<Note> Similar);

/// <summary>Whether an anchored note may be stale: its code or its flow changed since it was written or confirmed.</summary>
public sealed record NoteStaleness(long NoteId, bool MayBeStale, string? Reason);

/// <summary>
/// The only write path for notes, for Claude (memory_save) and for the owner (admin UI). Each call takes the author from the server, checks the text
/// (length, secrets), the anchor (the flow and step must exist; a step is the exact text memory_recall shows) and the writer rules, then stores and publishes NoteChanged.
/// Expected refusals are <see cref="ArgumentException"/>, <see cref="KeyNotFoundException"/> or <see cref="InvalidOperationException"/> with a message that says how to fix the call.
/// </summary>
public interface INoteService
{
    /// <param name="flow">A flow name for a flow or step note; null for a workspace note.</param>
    /// <param name="step">A step of that flow, as memory_recall shows it; null for a flow note.</param>
    /// <param name="about">For a workspace note: 'path' or 'path:Type.Member'.</param>
    /// <param name="title">Required: a few words that say what the note is about (<see cref="NoteRules.MaxTitleChars"/> at most).</param>
    Task<NoteSaved> AddAsync(Workspace workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? title, string? body, CancellationToken ct);

    /// <summary>Saves a new revision of the author's own note; the old one is kept as history. A null title, body or kind keeps the current one.</summary>
    Task<Note> EditAsync(Workspace workspace, NoteAuthor author, long id, string? title, string? body, string? kind, CancellationToken ct);

    Task<Note> RetireAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct);

    /// <summary>Re-baselines the note on the code as it is now: "may be stale" goes away until the code changes again.</summary>
    Task<Note> ConfirmAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct);

    /// <summary>Owner only. Pinned notes are in the workspace guide, within the pin budget.</summary>
    Task<Note> PinAsync(Workspace workspace, NoteAuthor author, long id, bool pinned, CancellationToken ct);

    /// <summary>For each note with an anchor that can be checked: whether the code or flow changed since it was written or confirmed. Never fails on missing code.</summary>
    Task<IReadOnlyDictionary<long, NoteStaleness>> CheckAsync(Workspace workspace, IReadOnlyList<Note> notes, CancellationToken ct);
}
