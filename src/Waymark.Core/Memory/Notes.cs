namespace Waymark.Core.Memory;

/// <summary>Who wrote a note: the owner in the admin UI, an MCP client (Claude) or a model Waymark runs itself.</summary>
public enum NoteAuthorKind
{
    Human,
    ClaudeClient,
    InternalModel,
}

/// <summary>The stored text forms of <see cref="NoteAuthorKind"/>: 'human', 'claude-client', 'internal-model'.</summary>
public static class NoteAuthorKinds
{
    public static string ToText(NoteAuthorKind kind) => kind switch
    {
        NoteAuthorKind.ClaudeClient => "claude-client",
        NoteAuthorKind.InternalModel => "internal-model",
        _ => "human",
    };

    public static bool TryParse(string? text, out NoteAuthorKind kind)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "human":
                kind = NoteAuthorKind.Human;
                return true;
            case "claude-client":
                kind = NoteAuthorKind.ClaudeClient;
                return true;
            case "internal-model":
                kind = NoteAuthorKind.InternalModel;
                return true;
            default:
                kind = NoteAuthorKind.Human;
                return false;
        }
    }

    /// <summary>The stored text, or <see cref="NoteAuthorKind.Human"/> when it is unknown.</summary>
    public static NoteAuthorKind FromStored(string? text) => TryParse(text, out var kind) ? kind : NoteAuthorKind.Human;
}

/// <summary>
/// The writer of a note. It always comes from the server (the tool pipeline or the admin UI), never from tool arguments, so a model cannot claim to be the owner.
/// <paramref name="Id"/> is the client id for a model, the admin actor for the owner.
/// </summary>
public sealed record NoteAuthor(NoteAuthorKind Kind, string Id)
{
    public const int MaxIdChars = 128;

    public bool IsHuman => Kind == NoteAuthorKind.Human;

    /// <summary>The label shown next to a note: 'human: admin-ui', 'claude-client: claude-ai'.</summary>
    public string Label => $"{NoteAuthorKinds.ToText(Kind)}: {Id}";

    /// <summary>True when both name the same writer (same kind and the same id, compared exactly).</summary>
    public bool SameAs(NoteAuthor other) => other is not null && Kind == other.Kind && string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <summary>An author with an id that fits the column: trimmed, at most <see cref="MaxIdChars"/> characters, 'unknown' when empty.</summary>
    public static NoteAuthor Of(NoteAuthorKind kind, string? id)
    {
        var clean = (id ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            clean = "unknown";
        }

        return new NoteAuthor(kind, clean.Length > MaxIdChars ? clean[..MaxIdChars] : clean);
    }
}

/// <summary>What a note is: a plain note, why something is so, a trap, a convention, a decision, or how to do something.</summary>
public enum NoteKind
{
    Note,
    Why,
    Gotcha,
    Convention,
    Decision,
    Howto,
}

/// <summary>The stored text forms of <see cref="NoteKind"/>, lower case.</summary>
public static class NoteKinds
{
    public static IReadOnlyList<NoteKind> All { get; } = [NoteKind.Note, NoteKind.Why, NoteKind.Gotcha, NoteKind.Convention, NoteKind.Decision, NoteKind.Howto];

    public static string ToText(NoteKind kind) => kind switch
    {
        NoteKind.Why => "why",
        NoteKind.Gotcha => "gotcha",
        NoteKind.Convention => "convention",
        NoteKind.Decision => "decision",
        NoteKind.Howto => "howto",
        _ => "note",
    };

    /// <summary>Reads a kind in any letter case; 'how-to' and 'how to' are 'howto'. Empty is <see cref="NoteKind.Note"/>.</summary>
    public static bool TryParse(string? text, out NoteKind kind)
    {
        var clean = (text ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in All)
        {
            if (string.Equals(ToText(candidate), clean, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = NoteKind.Note;
        return clean.Length == 0;
    }

    public static NoteKind FromStored(string? text) => TryParse(text, out var kind) ? kind : NoteKind.Note;
}

/// <summary>Where a note belongs inside its workspace.</summary>
public enum NoteScope
{
    /// <summary>The workspace as a whole, optionally about a file or a member (<see cref="Note.About"/>).</summary>
    Workspace,

    /// <summary>A flow as a whole.</summary>
    Flow,

    /// <summary>One step of a flow (<see cref="Note.StepSpec"/>).</summary>
    Step,
}

/// <summary>
/// A note of the owner or of a model, kept in waymark.db instead of a file in the repository. A revision is a new row that <see cref="ReplacesId"/> the old one,
/// which is kept with <see cref="SupersededUtc"/>; a deleted note is retired (<see cref="RetiredUtc"/>), never removed.
/// <paramref name="FlowName"/> is the name of the flow it is attached to now, null for a workspace note or a detached one.
/// </summary>
public sealed record Note(
    long Id,
    long WorkspaceId,
    long? MapId,
    string? FlowName,
    string? StepSpec,
    string? About,
    string? DetachedFrom,
    NoteKind Kind,
    string Body,
    string ContentHash,
    NoteAuthor Author,
    string? CodeHash,
    int? FlowVersion,
    DateTimeOffset? ConfirmedUtc,
    long? ReplacesId,
    DateTimeOffset? SupersededUtc,
    DateTimeOffset? RetiredUtc,
    string? RetiredBy,
    bool Pinned,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Neither replaced by a newer revision nor retired.</summary>
    public bool IsActive => SupersededUtc is null && RetiredUtc is null;

    /// <summary>Its flow was deleted; the note is kept with the flow's name.</summary>
    public bool IsDetached => MapId is null && DetachedFrom is not null;

    public NoteScope Scope => StepSpec is not null ? NoteScope.Step : MapId is not null || DetachedFrom is not null ? NoteScope.Flow : NoteScope.Workspace;
}

public enum NoteWrite
{
    Saved,

    /// <summary>No note with that id in this workspace.</summary>
    NotFound,

    /// <summary>The writer may not change this note (another author's, or a human's note for a model).</summary>
    NotYours,

    /// <summary>The note was revised or retired already; only its newest revision can change.</summary>
    NotActive,
}

/// <summary>The outcome of a change and, when saved, the note as it is now (for a revision: the new row).</summary>
public sealed record NoteWriteResult(NoteWrite Outcome, Note? Note);
