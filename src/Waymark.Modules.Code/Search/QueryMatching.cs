using Waymark.Core.Text;

namespace Waymark.Modules.Code.Search;

/// <summary>The text of a hit, lower-cased, once as it is and once with only letters and digits, so 'refresh_token' and 'RefreshToken' both contain 'refreshtoken'.</summary>
internal readonly record struct Haystack(string Lower, string Compact);

/// <summary>Local checks of which query words a hit's text contains. The full-text index finds candidates; these checks decide coverage, required and excluded words and phrases.</summary>
internal static class QueryMatching
{
    public static Haystack Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Haystack(text.ToLowerInvariant(), IdentifierWords.Sanitize(text));
    }

    /// <summary>A light stemmer that agrees with the index on the common endings: creating, created, creates and create all become 'creat'.</summary>
    public static string Stem(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var w = word;
        if (w.Length >= 7 && w.EndsWith("ing", StringComparison.Ordinal))
        {
            w = w[..^3];
        }
        else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal))
        {
            w = w[..^2];
        }
        else if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal))
        {
            w = w[..^2];
        }
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal))
        {
            w = w[..^1];
        }

        return w.Length > 4 && w.EndsWith('e') ? w[..^1] : w;
    }

    /// <summary>True when the stem of <paramref name="word"/> occurs in the text.</summary>
    public static bool Matches(string word, Haystack text)
    {
        var stem = Stem(IdentifierWords.Sanitize(word));
        return stem.Length > 0 && (text.Lower.Contains(stem, StringComparison.Ordinal) || text.Compact.Contains(stem, StringComparison.Ordinal));
    }

    /// <summary>
    /// The words of a text in order, identifiers split into their parts: "Refresh the HttpClient" gives refresh, the, http, client.
    /// A field separator (U+0001 or U+0002) adds an empty word, so a phrase never runs from one field into the next.
    /// </summary>
    public static List<string> OrderedWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                if (text[i] is '\u0001' or '\u0002')
                {
                    result.Add(string.Empty);
                }

                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            result.AddRange(IdentifierWords.Parts(text[start..i]));
        }

        return result;
    }

    /// <summary>True when the phrase words occur next to each other, each as the start of the text word (so 'refresh token' finds 'refreshes tokens').</summary>
    public static bool ContainsPhrase(IReadOnlyList<string> textWords, IReadOnlyList<string> phrase)
    {
        ArgumentNullException.ThrowIfNull(textWords);
        ArgumentNullException.ThrowIfNull(phrase);
        if (phrase.Count == 0)
        {
            return true;
        }

        var stems = phrase.Select(Stem).ToList();
        for (var start = 0; start + stems.Count <= textWords.Count; start++)
        {
            var all = true;
            for (var k = 0; k < stems.Count && all; k++)
            {
                all = textWords[start + k].StartsWith(stems[k], StringComparison.Ordinal);
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }
}
