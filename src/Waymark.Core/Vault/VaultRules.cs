using System.Text.RegularExpressions;

namespace Waymark.Core.Vault;

/// <summary>What a secret name and value may look like, and how output is cleaned of secret values.</summary>
public static partial class VaultRules
{
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 8192;
    public const int HintLength = 4;
    public const int MinScrubLength = 6;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool TryNormalize(string? name, string? value, out string normalizedName, out string? error)
    {
        normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length is 0 or > MaxNameLength || !NamePattern().IsMatch(normalizedName))
        {
            error = "Use letters, digits and underscores, starting with a letter (up to 64 characters), for example GITHUB_TOKEN.";
            return false;
        }

        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            error = "The value must not be empty and can be at most 8,192 characters.";
            return false;
        }

        error = null;
        return true;
    }

    public static string HintOf(string value) =>
        value.Length > HintLength * 2 ? value[^HintLength..] : string.Empty;

    /// <summary>
    /// Replaces every secret value found in the text with [secret:NAME]. Longer values go first so a value that
    /// contains another is replaced whole. Values shorter than six characters are not scrubbed: they would match too much.
    /// </summary>
    public static string Scrub(string text, IEnumerable<KeyValuePair<string, string>> secretsByName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(secretsByName);
        var result = text;
        foreach (var secret in secretsByName.Where(s => s.Value.Length >= MinScrubLength).OrderByDescending(s => s.Value.Length))
        {
            result = result.Replace(secret.Value, "[secret:" + secret.Key + "]", StringComparison.Ordinal);
        }

        return result;
    }
}
