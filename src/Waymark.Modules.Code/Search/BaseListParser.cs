namespace Waymark.Modules.Code.Search;

/// <summary>Reads the base list text kept in the index into simple type names.</summary>
internal static class BaseListParser
{
    /// <summary>
    /// Splits at top-level commas only, so <c>IRepository&lt;A, B&gt;</c> stays one entry, and reduces each entry to its
    /// simple name (no namespace, no type arguments).
    /// </summary>
    public static IReadOnlyList<string> SimpleNames(string? baseList)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(baseList))
        {
            return names;
        }

        var depth = 0;
        var start = 0;
        for (var i = 0; i <= baseList.Length; i++)
        {
            if (i < baseList.Length)
            {
                if (baseList[i] == '<')
                {
                    depth++;
                }
                else if (baseList[i] == '>')
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (baseList[i] != ',' || depth > 0)
                {
                    continue;
                }
            }

            var entry = baseList[start..i].Trim();
            start = i + 1;
            var generic = entry.IndexOf('<', StringComparison.Ordinal);
            if (generic >= 0)
            {
                entry = entry[..generic];
            }

            entry = entry[(entry.LastIndexOf('.') + 1)..].Trim();
            if (entry.Length > 0)
            {
                names.Add(entry);
            }
        }

        return names;
    }
}
