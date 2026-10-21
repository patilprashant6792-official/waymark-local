namespace Waymark.Core.Security;

/// <summary>Checks and normalises the patterns the owner types for a workspace. Pure: no file system access.</summary>
public static class AccessRuleRules
{
    public const int MaxPatternLength = 100;
    public const int MaxNoteLength = 200;
    public const int MaxRulesPerWorkspace = 200;

    private const int MinKeywordLength = 3;
    private const int MaxExtensionLength = 16;
    private const string NameCharactersMessage = "Use letters, digits, spaces and . _ - (and * ? as wildcards).";

    /// <summary>True when the pattern is acceptable; <paramref name="normalized"/> is then what gets stored. Otherwise <paramref name="error"/> says why.</summary>
    public static bool TryNormalize(AccessRuleKind kind, string? pattern, out string normalized, out string error)
    {
        normalized = string.Empty;
        var value = pattern?.Trim() ?? string.Empty;
        string? failure;
        if (value.Length == 0)
        {
            failure = "Enter a pattern.";
        }
        else if (value.Length > MaxPatternLength)
        {
            failure = $"Use at most {MaxPatternLength} characters.";
        }
        else
        {
            failure = kind switch
            {
                AccessRuleKind.BlockFolder => NormalizeFolder(value, out normalized),
                AccessRuleKind.BlockFileName => NormalizeFileName(value, out normalized),
                AccessRuleKind.BlockKeyword => NormalizeKeyword(value, out normalized),
                AccessRuleKind.AllowExtension => NormalizeExtension(value, out normalized),
                _ => "Unknown rule type.",
            };
        }

        error = failure ?? string.Empty;
        return failure is null;
    }

    public static bool IsValidNote(string? note) => (note?.Length ?? 0) <= MaxNoteLength && note?.Any(char.IsControl) != true;

    private static string? NormalizeFolder(string value, out string normalized)
    {
        normalized = string.Empty;
        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return "Enter a folder name.";
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            return "'.' and '..' are not allowed.";
        }

        if (segments.Length > 1 && segments.Any(segment => segment.AsSpan().IndexOfAny('*', '?') >= 0))
        {
            return "Wildcards work in a single folder name, not in a path.";
        }

        if (!segments.All(segment => IsNameText(segment, allowWildcards: true)))
        {
            return NameCharactersMessage;
        }

        if (!segments.Any(segment => segment.Any(char.IsAsciiLetterOrDigit)))
        {
            return "Include at least one letter or digit.";
        }

        normalized = string.Join('/', segments);
        return null;
    }

    private static string? NormalizeFileName(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!IsNameText(value, allowWildcards: true))
        {
            return "A file name cannot contain '/' or '\\'. " + NameCharactersMessage;
        }

        if (!value.Any(char.IsAsciiLetterOrDigit))
        {
            return "Include at least one letter or digit, so the rule cannot block every file.";
        }

        normalized = value;
        return null;
    }

    private static string? NormalizeKeyword(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length < MinKeywordLength)
        {
            return $"Use at least {MinKeywordLength} characters, so the rule does not block unrelated paths.";
        }

        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return "Use letters, digits and . _ - only.";
        }

        normalized = value.ToLowerInvariant();
        return null;
    }

    private static string? NormalizeExtension(string value, out string normalized)
    {
        normalized = string.Empty;
        var text = value.StartsWith('.') ? value : "." + value;
        if (text.Length < 2 || text.Length > MaxExtensionLength || !text.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            return "Use a dot followed by letters or digits, for example .proto.";
        }

        normalized = text.ToLowerInvariant();
        return null;
    }

    private static bool IsNameText(string text, bool allowWildcards) =>
        text.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' || (allowWildcards && c is '*' or '?'));
}
