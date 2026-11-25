using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Waymark.Core.Memory;

/// <summary>The limits and name rules of a feature map (a flow). Pure. Shared by memory_save (Claude) and by the import of a bundle (the owner), so both accept the same flows.</summary>
public static class FeatureMapRules
{
    /// <summary>
    /// A safety bound on the steps of one flow, against runaway or hostile input. It is not a design limit: a flow has as many steps as its path,
    /// and memory_recall returns a long flow in parts.
    /// </summary>
    public const int MaxStepsPerFlow = 500;
    public const int MaxSummaryChars = 300;

    /// <summary>Levels below the trigger.</summary>
    public const int MaxDepth = 8;

    /// <summary>The longest reason for keeping two flows with the same trigger.</summary>
    public const int MaxDistinctChars = 120;

    // 1 to 60 characters, matching the name rule told to the agent.
    private static readonly Regex NameText = new("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>True when the text is a feature name after trimming, lower-casing and turning spaces and underscores into '-'; <paramref name="name"/> is then what gets stored.</summary>
    public static bool TryNormalizeName(string? text, [NotNullWhen(true)] out string? name)
    {
        var candidate = (text ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        name = NameText.IsMatch(candidate) ? candidate : null;
        return name is not null;
    }
}
