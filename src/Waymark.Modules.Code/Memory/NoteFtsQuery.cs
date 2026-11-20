using Waymark.Core.Text;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// Builds the FTS5 expression for the note and flow word searches. Only sanitized [a-z0-9] words reach it, each quoted, so nothing typed can become query syntax.
/// Short filler words are dropped; a word matches as a prefix.
/// </summary>
internal static class NoteFtsQuery
{
    public const int MaxWords = 12;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "do", "does", "for", "from", "how", "in", "is", "it", "of", "on", "or", "the", "to", "what", "when", "where", "which", "who", "why", "with",
    };

    /// <summary>The query's words, sanitized, without filler, in order, at most <see cref="MaxWords"/>.</summary>
    public static IReadOnlyList<string> Words(string? query)
    {
        var words = new List<string>();
        foreach (var raw in (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var part in raw.Split(['.', ':', '/', '\\', '-', '_', ',', ';', '(', ')', '\'', '"', '?', '!'], StringSplitOptions.RemoveEmptyEntries))
            {
                var word = IdentifierWords.Sanitize(part);
                if (word.Length == 0 || Filler.Contains(word) || words.Contains(word))
                {
                    continue;
                }

                words.Add(word);
                if (words.Count == MaxWords)
                {
                    return words;
                }
            }
        }

        return words;
    }

    /// <summary>An expression where every word must match (<paramref name="all"/>) or any word may; null when there are no words.</summary>
    public static string? Build(IReadOnlyList<string> words, bool all)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return null;
        }

        return string.Join(all ? " AND " : " OR ", words.Select(word => word.Length >= 2 ? $"\"{word}\"*" : $"\"{word}\""));
    }
}
