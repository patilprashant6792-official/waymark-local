using Waymark.Core.Text;

namespace Waymark.Modules.Code.Search;

/// <summary>Which parts of the index code_search looks in.</summary>
internal enum MatchIn
{
    /// <summary>Names, split name words, doc comments, paths and the words of member bodies.</summary>
    All,

    /// <summary>Symbol names only: the behaviour of the first code_search.</summary>
    Names,

    /// <summary>Doc comments only.</summary>
    Docs,

    /// <summary>The words of member bodies only (identifiers, comments and string words).</summary>
    Code
}

internal static class MatchInParser
{
    public static bool TryParse(string? text, out MatchIn mode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            mode = MatchIn.All;
            return true;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }
}

/// <summary>
/// A parsed code_search query. Syntax: plain words (all help, none is required), "a phrase" (words next to each other), +word (must match),
/// -word (must not match). <see cref="Terms"/> are extra words the caller supplies (synonyms, other spellings) that widen the search.
/// </summary>
/// <param name="Display">The query as typed, whitespace collapsed.</param>
/// <param name="Words">The words searched for, in the spelling typed, without the filler words of a sentence ('the', 'how'). Includes required words and the words of phrases.</param>
/// <param name="Required">The words that must match.</param>
/// <param name="Excluded">The words that must not match.</param>
/// <param name="Phrases">Each phrase as its lower-case words.</param>
/// <param name="Terms">Extra words from the caller.</param>
internal sealed record SearchQuery(
    string Display,
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<IReadOnlyList<string>> Phrases,
    IReadOnlyList<string> Terms)
{
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "to", "in", "on", "for", "and", "or", "is", "are", "does", "do", "how", "what", "where", "which", "when",
        "with", "from", "by", "at", "as", "it", "this", "that", "be", "i", "we", "my"
    };

    /// <summary>One plain word and nothing else: a name lookup, ranked exactly like the first code_search.</summary>
    public bool IsIdentifier => Words.Count == 1 && Required.Count == 0 && Excluded.Count == 0 && Phrases.Count == 0 && Terms.Count == 0;

    /// <summary>The same query with each phrase turned into required words, for searches over body words, which have no order.</summary>
    public SearchQuery PhrasesAsRequired() =>
        Phrases.Count == 0
            ? this
            : this with { Phrases = [], Required = Required.Concat(Phrases.SelectMany(p => p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
    /// <exception cref="ArgumentException">The query is empty or too long, or it has more words and terms than allowed.</exception>
    public static SearchQuery Parse(string? query, IReadOnlyList<string>? terms, int maxLength, int maxWords)
    {
        var collapsed = string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            throw new ArgumentException("query is required.");
        }

        if (collapsed.Length > maxLength)
        {
            throw new ArgumentException($"query is longer than {maxLength} characters.");
        }

        var plain = new List<string>();
        var required = new List<string>();
        var excluded = new List<string>();
        var phrases = new List<IReadOnlyList<string>>();
        var i = 0;
        while (i < collapsed.Length)
        {
            if (char.IsWhiteSpace(collapsed[i]))
            {
                i++;
                continue;
            }

            var sign = '\0';
            if ((collapsed[i] is '+' or '-') && i + 1 < collapsed.Length && !char.IsWhiteSpace(collapsed[i + 1]))
            {
                sign = collapsed[i];
                i++;
            }

            if (collapsed[i] == '"')
            {
                var close = collapsed.IndexOf('"', i + 1);
                var body = close < 0 ? collapsed[(i + 1)..] : collapsed[(i + 1)..close];
                i = close < 0 ? collapsed.Length : close + 1;
                var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(HasLetterOrDigit).ToList();
                if (sign == '-')
                {
                    excluded.AddRange(words);
                }
                else if (words.Count == 1)
                {
                    AddPlain(words[0], sign, plain, required);
                }
                else if (words.Count > 1)
                {
                    phrases.Add(words.Select(w => IdentifierWords.Sanitize(w)).Where(w => w.Length > 0).ToList());
                    plain.AddRange(words);
                }

                continue;
            }

            var start = i;
            while (i < collapsed.Length && !char.IsWhiteSpace(collapsed[i]))
            {
                i++;
            }

            var word = collapsed[start..i].Trim('"');
            if (!HasLetterOrDigit(word))
            {
                continue;
            }

            if (sign == '-')
            {
                excluded.Add(word);
            }
            else
            {
                AddPlain(word, sign, plain, required);
            }
        }

        var distinct = plain.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var content = distinct.Where(w => required.Contains(w, StringComparer.OrdinalIgnoreCase) || !FillerWords.Contains(w)).ToList();
        if (content.Count == 0)
        {
            // One filler word alone ('is', 'it') may still be a name; several are a sentence with nothing to look for.
            if (distinct.Count >= 2)
            {
                throw new ArgumentException("query has only filler words (a, the, how, does, ...); add the words that name what you are looking for.");
            }

            content = distinct;
        }

        if (content.Count == 0)
        {
            throw new ArgumentException("query needs at least one word to search for; words that are only +, - or quotes are ignored, and a query of only -words has nothing to find.");
        }

        var extra = new List<string>();
        foreach (var entry in terms ?? [])
        {
            extra.AddRange((entry ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(HasLetterOrDigit));
        }

        var extraTerms = extra.Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !content.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (content.Count + extraTerms.Count > maxWords)
        {
            throw new ArgumentException($"query and terms together may have at most {maxWords} words.");
        }

        return new SearchQuery(
            collapsed,
            content,
            required.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            excluded.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            phrases,
            extraTerms);
    }

    private static void AddPlain(string word, char sign, List<string> plain, List<string> required)
    {
        plain.Add(word);
        if (sign == '+')
        {
            required.Add(word);
        }
    }

    private static bool HasLetterOrDigit(string word) => word.Any(char.IsLetterOrDigit);
}
