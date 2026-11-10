using Waymark.Core.Text;

namespace Waymark.Modules.Code.Search;

/// <summary>
/// Builds the FTS5 expressions for the full-text index. Only [a-z0-9] words and spaces ever reach the expression, each inside double quotes,
/// so nothing a user types can become query syntax.
/// </summary>
internal static class FtsQuery
{
    public const string AllColumns = "{name full_name tokens doc path terms}";
    public const string DocColumns = "{doc}";
    public const string CodeColumns = "{terms}";

    public static string ColumnsFor(MatchIn mode) => mode switch
    {
        MatchIn.Docs => DocColumns,
        MatchIn.Code => CodeColumns,
        _ => AllColumns
    };

    /// <summary>
    /// One group per word, joined by AND (a hit must have every word) or OR (any word). A word matches as a prefix of an indexed word, and a
    /// camel-case word also matches its parts side by side ('RefreshToken' finds 'refresh token'). Null when no word has a letter or digit.
    /// </summary>
    public static string? Build(IEnumerable<string> words, string columns, bool all)
    {
        var groups = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            var group = Group(word);
            if (group is not null && seen.Add(group))
            {
                groups.Add(group);
            }
        }

        return groups.Count == 0 ? null : $"{columns} : ({string.Join(all ? " AND " : " OR ", groups)})";
    }

    private static string? Group(string word)
    {
        var whole = IdentifierWords.Sanitize(word);
        if (whole.Length == 0)
        {
            return null;
        }

        var alternatives = new List<string> { whole.Length >= 2 ? $"\"{whole}\"*" : $"\"{whole}\"" };
        var parts = IdentifierWords.Parts(word).ToList();
        if (parts.Count > 1)
        {
            alternatives.Add($"\"{string.Join(' ', parts)}\"");
        }

        return alternatives.Count == 1 ? alternatives[0] : "(" + string.Join(" OR ", alternatives) + ")";
    }
}
