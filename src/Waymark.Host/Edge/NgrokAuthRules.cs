namespace Waymark.Host.Edge;

/// <summary>Checks the shape of a pasted ngrok authtoken before it is saved, so a typo or a pasted command is caught early.</summary>
public static class NgrokAuthRules
{
    public const int MinLength = 20;
    public const int MaxLength = 256;

    public static bool TryNormalize(string? input, out string value, out string error)
    {
        value = string.Empty;
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length is < MinLength or > MaxLength || !trimmed.All(IsAllowed))
        {
            error = "That does not look like an ngrok authtoken. Copy only the token from dashboard.ngrok.com, not the whole command.";
            return false;
        }

        value = trimmed;
        error = string.Empty;
        return true;
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
