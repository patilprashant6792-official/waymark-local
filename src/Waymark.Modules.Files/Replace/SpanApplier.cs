using System.Text;

namespace Waymark.Modules.Files.Replace;

/// <summary>Writes one replacement into every range of a text. Every character outside the ranges stays as it was. Pure.</summary>
internal static class SpanApplier
{
    /// <exception cref="ArgumentException">The ranges are not in order, overlap, or fall outside the text.</exception>
    public static string Apply(string text, IReadOnlyList<TextRange> ranges, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(replacement);
        if (ranges.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + (ranges.Count * Math.Max(0, replacement.Length - ranges[0].Length)));
        var position = 0;
        foreach (var range in ranges)
        {
            if (range.Length <= 0 || range.Start < position || range.End > text.Length)
            {
                throw new ArgumentException("The places to replace are out of order, overlap or lie outside the text.", nameof(ranges));
            }

            builder.Append(text, position, range.Start - position).Append(replacement);
            position = range.End;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }
}
