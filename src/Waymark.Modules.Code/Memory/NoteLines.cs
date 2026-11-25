using System.Globalization;
using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The notes of the flows in one memory_recall answer, with what is known about their staleness. <see cref="Shown"/> collects the ids of the notes whose line was
/// actually written into the answer, so only those count as used.
/// </summary>
internal sealed record FlowNotes(IReadOnlyList<Note> Notes, IReadOnlyDictionary<long, NoteStaleness> Staleness)
{
    public static FlowNotes Empty => new([], new Dictionary<long, NoteStaleness>());

    public List<long> Shown { get; } = [];

    public NoteStaleness? StalenessOf(long id) => Staleness.TryGetValue(id, out var found) ? found : null;
}

/// <summary>
/// How a note reads in a reply to Claude: one line, with its id, kind, author label and date, a staleness mark, its title, then the body cut to fit.
/// Lists that only point at notes use <see cref="Pointer"/>: id, kind and title, and the reader opens the ones it wants (memory_recall notes with noteIds).
/// Notes are untrusted text, so a title or body can never start a line of its own or run on without a cap.
/// </summary>
internal static class NoteLines
{
    /// <summary>'owner' or 'agent'. Every AI client is the same author; the client a revision came through is kept in the database, not shown to models.</summary>
    public static string AuthorLabel(NoteAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        return author.Label;
    }

    /// <summary>
    /// 'note #41 gotcha [owner · 2026-11-12] may be stale (code changed): One notice per call — Only one notice per call, …'.
    /// A note from before titles has none: its line is the body alone, as it was.
    /// </summary>
    public static string Line(Note note, NoteStaleness? staleness, bool withDate = true, int bodyChars = NoteRules.MaxLineChars)
    {
        ArgumentNullException.ThrowIfNull(note);
        var text = Head(note, staleness, withDate).Append(": ");
        if (note.Title.Length > 0)
        {
            text.Append(NoteRules.OneLine(note.Title, NoteRules.MaxTitleChars)).Append(" — ");
        }

        return text.Append(NoteRules.OneLine(note.Body, bodyChars)).ToString();
    }

    /// <summary>'note #41 gotcha [owner] may be stale (code changed): One notice per call' — the title only, for lists that point at notes.</summary>
    public static string Pointer(Note note, NoteStaleness? staleness = null)
    {
        ArgumentNullException.ThrowIfNull(note);
        return Head(note, staleness, withDate: false).Append(": ").Append(TitleText(note)).ToString();
    }

    /// <summary>
    /// The title on one line. A note from before titles has none, so it reads '(untitled) ' and the start of its body; nothing is stored for it.
    /// </summary>
    public static string TitleText(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.Title.Length > 0
            ? NoteRules.OneLine(note.Title, NoteRules.MaxTitleChars)
            : "(untitled) " + NoteRules.OneLine(note.Body, NoteRules.MaxTitleChars);
    }

    /// <summary>The call that opens these notes in full.</summary>
    public static string OpenCall(IEnumerable<long> ids) =>
        $"memory_recall(action='notes', noteIds='{string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))}')";

    private static StringBuilder Head(Note note, NoteStaleness? staleness, bool withDate)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"note #{note.Id} {NoteKinds.ToText(note.Kind)} [{AuthorLabel(note.Author)}");
        if (withDate)
        {
            text.Append(" · ").Append(note.UpdatedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        text.Append(']');
        if (note.Pinned)
        {
            text.Append(" pinned");
        }

        if (staleness is { MayBeStale: true })
        {
            text.Append(" may be stale (").Append(staleness.Reason ?? "changed").Append(')');
        }

        if (!note.IsActive)
        {
            text.Append(note.RetiredUtc is not null ? " retired" : " replaced");
        }

        return text;
    }

    /// <summary>Where a note is anchored, in a few words: 'about src/A.cs', 'flow x step A.Run', 'workspace', 'detached from x'.</summary>
    public static string Anchor(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.IsDetached)
        {
            return note.StepSpec is null ? $"detached from flow {note.DetachedFrom}" : $"detached from flow {note.DetachedFrom}, step {NoteEmbedText.Label(note.StepSpec)}";
        }

        if (note.FlowName is { } flow)
        {
            return note.StepSpec is null ? $"flow {flow} (id {note.MapId})" : $"flow {flow} (id {note.MapId}) step {NoteEmbedText.Label(note.StepSpec)}";
        }

        return note.About is null ? "workspace" : $"about {note.About}";
    }
}
