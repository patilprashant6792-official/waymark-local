using System.Text.Json;
using Waymark.Logging;

namespace Waymark.Host.Mcp;

/// <summary>
/// The purpose argument of a command tool call: the one sentence the AI gives for why it runs the command. It is shown live and kept in the
/// audit trail, so it is made safe first: one line, single spaces, credentials removed and cut to <see cref="MaxChars"/>.
/// </summary>
public static class ToolPurpose
{
    public const string ArgumentName = "purpose";
    public const int MaxChars = 300;

    /// <summary>The cleaned purpose, or null when the call has none.</summary>
    public static string? From(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(ArgumentName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var words = (value.GetString() ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        var clean = LogScrubber.Scrub(string.Join(' ', words));
        return clean.Length <= MaxChars ? clean : clean[..MaxChars] + "...";
    }
}
