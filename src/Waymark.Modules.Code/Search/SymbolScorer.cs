namespace Waymark.Modules.Code.Search;

/// <summary>
/// Visual Studio style ranking of a name against a query, ported from the old code search:
/// exact 1000, prefix 500, camel-case initials 300, word start 200, all words contained 150, substring 100,
/// characters in order 1 to 50. Zero means no match.
/// </summary>
internal static class SymbolScorer
{
    private const double ExactScore = 1000;
    private const double PrefixScore = 500;
    private const double CamelCaseScore = 300;
    private const double WordStartScore = 200;
    private const double AllWordsScore = 150;
    private const double SubstringScore = 100;
    private const int MaxFuzzyScore = 50;
    private const int FuzzyGapPenalty = 5;

    public static double Score(string name, IReadOnlyList<string> tokens, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(name) || tokens.Count == 0)
        {
            return 0;
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (tokens.Count > 1)
        {
            return tokens.All(token => name.Contains(token, comparison)) ? AllWordsScore : 0;
        }

        var query = tokens[0];
        if (name.Equals(query, comparison))
        {
            return ExactScore;
        }

        if (name.StartsWith(query, comparison))
        {
            return PrefixScore;
        }

        if (UpperCaseInitials(name).StartsWith(query, comparison))
        {
            return CamelCaseScore;
        }

        if (StartsAnyWord(name, query, comparison))
        {
            return WordStartScore;
        }

        if (name.Contains(query, comparison))
        {
            return SubstringScore;
        }

        return FuzzyScore(name, query, comparison);
    }

    private static string UpperCaseInitials(string name) => string.Concat(name.Where(char.IsUpper));

    // A word starts at the first character, at every upper-case letter and after an underscore.
    private static bool StartsAnyWord(string name, string query, StringComparison comparison)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var isStart = i == 0 || char.IsUpper(name[i]) || name[i - 1] == '_';
            if (isStart && name.AsSpan(i).StartsWith(query, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static double FuzzyScore(string name, string query, StringComparison comparison)
    {
        var matches = 0;
        var totalGap = 0;
        var lastMatch = -1;
        var queryIndex = 0;
        for (var i = 0; i < name.Length && queryIndex < query.Length; i++)
        {
            if (name.AsSpan(i, 1).Equals(query.AsSpan(queryIndex, 1), comparison))
            {
                if (lastMatch >= 0)
                {
                    totalGap += i - lastMatch - 1;
                }

                lastMatch = i;
                matches++;
                queryIndex++;
            }
        }

        if (matches != query.Length)
        {
            return 0;
        }

        var averageGap = matches > 1 ? (double)totalGap / (matches - 1) : 0;
        return Math.Max(1, MaxFuzzyScore - (int)(averageGap * FuzzyGapPenalty));
    }
}
