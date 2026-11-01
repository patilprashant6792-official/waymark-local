using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Waymark.Core.Memory;

/// <summary>
/// One place in the code, saved by name: a file, a line range of a file, or a type or member of it. Never content, so it stays valid when the code moves between branches.
/// Text forms: <c>path</c>, <c>path#L10-40</c>, <c>path:Name</c> (a type when the file has one of that name, otherwise a member) and <c>path:Type.Member</c>.
/// </summary>
public sealed record FeatureComponent(string Path, string? Type, string? Member, int? StartLine, int? EndLine)
{
    public bool IsRange => StartLine is not null;

    public bool IsFile => Member is null && StartLine is null;
}

/// <summary>One line of the components parameter: the component and its optional note (the text after a '|').</summary>
public sealed record ComponentLine(FeatureComponent Component, string Note);

/// <summary>Reads and prints the text form of a <see cref="FeatureComponent"/>. The stored form is what <see cref="Format"/> prints.</summary>
public static class FeatureComponentSpec
{
    public const int MaxSpecChars = 300;
    public const int MaxNoteChars = 120;
    public const int MaxRangeLines = 400;

    private const int ShortChars = 60;

    private static readonly Regex RangeText = new("^L?([0-9]{1,7})(?:-L?([0-9]{1,7}))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Parses 'spec' or 'spec | note'. A leading list marker ('- ' or '* ') and surrounding backticks are ignored.</summary>
    public static bool TryParseLine(string? line, [NotNullWhen(true)] out ComponentLine? parsed, out string? error)
    {
        parsed = null;
        error = null;
        var text = (line ?? string.Empty).Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }

        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var specText = (bar < 0 ? text : text[..bar]).Trim().Trim('`');
        var note = bar < 0 ? string.Empty : text[(bar + 1)..].Trim();
        if (note.Length > MaxNoteChars)
        {
            error = $"The note of '{Shorten(specText)}' is longer than {MaxNoteChars} characters.";
            return false;
        }

        if (!TryParse(specText, out var component, out error))
        {
            return false;
        }

        parsed = new ComponentLine(component, note);
        return true;
    }

    public static bool TryParse(string? spec, [NotNullWhen(true)] out FeatureComponent? component, out string? error)
    {
        component = null;
        error = null;
        var text = (spec ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxSpecChars)
        {
            error = $"A component has 1 to {MaxSpecChars} characters.";
            return false;
        }

        if (text.Any(char.IsWhiteSpace) || text.Contains(',', StringComparison.Ordinal))
        {
            error = $"Not a component: '{Shorten(text)}'. Write one component per line, with no spaces or commas inside it.";
            return false;
        }

        if (Path.IsPathRooted(text))
        {
            error = $"'{Shorten(text)}' is an absolute path. Use the path from the workspace root.";
            return false;
        }

        var hash = text.IndexOf('#', StringComparison.Ordinal);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (hash >= 0)
        {
            var rangePath = colon < 0 ? Normalize(text[..hash]) : null;
            var match = RangeText.Match(text[(hash + 1)..]);
            if (rangePath is null || !match.Success)
            {
                error = $"Not a component: '{Shorten(text)}'. A line range is written 'path#L10-40'.";
                return false;
            }

            var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : start;
            if (start < 1 || end < start || end - start >= MaxRangeLines)
            {
                error = $"A line range needs 1 <= start <= end and at most {MaxRangeLines} lines: '{Shorten(text)}'.";
                return false;
            }

            component = new FeatureComponent(rangePath, null, null, start, end);
            return true;
        }

        var path = Normalize(colon < 0 ? text : text[..colon]);
        if (path is null)
        {
            error = $"Not a path inside the workspace: '{Shorten(text)}'.";
            return false;
        }

        if (colon < 0)
        {
            component = new FeatureComponent(path, null, null, null, null);
            return true;
        }

        var symbol = text[(colon + 1)..];
        var dot = symbol.LastIndexOf('.');
        var type = dot < 0 ? null : symbol[..dot];
        var member = dot < 0 ? symbol : symbol[(dot + 1)..];
        if (member.Length == 0 || type is { Length: 0 })
        {
            error = $"Not a component: '{Shorten(text)}'. After ':' write a name or Type.Member.";
            return false;
        }

        component = new FeatureComponent(path, type, member, null, null);
        return true;
    }

    /// <summary>The canonical text of a component; two components that print the same text are the same component.</summary>
    public static string Format(FeatureComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.IsRange)
        {
            return $"{component.Path}#L{component.StartLine}-{component.EndLine}";
        }

        if (component.Member is null)
        {
            return component.Path;
        }

        return component.Type is null ? $"{component.Path}:{component.Member}" : $"{component.Path}:{component.Type}.{component.Member}";
    }

    private static string? Normalize(string path)
    {
        var text = path.Trim().Replace('\\', '/');
        if (text.Length == 0)
        {
            return null;
        }

        return text.Split('/').Any(part => part.Length == 0 || part is "." or "..") ? null : text;
    }

    private static string Shorten(string text) => text.Length <= ShortChars ? text : text[..ShortChars] + "...";
}
