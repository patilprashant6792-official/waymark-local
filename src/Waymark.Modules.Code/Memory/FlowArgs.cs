using System.Globalization;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>Reads and checks the arguments of recall and memorise. Pure; every refusal says how to fix the call.</summary>
internal static class FlowArgs
{
    public const int MaxIdsPerGet = 5;
    public const int SummaryPreviewChars = 140;

    public static string CleanName(string? name) =>
        FeatureMapRules.TryNormalizeName(name, out var clean)
            ? clean
            : throw new ArgumentException("name must be 1 to 60 characters: lowercase letters, digits and '-', starting with a letter or digit (for example 'command-gates').");

    public static string? CleanSummary(string? summary)
    {
        var text = summary?.Trim();
        return text is { Length: > FeatureMapRules.MaxSummaryChars }
            ? throw new ArgumentException($"summary is at most {FeatureMapRules.MaxSummaryChars} characters: one or two sentences on what the flow does, from its trigger to its outcome.")
            : text;
    }

    /// <summary>The reason a second flow with the same trigger is kept; empty when none is given.</summary>
    public static string CleanDistinct(string? distinct)
    {
        var text = (distinct ?? string.Empty).Trim();
        return text.Length > FeatureMapRules.MaxDistinctChars || text.Any(char.IsControl)
            ? throw new ArgumentException($"distinct is at most {FeatureMapRules.MaxDistinctChars} characters on one line: how this flow differs from the other one that starts at the same step.")
            : text;
    }

    public static List<long> ParseIds(string? ids)
    {
        var parts = (ids ?? string.Empty).Trim('[', ']').Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<long>();
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            {
                throw new ArgumentException($"ids are numbers from recall(action='list'), separated by commas: '{part}' is not one.");
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count switch
        {
            0 => throw new ArgumentException("ids is required: take the ids from recall(action='list')."),
            > MaxIdsPerGet => throw new ArgumentException($"ids accepts at most {MaxIdsPerGet} per call."),
            _ => result,
        };
    }

    /// <summary>One line of text that fits in a table cell.</summary>
    public static string Cell(string text)
    {
        var flat = text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= SummaryPreviewChars ? flat : flat[..SummaryPreviewChars] + "...";
    }
}
