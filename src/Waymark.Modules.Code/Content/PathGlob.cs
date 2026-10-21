using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Code.Content;

/// <summary>
/// A small glob over workspace-relative paths written with '/': '*' stays inside one folder, '**' crosses folders,
/// '?' is one character, and a trailing '/' means everything below that folder. A pattern with no '/' is matched
/// against the file name in any folder. Matching ignores case.
/// </summary>
internal sealed class PathGlob
{
    private const int MaxPatternLength = 200;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex _regex;
    private readonly bool _fileNameOnly;

    private PathGlob(Regex regex, bool fileNameOnly)
    {
        _regex = regex;
        _fileNameOnly = fileNameOnly;
    }

    /// <summary>Null when no pattern was given.</summary>
    /// <exception cref="ArgumentException">The pattern is too long, absolute or leaves the workspace.</exception>
    public static PathGlob? Parse(string? pattern)
    {
        var value = pattern?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > MaxPatternLength)
        {
            throw new ArgumentException($"pathGlob is longer than {MaxPatternLength} characters.");
        }

        if (value.Contains(':') || value.Split('/').Any(part => part == ".."))
        {
            throw new ArgumentException("pathGlob must be relative to the workspace root and must not contain ':' or '..'.");
        }

        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.TrimStart('/');
        if (value.EndsWith('/'))
        {
            value += "**";
        }

        if (value.Length == 0)
        {
            return null;
        }

        return new PathGlob(
            new Regex(ToRegex(value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
            fileNameOnly: !value.Contains('/'));
    }

    public bool IsMatch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var target = _fileNameOnly ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;
        try
        {
            return _regex.IsMatch(target);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string ToRegex(string glob)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        regex.Append("(?:.*/)?");
                    }
                    else
                    {
                        regex.Append(".*");
                    }

                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(new string(glob[i], 1)));
                    break;
            }
        }

        return regex.Append('$').ToString();
    }
}
