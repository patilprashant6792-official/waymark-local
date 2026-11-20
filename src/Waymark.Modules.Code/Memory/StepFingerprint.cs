using System.Security.Cryptography;
using System.Text;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The fingerprint of the code a note is anchored to: the lines of the step (every overload of a method, in file order), each without trailing white space,
/// joined with '\n', hashed with SHA-256 and written 'v1:&lt;hex&gt;'. Line endings and trailing spaces do not count, so a checkout on another OS does not make a note stale.
/// Pure: the code is never stored or returned, only the hash.
/// </summary>
internal static class StepFingerprint
{
    public const string Prefix = "v1:";

    /// <param name="lines">The file's lines without line breaks; line 1 is index 0.</param>
    /// <param name="ranges">The lines that make up the step, 1-based and inclusive; clamped to the file, sorted by start.</param>
    public static string Compute(IReadOnlyList<string> lines, IReadOnlyList<LineSpan> ranges)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(ranges);
        var text = new StringBuilder();
        var first = true;
        foreach (var range in ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            var start = Math.Max(1, range.Start);
            var end = Math.Min(lines.Count, range.End);
            for (var number = start; number <= end; number++)
            {
                if (!first)
                {
                    text.Append('\n');
                }

                text.Append(lines[number - 1].TrimEnd());
                first = false;
            }
        }

        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The fingerprint of a step that was found in the code, or null when it is not on this branch or its file could not be read.</summary>
    public static string? Of(StepView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!view.Found || view.File?.Text is not { } text)
        {
            return null;
        }

        var lines = new string[text.LineCount];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = text.Line(i + 1);
        }

        return Compute(lines, view.AllRanges);
    }
}
