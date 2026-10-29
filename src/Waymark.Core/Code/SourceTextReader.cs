using System.Text;

namespace Waymark.Core.Code;

/// <summary>
/// The text rules every tree-sitter based indexer shares, so the lines an indexer counts are the lines the readers show (Roslyn's SourceText splits lines
/// on \r, \n, \r\n, NEL, LS and PS; a parser that counts only \n would be off by one after a lone \r).
/// </summary>
public static class SourceTextReader
{
    private static readonly char[] LineBreaks = ['\r', '\u0085', '\u2028', '\u2029'];

    /// <summary>UTF-8 (with or without a byte order mark) or UTF-16 with a mark. A file with NUL bytes and no UTF-16 mark is binary.</summary>
    public static bool TryDecode(byte[] content, out string text)
    {
        ArgumentNullException.ThrowIfNull(content);
        text = string.Empty;
        var utf16 = content.Length >= 2 && ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF));
        if (!utf16 && Array.IndexOf(content, (byte)0) >= 0)
        {
            return false;
        }

        using var reader = new StreamReader(new MemoryStream(content, writable: false), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        text = reader.ReadToEnd();
        return true;
    }

    /// <summary>Every line break becomes \n.</summary>
    public static string NormalizeLineBreaks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOfAny(LineBreaks) < 0)
        {
            return text;
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u0085', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');
    }

    /// <summary>Lines as an editor shows them: a final line break does not start another line, an empty file has none. The text must be normalised.</summary>
    public static int CountLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return text[^1] == '\n' ? lines - 1 : lines;
    }
}
