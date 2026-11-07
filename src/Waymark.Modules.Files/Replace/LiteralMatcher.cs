using System.Buffers;
using System.Text;

namespace Waymark.Modules.Files.Replace;

/// <summary>
/// Finds a literal string in text: ordinal, left to right, without overlap (after a match the search resumes at its end, as string.Replace does).
/// Pure: no file system access.
/// </summary>
internal static class LiteralMatcher
{
    /// <summary>
    /// With <paramref name="wholeWord"/>, a match must not touch a letter, digit or '_' on a side where <paramref name="find"/> itself
    /// starts or ends with one; a side that is punctuation needs no boundary ('-&gt;Order' needs one only after 'r').
    /// </summary>
    public static IReadOnlyList<TextRange> Find(string text, string find, bool caseSensitive, bool wholeWord)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(find);

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needStart = wholeWord && StartsWithWordRune(find);
        var needEnd = wholeWord && EndsWithWordRune(find);
        var matches = new List<TextRange>();
        var index = text.IndexOf(find, 0, comparison);
        while (index >= 0)
        {
            var end = index + find.Length;
            var accepted = (!needStart || !WordRuneBefore(text, index)) && (!needEnd || !WordRuneAfter(text, end));
            if (accepted)
            {
                matches.Add(new TextRange(index, find.Length));
                index = end < text.Length ? text.IndexOf(find, end, comparison) : -1;
            }
            else
            {
                index = index + 1 < text.Length ? text.IndexOf(find, index + 1, comparison) : -1;
            }
        }

        return matches;
    }

    private static bool IsWordRune(Rune rune) => rune.Value == '_' || Rune.IsLetterOrDigit(rune);

    private static bool StartsWithWordRune(string value) =>
        Rune.DecodeFromUtf16(value, out var rune, out _) == OperationStatus.Done && IsWordRune(rune);

    private static bool EndsWithWordRune(string value) =>
        Rune.DecodeLastFromUtf16(value, out var rune, out _) == OperationStatus.Done && IsWordRune(rune);

    private static bool WordRuneBefore(string text, int index) =>
        index > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var rune, out _) == OperationStatus.Done && IsWordRune(rune);

    private static bool WordRuneAfter(string text, int end) =>
        end < text.Length && Rune.DecodeFromUtf16(text.AsSpan(end), out var rune, out _) == OperationStatus.Done && IsWordRune(rune);
}
