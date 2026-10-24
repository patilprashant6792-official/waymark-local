using System.Globalization;

namespace Waymark.Modules.Files;

/// <summary>Literal replacements and indentation shifts on copied text. Text uses '\n' line breaks.</summary>
internal static class TextTransforms
{
    private const int MaxIndentShift = 64;
    private const int ShownCharacters = 60;

    /// <summary>Applies the rules in order; each rule sees the text left by the previous one. A rule whose match count differs from the expected count fails the copy.</summary>
    /// <exception cref="PatchValidationException">A rule is incomplete or did not match as expected.</exception>
    public static string ApplyReplacements(string text, IReadOnlyList<ReplaceRule> rules, int maxRules, List<string> notes)
    {
        if (rules.Count > maxRules)
        {
            throw new PatchValidationException($"At most {maxRules} replace rules can be used for one copy.");
        }

        var current = text;
        var total = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] ?? throw new PatchValidationException($"replace #{i + 1} is empty.");
            var number = i + 1;
            if (string.IsNullOrEmpty(rule.Find))
            {
                throw new PatchValidationException($"replace #{number}: find must not be empty.");
            }

            if (rule.With is null)
            {
                throw new PatchValidationException($"replace #{number}: with is required (use an empty string to delete the text).");
            }

            if (rule.Count is < 1)
            {
                throw new PatchValidationException($"replace #{number}: count must be at least 1 (or leave it out).");
            }

            var find = Normalize(rule.Find);
            var found = CountOccurrences(current, find);
            var matches = rule.Count is { } expected ? found == expected : found > 0;
            if (!matches)
            {
                var wanted = rule.Count is { } n ? n.ToString(CultureInfo.InvariantCulture) : "at least 1";
                throw new PatchValidationException($"replace #{number}: found {found} match(es) of '{Shorten(find)}', expected {wanted}. Nothing was copied.");
            }

            current = current.Replace(find, Normalize(rule.With), StringComparison.Ordinal);
            total += found;
        }

        notes.Add(string.Create(CultureInfo.InvariantCulture, $"{total} replacement(s)"));
        return current;
    }

    /// <summary>A negative shift removes up to that many leading spaces or tabs from every line; a positive shift adds spaces to every non-blank line.</summary>
    /// <exception cref="PatchValidationException">The shift is larger than 64.</exception>
    public static string ShiftIndent(string text, int indent)
    {
        if (indent == 0)
        {
            return text;
        }

        if (Math.Abs(indent) > MaxIndentShift)
        {
            throw new PatchValidationException($"indent must be between -{MaxIndentShift} and {MaxIndentShift}.");
        }

        var lines = text.Split('\n');
        var padding = indent > 0 ? new string(' ', indent) : string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = indent > 0
                ? (string.IsNullOrWhiteSpace(lines[i]) ? lines[i] : padding + lines[i])
                : RemoveLeadingWhitespace(lines[i], -indent);
        }

        return string.Join('\n', lines);
    }

    private static string RemoveLeadingWhitespace(string line, int maxCount)
    {
        var count = 0;
        while (count < maxCount && count < line.Length && line[count] is ' ' or '\t')
        {
            count++;
        }

        return line[count..];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Shorten(string value)
    {
        var line = value.Replace('\n', ' ');
        return line.Length <= ShownCharacters ? line : line[..ShownCharacters] + "...";
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
