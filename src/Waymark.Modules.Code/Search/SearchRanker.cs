using Waymark.Core.Text;

namespace Waymark.Modules.Code.Search;

/// <summary>One symbol that one or both search legs found.</summary>
/// <param name="Key">Identity across legs: workspace, path, line, name and label.</param>
/// <param name="Text">The symbol's own name, signature and doc summary, the fields joined by U+0002 so no phrase runs across two of them.</param>
/// <param name="NameScore">The score of the name leg (1000 exact down to 1 fuzzy); 0 when only the content leg found it.</param>
/// <param name="NameRank">1-based place in the name leg's order; null when the name leg did not find it.</param>
/// <param name="ContentRank">1-based place in the content leg's order; null when the content leg did not find it.</param>
/// <param name="Terms">The indexed body words (an unordered bag), set when the content leg found the symbol.</param>
/// <param name="Context">The owner and the path, joined by U+0002.</param>
internal sealed record SearchCandidate(
    string Key,
    string Name,
    string Label,
    string Workspace,
    string Path,
    int Line,
    string Detail,
    string Text,
    bool IsType,
    double NameScore,
    int? NameRank,
    int? ContentRank,
    string Terms = "",
    string Context = "");

internal sealed record RankedHit(SearchCandidate Candidate, double Score, IReadOnlyList<string> Matched);

/// <summary>
/// Combines the name leg and the content leg. Reciprocal rank fusion merges two orders without comparing their incomparable scores; the result
/// is weighed by how many query words the symbol covers, by what kind of symbol it is, by whether it lives in tests, and by how often it is called.
/// A name that equals the query always comes first. Pure: no I/O, so every rule can be tested alone.
/// </summary>
internal static class SearchRanker
{
    private const double CoverageFloor = 0.35;
    private const double TermWeight = 0.5;
    private const double TestPathWeight = 0.6;
    private const double MaxCallerBoost = 1.25;
    private const double CallerBoostStep = 0.08;
    private const double ExactScore = 1000;
    private const double StrongNameScore = 200;
    private const double ContextWeight = 0.3;
    private const string Separator = "\u0001";

    /// <param name="legacyOrder">True orders by the name score alone (name, path, line break ties), as the first code_search did; the fusion and the weights are skipped.</param>
    public static IReadOnlyList<RankedHit> Rank(
        IReadOnlyCollection<SearchCandidate> candidates,
        SearchQuery query,
        int rrfK,
        IReadOnlyDictionary<string, int> callers,
        bool legacyOrder)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(callers);

        var typeKeys = candidates.Where(c => c.IsType).Select(TypeKey).ToHashSet(StringComparer.Ordinal);
        var queryAsName = IdentifierWords.Sanitize(string.Concat(query.Words));
        var scored = new List<(RankedHit Hit, int Tier, double TierScore)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Label == "constructor" && typeKeys.Contains(TypeKey(candidate)))
            {
                continue;
            }

            var own = QueryMatching.Prepare(candidate.Text + Separator + candidate.Terms);
            var around = QueryMatching.Prepare(candidate.Context);
            var text = QueryMatching.Prepare(candidate.Text + Separator + candidate.Terms + Separator + candidate.Context);
            if (!Passes(candidate, query, text))
            {
                continue;
            }

            // A word of the file name or the owner alone must not make a content-only hit for a lookup by one word.
            if (query.IsIdentifier && candidate.NameScore <= 0 && !query.Words.Any(w => QueryMatching.Matches(w, own)))
            {
                continue;
            }

            var matched = query.Words.Concat(query.Terms).Where(w => QueryMatching.Matches(w, text)).ToList();
            var exact = candidate.NameScore >= ExactScore
                || (!query.IsIdentifier && queryAsName.Length > 0 && IdentifierWords.Sanitize(candidate.Name) == queryAsName);
            if (legacyOrder)
            {
                scored.Add((new RankedHit(candidate, candidate.NameScore, matched), 0, 0));
                continue;
            }

            var score = Fuse(candidate, rrfK)
                * CoverageFactor(query, own, around)
                * KindWeight(candidate.Label)
                * (TestPaths.IsTest(candidate.Path) ? TestPathWeight : 1)
                * CallerBoost(candidate, callers);
            var tier = exact ? 2 : (query.IsIdentifier && candidate.NameScore >= StrongNameScore ? 1 : 0);
            scored.Add((new RankedHit(candidate, score, matched), tier, tier > 0 ? candidate.NameScore : 0));
        }

        var ordered = legacyOrder
            ? scored.OrderByDescending(s => s.Hit.Score)
            : scored.OrderByDescending(s => s.Tier).ThenByDescending(s => s.TierScore).ThenByDescending(s => s.Hit.Score);
        return ordered
            .ThenBy(s => s.Hit.Candidate.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Hit.Candidate.Path, StringComparer.Ordinal)
            .ThenBy(s => s.Hit.Candidate.Line)
            .Select(s => s.Hit)
            .ToList();
    }

    /// <summary>
    /// The fraction of the query words a symbol covers: 1 for a lookup by one name, a caller's extra terms count half, and a word found only in the
    /// owner or the path counts 0.3 (every member of a file would otherwise match a word of its file name).
    /// </summary>
    /// <param name="text">The symbol's own words: name, signature, doc and body words.</param>
    /// <param name="context">The owner and the path; null when unknown.</param>
    public static double Coverage(SearchQuery query, Haystack text, Haystack? context = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IsIdentifier || query.Words.Count == 0)
        {
            return 1;
        }

        double Credit(string word) =>
            QueryMatching.Matches(word, text) ? 1 : (context is { } c && QueryMatching.Matches(word, c) ? ContextWeight : 0);

        var words = query.Words.Sum(Credit);
        var terms = query.Terms.Sum(Credit);
        return Math.Min(1, (words + (TermWeight * terms)) / query.Words.Count);
    }

    /// <summary>Constructors count little against the type they construct, fields and enum members a little less than methods, and types a little more.</summary>
    public static double KindWeight(string label) => label switch
    {
        "class" or "interface" or "struct" or "record" or "recordstruct" or "typealias" => 1.15,
        "constructor" => 0.5,
        "field" or "variable" => 0.85,
        "enummember" => 0.8,
        _ => 1.0
    };

    public static double RrfScore(int? rank, int k) => rank is { } r ? 1.0 / (k + r) : 0;

    private static double Fuse(SearchCandidate candidate, int k) => RrfScore(candidate.NameRank, k) + RrfScore(candidate.ContentRank, k);

    private static double CoverageFactor(SearchQuery query, Haystack text, Haystack context) =>
        CoverageFloor + ((1 - CoverageFloor) * Coverage(query, text, context));

    private static double CallerBoost(SearchCandidate candidate, IReadOnlyDictionary<string, int> callers) =>
        callers.TryGetValue(candidate.Name, out var count) && count > 0
            ? Math.Min(MaxCallerBoost, 1 + (CallerBoostStep * Math.Log10(1 + count)))
            : 1;

    private static bool Passes(SearchCandidate candidate, SearchQuery query, Haystack text)
    {
        if (query.Required.Any(w => !QueryMatching.Matches(w, text)) || query.Excluded.Any(w => QueryMatching.Matches(w, text)))
        {
            return false;
        }

        if (query.Phrases.Count == 0)
        {
            return true;
        }

        // Adjacency is judged on the symbol's own name, signature and doc only: body words are an unordered bag, and the fields are separated.
        var words = QueryMatching.OrderedWords(candidate.Text);
        return query.Phrases.All(phrase => QueryMatching.ContainsPhrase(words, phrase));
    }

    private static string TypeKey(SearchCandidate candidate) => $"{candidate.Workspace}|{candidate.Path}|{candidate.Name}";
}
