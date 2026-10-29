using System.Text;

namespace Waymark.Languages.TypeScript;

/// <summary>Turns pieces of source into the short single-line texts the index keeps.</summary>
internal static class TypeScriptText
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

    /// <summary>The text of a string literal without its quotes; the text itself when it has none.</summary>
    public static string Unquote(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] is '"' or '\'' or '`' && trimmed[^1] == trimmed[0] ? trimmed[1..^1] : trimmed;
    }

    /// <summary>The first descriptive line of a JSDoc block (<c>/** ... */</c>); null for any other comment or when it holds only tags.</summary>
    public static string? JsDocSummary(string comment, int maxChars)
    {
        if (!comment.StartsWith("/**", StringComparison.Ordinal))
        {
            return null;
        }

        var body = comment.Trim();
        body = body.StartsWith("/**", StringComparison.Ordinal) ? body[3..] : body;
        body = body.EndsWith("*/", StringComparison.Ordinal) ? body[..^2] : body;
        var summary = new StringBuilder();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('*').Trim();
            if (line.StartsWith('@'))
            {
                break;
            }

            if (line.Length == 0)
            {
                if (summary.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (summary.Length > 0)
            {
                summary.Append(' ');
            }

            summary.Append(line);
        }

        return summary.Length == 0 ? null : Squash(summary.ToString(), maxChars);
    }
}
