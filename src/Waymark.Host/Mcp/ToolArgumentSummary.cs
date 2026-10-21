using System.Globalization;
using System.Text;
using System.Text.Json;
using Waymark.Logging;

namespace Waymark.Host.Mcp;

/// <summary>
/// A short one-line view of what a tool call was asked, for the Live page: short values are shown, long text is cut and says how much
/// was left out, and values with a secret-like name are hidden. The workspace is left out because it has its own column.
/// </summary>
public static class ToolArgumentSummary
{
    private const int MaxValueChars = 60;
    private const int MaxTotalChars = 160;
    private const string WorkspaceArgument = "workspace";

    public static string Build(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var pair in arguments)
        {
            if (string.Equals(pair.Key, WorkspaceArgument, StringComparison.Ordinal))
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(pair.Key).Append('=').Append(Describe(pair.Key, pair.Value));
            if (text.Length > MaxTotalChars)
            {
                text.Length = MaxTotalChars;
                text.Append("...");
                break;
            }
        }

        return text.ToString();
    }

    private static string Describe(string key, JsonElement value)
    {
        if (LogScrubber.IsSensitiveKey(key))
        {
            return LogScrubber.Hidden;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Shorten(value.GetString() ?? string.Empty),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => Shorten(value.GetRawText())
        };
    }

    private static string Shorten(string value)
    {
        var oneLine = LogScrubber.Scrub(value.ReplaceLineEndings(" "));
        return oneLine.Length <= MaxValueChars
            ? oneLine
            : string.Create(CultureInfo.InvariantCulture, $"{oneLine.AsSpan(0, MaxValueChars)}... (+{oneLine.Length - MaxValueChars} chars)");
    }
}
