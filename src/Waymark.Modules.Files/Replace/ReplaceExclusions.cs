using System.Globalization;

namespace Waymark.Modules.Files.Replace;

/// <summary>A match the agent drops from a plan: a whole file, or every match on one line of it.</summary>
internal sealed record Exclusion(string Path, int? Line);

/// <summary>Parses and applies excludeJson entries such as "docs/a.md" or "src/A.cs#L12". Pure.</summary>
internal static class ReplaceExclusions
{
    private const string LineMarker = "#L";

    /// <exception cref="ArgumentException">An entry is empty or its line number is not a positive whole number.</exception>
    public static IReadOnlyList<Exclusion> Parse(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        var parsed = new List<Exclusion>(entries.Count);
        foreach (var entry in entries)
        {
            var value = entry?.Trim().Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("excludeJson holds an empty entry.");
            }

            var marker = value.LastIndexOf(LineMarker, StringComparison.Ordinal);
            if (marker <= 0)
            {
                parsed.Add(new Exclusion(value, null));
                continue;
            }

            var number = value[(marker + LineMarker.Length)..];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1)
            {
                throw new ArgumentException($"excludeJson entry '{value}': the part after #L must be a line number.");
            }

            parsed.Add(new Exclusion(value[..marker], line));
        }

        return parsed;
    }

    /// <summary>The files and places left after the exclusions, and how many places were dropped.</summary>
    /// <exception cref="ArgumentException">An exclusion names a file that is not in the plan, or a line of it with no match.</exception>
    public static (IReadOnlyList<FilePlan> Kept, int Excluded) Apply(IReadOnlyList<FilePlan> files, IReadOnlyList<Exclusion> exclusions)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (exclusions.Count == 0)
        {
            return (files, 0);
        }

        var comparer = Waymark.Core.Platform.PathComparisons.Comparer;
        var byPath = files.ToDictionary(file => file.RelativePath, comparer);
        var wholeFiles = new HashSet<string>(comparer);
        var lines = new Dictionary<string, HashSet<int>>(comparer);
        foreach (var exclusion in exclusions)
        {
            if (!byPath.TryGetValue(exclusion.Path, out var file))
            {
                throw new ArgumentException($"excludeJson names '{exclusion.Path}', which is not in the plan.");
            }

            if (exclusion.Line is not { } line)
            {
                wholeFiles.Add(file.RelativePath);
                continue;
            }

            if (!file.Spans.Any(span => span.Line == line))
            {
                throw new ArgumentException($"excludeJson names line {line} of '{file.RelativePath}', which has no match in the plan.");
            }

            if (!lines.TryGetValue(file.RelativePath, out var set))
            {
                set = [];
                lines[file.RelativePath] = set;
            }

            set.Add(line);
        }

        var kept = new List<FilePlan>(files.Count);
        var excluded = 0;
        foreach (var file in files)
        {
            if (wholeFiles.Contains(file.RelativePath))
            {
                excluded += file.Spans.Count;
                continue;
            }

            if (!lines.TryGetValue(file.RelativePath, out var dropped))
            {
                kept.Add(file);
                continue;
            }

            var spans = file.Spans.Where(span => !dropped.Contains(span.Line)).ToList();
            excluded += file.Spans.Count - spans.Count;
            if (spans.Count > 0)
            {
                kept.Add(file with { Spans = spans });
            }
        }

        return (kept, excluded);
    }
}
