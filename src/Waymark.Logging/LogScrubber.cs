using System.Text.RegularExpressions;

namespace Waymark.Logging;

/// <summary>
/// Removes credentials from text before it is kept for display: bearer values, Waymark's own token prefixes,
/// sensitive query or form values (including the pairing secret and authorization code) and JSON secrets.
/// </summary>
public static partial class LogScrubber
{
    public const string Hidden = "[hidden]";

    private static readonly string[] SensitiveKeyEndings =
        ["password", "passwd", "secret", "token", "authorization", "verifier", "apikey", "api_key", "credential"];

    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.None, 100)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(?:sma|smr|smk)_[A-Za-z0-9_\-]{16,}", RegexOptions.None, 100)]
    private static partial Regex WaymarkValuePattern();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9_\-])(?:code|p|state|code_verifier|token|authtoken|access_token|refresh_token|client_secret|password|passwd|pwd|api_key|apikey|secret)=[^&\s""';,]+", RegexOptions.None, 100)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"(?i)(""(?:password|passwd|client_secret|access_token|refresh_token|authorization|api_key|apikey|authtoken|secret|code_verifier)""\s*:\s*"")[^""]*", RegexOptions.None, 100)]
    private static partial Regex JsonPattern();

    /// <summary>True for field names such as password, client_secret, access_token or authorization.</summary>
    public static bool IsSensitiveKey(string key)
    {
        foreach (var ending in SensitiveKeyEndings)
        {
            if (key.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        try
        {
            var result = BearerPattern().Replace(value, "$1" + Hidden);
            result = WaymarkValuePattern().Replace(result, Hidden);
            result = KeyValuePattern().Replace(result, match => string.Concat(match.Value.AsSpan(0, match.Value.IndexOf('=') + 1), Hidden));
            return JsonPattern().Replace(result, "$1" + Hidden);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[could not be checked for secrets]";
        }
    }
}
