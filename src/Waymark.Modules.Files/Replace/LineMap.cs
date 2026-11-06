using System.Text;

namespace Waymark.Modules.Files.Replace;

/// <summary>
/// Line numbers of a text without changing it. A line ends at '\n'; a '\r' just before it belongs to the line break, so CRLF, LF and a mix
/// all give the line numbers an editor shows. Pure.
/// </summary>
internal sealed class LineMap
{
    private readonly string _text;
    private readonly List<int> _starts = [0];

    public LineMap(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i + 1 < text.Length)
            {
                _starts.Add(i + 1);
            }
        }
    }

    /// <summary>Lines in the text; a final line break does not start another line, and an empty text has none.</summary>
    public int LineCount => _text.Length == 0 ? 0 : _starts.Count;

    /// <summary>The 1-based line the character at <paramref name="index"/> is on.</summary>
    public int LineOf(int index)
    {
        var found = _starts.BinarySearch(index);
        return found >= 0 ? found + 1 : ~found;
    }

    /// <summary>Index of the first character of a 1-based line.</summary>
    public int StartOf(int line) => _starts[line - 1];

    /// <summary>The text of a 1-based line without its line break.</summary>
    public string LineText(int line)
    {
        var start = _starts[line - 1];
        var end = line < _starts.Count ? _starts[line] - 1 : _text.Length;
        if (end > start && _text[end - 1] == '\n')
        {
            end--;
        }

        if (end > start && _text[end - 1] == '\r')
        {
            end--;
        }

        return _text[start..end];
    }

    /// <summary>
    /// The line with every range marked as ⟦match⟧, leading whitespace removed, cut to about <paramref name="maxChars"/> characters
    /// around the first match. Ranges are relative to the start of the line.
    /// </summary>
    public static string Excerpt(string line, IReadOnlyList<TextRange> ranges, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(ranges);
        var builder = new StringBuilder(line.Length + (ranges.Count * 2));
        var position = 0;
        foreach (var range in ranges)
        {
            builder.Append(line, position, range.Start - position).Append('⟦').Append(line, range.Start, range.Length).Append('⟧');
            position = range.End;
        }

        builder.Append(line, position, line.Length - position);
        var marked = builder.ToString().TrimStart();
        if (marked.Length <= maxChars)
        {
            return marked;
        }

        var first = Math.Max(0, marked.IndexOf('⟦', StringComparison.Ordinal) - 40);
        var length = Math.Min(maxChars, marked.Length - first);
        var cut = marked.Substring(first, length);
        return (first > 0 ? "…" : string.Empty) + cut + (first + length < marked.Length ? "…" : string.Empty);
    }
}
