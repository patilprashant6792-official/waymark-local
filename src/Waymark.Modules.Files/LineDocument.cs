namespace Waymark.Modules.Files;

/// <summary>A text file as a list of lines plus whether its last line ends with a line break.</summary>
internal sealed class LineDocument
{
    private LineDocument(List<string> lines, bool endsWithNewline)
    {
        Lines = lines;
        EndsWithNewline = endsWithNewline;
    }

    public List<string> Lines { get; }

    public bool EndsWithNewline { get; }

    /// <summary>Splits on line breaks. An empty file has no lines and gets a final line break once it is filled.</summary>
    public static LineDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new LineDocument([], true);
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var endsWithNewline = normalized[^1] == '\n';
        var body = endsWithNewline ? normalized[..^1] : normalized;
        return new LineDocument([.. body.Split('\n')], endsWithNewline);
    }

    /// <summary>Lines of patch content. One final line break ends the last line instead of adding an empty one.</summary>
    public static List<string> SplitContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var body = normalized.EndsWith('\n') ? normalized[..^1] : normalized;
        return [.. body.Split('\n')];
    }

    /// <summary>The text with '\n' line breaks; TextFileFormat turns them into the file's own line ending.</summary>
    public string ToText()
    {
        if (Lines.Count == 0)
        {
            return string.Empty;
        }

        var text = string.Join('\n', Lines);
        return EndsWithNewline ? text + '\n' : text;
    }
}
