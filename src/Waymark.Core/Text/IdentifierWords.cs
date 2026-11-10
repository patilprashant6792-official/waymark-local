using System.Text.RegularExpressions;

namespace Waymark.Core.Text;

/// <summary>
/// Splits identifiers into words. One implementation is shared by the code index, the package index and the query path, because the words
/// written at index time and the words searched for must agree exactly.
/// </summary>
public static partial class IdentifierWords
{
    private static readonly char[] Separators = ['.', '_', '`', '<', '>', '[', ']', ',', '(', ')', ' ', '?', '&', '*', ':', '-', '+', '='];

    /// <summary>
    /// Splits "CompleteChatAsync" into completechatasync, complete, chat, async. Every token is [a-z0-9] only and at least two
    /// characters long, so the store can match each one as a quoted prefix without any query syntax leaking in.
    /// </summary>
    public static IEnumerable<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var whole = Sanitize(segment);
            if (whole.Length >= 2 && seen.Add(whole))
            {
                yield return whole;
            }

            foreach (var lower in Parts(segment))
            {
                if (lower.Length >= 2 && seen.Add(lower))
                {
                    yield return lower;
                }
            }
        }
    }

    /// <summary>The camel-case, digit and acronym parts of one identifier, lower-case, in order: "HTTPClient2" gives http, client, 2.</summary>
    public static IEnumerable<string> Parts(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            yield break;
        }

        foreach (Match part in IdentifierParts().Matches(identifier))
        {
            yield return part.Value.ToLowerInvariant();
        }
    }

    /// <summary>Lower-case letters and digits of <paramref name="segment"/>; everything else is dropped.</summary>
    public static string Sanitize(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var buffer = new char[segment.Length];
        var length = 0;
        foreach (var c in segment)
        {
            var lower = char.ToLowerInvariant(c);
            if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
            {
                buffer[length++] = lower;
            }
        }

        return new string(buffer, 0, length);
    }

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z]+|[0-9]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierParts();
}
