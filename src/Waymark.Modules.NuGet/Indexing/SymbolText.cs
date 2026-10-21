using System.Text.RegularExpressions;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Identifier tokenisation shared by the indexer and the query path; they must agree exactly.</summary>
internal static partial class SymbolText
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

            foreach (Match part in IdentifierParts().Matches(segment))
            {
                var lower = part.Value.ToLowerInvariant();
                if (lower.Length >= 2 && seen.Add(lower))
                {
                    yield return lower;
                }
            }
        }
    }

    private static string Sanitize(string segment)
    {
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
