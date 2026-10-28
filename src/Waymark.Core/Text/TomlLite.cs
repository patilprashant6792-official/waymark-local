using System.Text;

namespace Waymark.Core.Text;

/// <summary>One table of a TOML file: its dotted name and the keys directly under it, each with its raw value text.</summary>
/// <param name="Name">For example <c>project</c> or <c>tool.poetry.dependencies</c>; empty for the keys before the first table header.</param>
/// <param name="IsArrayItem">True for one item of an array of tables, <c>[[package]]</c>.</param>
/// <param name="Values">Key to raw value. A value that spans several lines keeps its line breaks. Comments are removed.</param>
public sealed record TomlSection(string Name, bool IsArrayItem, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// A reader for the small part of TOML that project files and lock files use (pyproject.toml, uv.lock, poetry.lock, pylock.toml): tables, arrays of
/// tables, and key = value pairs whose values may span lines. It does not build a value tree; it hands out raw text and two helpers read strings from it.
/// Anything it does not understand is skipped, never thrown, because the files are only read to describe a project.
/// </summary>
public static class TomlLite
{
    public static IReadOnlyList<TomlSection> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sections = new List<TomlSection>();
        var current = new SectionBuilder(string.Empty, isArrayItem: false);
        var lines = text.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var scanner = new Scanner();
            var clean = scanner.Feed(lines[index++].TrimEnd('\r')).Trim();
            if (clean.Length == 0)
            {
                continue;
            }

            if (clean.StartsWith("[[", StringComparison.Ordinal) && clean.EndsWith("]]", StringComparison.Ordinal))
            {
                sections.Add(current.Build());
                current = new SectionBuilder(clean[2..^2].Trim(), isArrayItem: true);
                continue;
            }

            var equals = FindEquals(clean);
            if (clean[0] == '[' && equals < 0)
            {
                sections.Add(current.Build());
                current = new SectionBuilder(clean.TrimStart('[').TrimEnd(']').Trim(), isArrayItem: false);
                continue;
            }

            if (equals <= 0)
            {
                continue;
            }

            var key = KeyText(clean[..equals].Trim());
            var value = new StringBuilder(clean[(equals + 1)..].Trim());
            while (scanner.Open && index < lines.Length)
            {
                value.Append('\n').Append(scanner.Feed(lines[index++].TrimEnd('\r')).Trim());
            }

            current.Values[key] = value.ToString();
        }

        sections.Add(current.Build());
        return [.. sections.Where(s => s.Name.Length > 0 || s.Values.Count > 0)];
    }

    /// <summary>The text of a string value (basic, literal or triple-quoted); null when the value is not a string.</summary>
    public static string? AsString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.Length >= 6
            && ((text.StartsWith("\"\"\"", StringComparison.Ordinal) && text.EndsWith("\"\"\"", StringComparison.Ordinal))
                || (text.StartsWith("'''", StringComparison.Ordinal) && text.EndsWith("'''", StringComparison.Ordinal))))
        {
            return text[3..^3].Trim('\n');
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return Unescape(text[1..^1]);
        }

        return text.Length >= 2 && text[0] == '\'' && text[^1] == '\'' ? text[1..^1] : null;
    }

    /// <summary>The string items of an array value, in order. Items that are not strings (inline tables, numbers) are left out.</summary>
    public static IReadOnlyList<string> Strings(string? raw)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return items;
        }

        var depth = 0;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c is '[' or '{')
            {
                depth++;
                i++;
            }
            else if (c is ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                i++;
            }
            else if (c is '"' or '\'')
            {
                var end = EndOfString(raw, i);
                if (depth == 1)
                {
                    var literal = raw.Substring(i, Math.Min(end + 1, raw.Length) - i);
                    var value = AsString(literal);
                    if (value is not null)
                    {
                        items.Add(value);
                    }
                }

                i = end + 1;
            }
            else
            {
                i++;
            }
        }

        return items;
    }

    private static int FindEquals(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is '"' or '\'')
            {
                i = EndOfString(line, i) + 1;
                continue;
            }

            if (c == '=')
            {
                return i;
            }

            if (c is '[' or '{')
            {
                // A bracket before any '=' means a table header, not a key.
                return -1;
            }

            i++;
        }

        return -1;
    }

    private static string KeyText(string key) =>
        key.Length >= 2 && ((key[0] == '"' && key[^1] == '"') || (key[0] == '\'' && key[^1] == '\'')) ? key[1..^1] : key;

    /// <summary>The index of the quote that closes the string starting at <paramref name="start"/>; the line length when it is not closed.</summary>
    private static int EndOfString(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote)
            {
                return i;
            }
        }

        return text.Length;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                builder.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private sealed class SectionBuilder(string name, bool isArrayItem)
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public TomlSection Build() => new(name, isArrayItem, Values);
    }

    /// <summary>Removes comments and tracks whether a value is still open: an unclosed bracket, brace or multi-line string.</summary>
    private sealed class Scanner
    {
        private string? _multiline;
        private int _depth;

        public bool Open => _depth > 0 || _multiline is not null;

        public string Feed(string line)
        {
            var result = new StringBuilder(line.Length);
            var i = 0;
            while (i < line.Length)
            {
                var c = line[i];
                if (_multiline is not null)
                {
                    if (string.CompareOrdinal(line, i, _multiline, 0, 3) == 0)
                    {
                        result.Append(_multiline);
                        i += 3;
                        _multiline = null;
                    }
                    else
                    {
                        result.Append(c);
                        i++;
                    }

                    continue;
                }

                if (c == '#')
                {
                    break;
                }

                if (c is '"' or '\'')
                {
                    var triple = new string(c, 3);
                    if (string.CompareOrdinal(line, i, triple, 0, 3) == 0)
                    {
                        _multiline = triple;
                        result.Append(triple);
                        i += 3;
                        continue;
                    }

                    var end = EndOfString(line, i);
                    result.Append(line, i, Math.Min(end + 1, line.Length) - i);
                    i = end + 1;
                    continue;
                }

                if (c is '[' or '{')
                {
                    _depth++;
                }
                else if (c is ']' or '}')
                {
                    _depth = Math.Max(0, _depth - 1);
                }

                result.Append(c);
                i++;
            }

            return result.ToString();
        }
    }
}
