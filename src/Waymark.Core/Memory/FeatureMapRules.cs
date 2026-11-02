using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Waymark.Core.Memory;

/// <summary>The limits and name rules of a feature map. Pure. Shared by memorise (Claude) and by the import of a bundle (the owner), so both accept the same maps.</summary>
public static class FeatureMapRules
{
    public const int MaxComponentsPerMap = 30;
    public const int MaxSummaryChars = 300;

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
