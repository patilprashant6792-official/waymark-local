using System.Text.RegularExpressions;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// Watches what a program in a terminal prints: counts error and warning lines (so a read can say "2 new errors since you last looked"), and tells
/// when the app inside a watcher went down or came back, from the texts its ecosystem prints for that.
/// </summary>
public sealed partial class HealthWatch(IReadOnlyList<string> appDown, IReadOnlyList<string> appUp)
{
    private const int MaxKept = 300;
    private readonly object _gate = new();
    private readonly List<LogLine> _errors = [];
    private readonly List<long> _warnings = [];

    /// <summary>True while the app inside the program is down.</summary>
    public bool AppDown { get; private set; }

    /// <summary>Set when <see cref="AppDown"/> changed; the reader clears it.</summary>
    public bool AppStateChanged { get; set; }

    /// <summary>The number of the last error line seen.</summary>
    public long LastErrorLine { get; private set; }

    [GeneratedRegex(@"\berror\b|\bexception\b|traceback \(most recent call last\)|\bfailed\b|\bfail:|npm err!|\bfatal\b|\bpanic\b", RegexOptions.IgnoreCase, 100)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b0 (error|errors|failed|failures)\b|\bno errors\b|errors?: 0\b|\berror\(s\): 0\b", RegexOptions.IgnoreCase, 100)]
    private static partial Regex NoErrors();

    [GeneratedRegex(@"\bwarn(ing)?\b", RegexOptions.IgnoreCase, 100)]
    private static partial Regex WarningWords();

    public void Check(LogLine line)
    {
        var text = line.Text;
        if (text.Length == 0)
        {
            return;
        }

        if (AppDown && appUp.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            AppDown = false;
            AppStateChanged = true;
        }
        else if (!AppDown && appDown.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            AppDown = true;
            AppStateChanged = true;
        }

        if (IsError(text))
        {
            lock (_gate)
            {
                _errors.Add(line);
                if (_errors.Count > MaxKept)
                {
                    _errors.RemoveAt(0);
                }

                LastErrorLine = line.Number;
            }
        }
        else if (Matches(WarningWords(), text))
        {
            lock (_gate)
            {
                _warnings.Add(line.Number);
                if (_warnings.Count > MaxKept)
                {
                    _warnings.RemoveAt(0);
                }
            }
        }
    }

    public int ErrorsAfter(long line)
    {
        lock (_gate)
        {
            return _errors.Count(e => e.Number > line);
        }
    }

    public int WarningsAfter(long line)
    {
        lock (_gate)
        {
            return _warnings.Count(w => w > line);
        }
    }

    /// <summary>The first error lines after <paramref name="line"/>, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<LogLine> ErrorLinesAfter(long line, int max)
    {
        lock (_gate)
        {
            return _errors.Where(e => e.Number > line).Take(max).ToList();
        }
    }

    private static bool IsError(string text) => Matches(ErrorWords(), text) && !Matches(NoErrors(), text);

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
