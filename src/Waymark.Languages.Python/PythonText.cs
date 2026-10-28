namespace Waymark.Languages.Python;

/// <summary>Turns pieces of Python source into the short single-line texts the index keeps.</summary>
internal static class PythonText
{
    private const string Ellipsis = "...";

    /// <summary>Collapses every run of white space to one space and cuts the text to <paramref name="maxChars"/>.</summary>
    public static string Squash(string text, int maxChars)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= maxChars
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, maxChars - Ellipsis.Length), Ellipsis);
    }

    /// <summary>The text between the quotes of a string literal (prefix letters and triple quotes handled); null when it is not a plain literal.</summary>
    public static string? Unquote(string literal)
    {
        var text = literal.Trim();
        var start = 0;
        while (start < text.Length && char.IsAsciiLetter(text[start]))
        {
            start++;
        }

        if (start >= text.Length || (text[start] != '"' && text[start] != '\''))
        {
            return null;
        }

        var quote = text[start];
        var triple = text.Length >= start + 6 && text[start + 1] == quote && text[start + 2] == quote;
        var width = triple ? 3 : 1;
        var innerStart = start + width;
        var innerEnd = text.Length - width;
        return innerEnd < innerStart ? null : text[innerStart..innerEnd];
    }

    /// <summary>The first non-empty line of a docstring literal; null when there is none.</summary>
    public static string? Summary(string literal, int maxChars)
    {
        var inner = Unquote(literal);
        if (inner is null)
        {
            return null;
        }

        foreach (var line in inner.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return Squash(trimmed, maxChars);
            }
        }

        return null;
    }
}
