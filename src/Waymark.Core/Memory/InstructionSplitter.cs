using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Core.Memory;

/// <summary>One note proposed from a piece of an instruction file: its text (with the heading it was under in front) and a guessed kind.</summary>
public sealed record SplitNote(string Body, NoteKind Kind);

/// <summary>
/// Splits the text of an instruction file (CLAUDE.md, AGENTS.md) into proposed notes: every list item and every paragraph is one, with the nearest heading
/// in front as context; code blocks stay with the text above them; a piece over the note cap is cut at sentence ends. The kind is a guess from a few words
/// (never, always, because, decided, how to). Pure, so the owner reviews everything before anything is saved.
/// </summary>
public static partial class InstructionSplitter
{
    public const int MaxNotes = 200;

    public static IReadOnlyList<SplitNote> Split(string? text)
    {
        var pieces = new List<string>();
        var heading = string.Empty;
        var current = new StringBuilder();
        var inCode = false;

        void Flush()
        {
            var piece = current.ToString().Trim();
            current.Clear();
            if (piece.Length > 0)
            {
                pieces.Add(heading.Length == 0 ? piece : $"{heading}: {piece}");
            }
        }

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                current.Append(' ').Append(line.Trim());
                continue;
            }

            if (inCode)
            {
                current.Append(' ').Append(line.Trim());
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }

            var headingMatch = Heading().Match(trimmed);
            if (headingMatch.Success)
            {
                Flush();
                heading = headingMatch.Groups[1].Value.Trim();
                continue;
            }

            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                // A list item that is indented further than two spaces continues the item above it.
                if (bullet.Groups[1].Value.Length >= 2 && current.Length > 0)
                {
                    current.Append(' ').Append(bullet.Groups[2].Value.Trim());
                    continue;
                }

                Flush();
                current.Append(bullet.Groups[2].Value.Trim());
                continue;
            }

            current.Append(current.Length == 0 ? string.Empty : " ").Append(trimmed);
        }

        Flush();
        var notes = new List<SplitNote>();
        foreach (var piece in pieces)
        {
            foreach (var part in Fit(Collapse(piece)))
            {
                notes.Add(new SplitNote(part, Guess(part)));
                if (notes.Count == MaxNotes)
                {
                    return notes;
                }
            }
        }

        return notes;
    }

    /// <summary>The kind a note most likely is, from its words.</summary>
    public static NoteKind Guess(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.ToLowerInvariant();
        if (Contains(text, "never", "don't", "do not", "avoid", "careful", "beware", "gotcha", "watch out", "pitfall", "trap"))
        {
            return NoteKind.Gotcha;
        }

        if (Contains(text, "we decided", "decided", "decision", "we chose", "chosen"))
        {
            return NoteKind.Decision;
        }

        if (Contains(text, "because", "the reason", "so that", "why "))
        {
            return NoteKind.Why;
        }

        if (Contains(text, "how to", "to run", "to build", "to test", "run `", "steps:"))
        {
            return NoteKind.Howto;
        }

        return Contains(text, "always", "must", "prefer", "use ", "naming", "convention", "should") ? NoteKind.Convention : NoteKind.Note;
    }

    private static bool Contains(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.Ordinal));

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // A piece over the cap is cut at sentence ends; a single sentence over the cap is cut hard.
    private static IEnumerable<string> Fit(string text)
    {
        if (text.Length <= NoteRules.MaxBodyChars)
        {
            yield return text;
            yield break;
        }

        var part = new StringBuilder();
        foreach (var sentence in Sentence().Split(text))
        {
            if (part.Length > 0 && part.Length + 1 + sentence.Length > NoteRules.MaxBodyChars)
            {
                yield return part.ToString();
                part.Clear();
            }

            var rest = sentence;
            while (rest.Length > NoteRules.MaxBodyChars)
            {
                yield return rest[..NoteRules.MaxBodyChars];
                rest = rest[NoteRules.MaxBodyChars..];
            }

            part.Append(part.Length == 0 ? string.Empty : " ").Append(rest);
        }

        if (part.Length > 0)
        {
            yield return part.ToString();
        }
    }

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)(?:[-*+]|\d{1,3}[.)])\s+(.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Sentence();
}
