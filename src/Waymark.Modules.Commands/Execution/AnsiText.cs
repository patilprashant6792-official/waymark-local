using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Execution;

/// <summary>Turns terminal output into plain text for Claude: colours and cursor codes removed, progress lines that rewrite themselves reduced to their last state.</summary>
public static partial class AnsiText
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]", RegexOptions.None, 200)]
    private static partial Regex EscapePattern();

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return EscapePattern().Replace(text, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>Strip, then keep only the text after the last carriage return of each line, as a terminal would show it.</summary>
    public static string Plain(string text)
    {
        var stripped = Strip(text).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!stripped.Contains('\r', StringComparison.Ordinal))
        {
            return stripped;
        }

        var result = new StringBuilder(stripped.Length);
        foreach (var line in stripped.Split('\n'))
        {
            var cut = line.LastIndexOf('\r');
            var shown = cut < 0 ? line : line[(cut + 1)..];
            if (cut >= 0 && shown.Length == 0)
            {
                shown = line.TrimEnd('\r');
                var previous = shown.LastIndexOf('\r');
                shown = previous < 0 ? shown : shown[(previous + 1)..];
            }

            result.Append(shown).Append('\n');
        }

        if (result.Length > 0)
        {
            result.Length--;
        }

        return result.ToString();
    }
}
