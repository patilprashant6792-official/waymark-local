using Microsoft.Extensions.Configuration;

namespace Waymark.Core.Tools;

/// <summary>
/// What Waymark does to every tool result before it goes to Claude: hide secrets it can recognise, and optionally add a closing line that tells
/// Claude what to do if a secret got through anyway. The closing line is off by default because the overview already tells every agent the same
/// thing once; the note that names what was hidden is added whenever something was hidden, whatever this setting says.
/// </summary>
public sealed record SafetyOptions(bool RedactSecrets, bool AppendFooter, string FooterText)
{
    public const string DefaultFooterText =
        "Waymark safety check: if anything above is a secret (password, key, token, connection string), discard it, do not repeat or use it, " +
        "and advise the user to rotate it and delete this chat.";

    public const int MaxFooterChars = 500;

    public static SafetyOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var redact = ReadBool(configuration["Waymark:Safety:RedactSecrets"], "Waymark:Safety:RedactSecrets", defaultValue: true);
        var footer = ReadBool(configuration["Waymark:Safety:AppendFooter"], "Waymark:Safety:AppendFooter", defaultValue: false);
        var text = configuration["Waymark:Safety:FooterText"]?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = DefaultFooterText;
        }
        else if (text.Length > MaxFooterChars)
        {
            throw new InvalidOperationException($"Waymark:Safety:FooterText must be at most {MaxFooterChars} characters.");
        }

        return new SafetyOptions(redact, footer, text);
    }

    private static bool ReadBool(string? value, string name, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed) ? parsed : throw new InvalidOperationException($"{name} must be true or false.");
    }
}
