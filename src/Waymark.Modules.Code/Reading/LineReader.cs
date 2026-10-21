using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>The three reading modes of code_read, all printing lines as "   12 | text".</summary>
internal static class LineReader
{
    public static void AppendAll(OutputBudget output, LoadedText source, string path)
    {
        if (source.LineCount == 0)
        {
            output.TryAppendLine($"// {path} is empty (0 lines)");
            return;
        }

        output.TryAppendLine($"// {path} - {source.LineCount} lines");
        AppendLines(output, source, 1, source.LineCount);
    }

    public static void AppendRange(OutputBudget output, LoadedText source, string path, int start, int end)
    {
        if (start < 1 || end < 1)
        {
            throw new ArgumentException("'startLine' and 'endLine' must be >= 1.");
        }

        if (start > end)
        {
            throw new ArgumentException($"'startLine' ({start}) must be <= 'endLine' ({end}).");
        }

        if (start > source.LineCount)
        {
            throw new ArgumentException($"'startLine' ({start}) exceeds the file length ({source.LineCount} lines).");
        }

        var last = Math.Min(end, source.LineCount);
        output.TryAppendLine($"// Lines {start}-{last} of {source.LineCount} - {path}");
        AppendLines(output, source, start, last);
    }

    public static void AppendMatches(OutputBudget output, LoadedText source, string path, string query, int maxMatches)
    {
        var lines = new List<string>();
        var total = 0;
        for (var number = 1; number <= source.LineCount; number++)
        {
            var text = source.Line(number);
            if (!text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total++;
            if (lines.Count < maxMatches)
            {
                lines.Add($"{number,4} | {text}");
            }
        }

        if (total == 0)
        {
            output.TryAppendLine($"No lines found matching '{query}' in {path} ({source.LineCount} lines searched).");
            return;
        }

        output.TryAppendLine($"// {total} match(es) for '{query}' in {path}");
        foreach (var line in lines)
        {
            output.TryAppendLine(line);
        }

        if (total > lines.Count)
        {
            output.TryAppendLine($"// Showing the first {lines.Count} matches. Use a more specific query.");
        }
    }

    private static void AppendLines(OutputBudget output, LoadedText source, int first, int last)
    {
        for (var number = first; number <= last; number++)
        {
            if (!output.TryAppendLine($"{number,4} | {source.Line(number)}"))
            {
                return;
            }
        }
    }
}
