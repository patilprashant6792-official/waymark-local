using System.Text;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// Picks the first few lines of a run's output as they arrive, so the Live page can show how a command started without streaming all of it.
/// Blank lines do not count, colour codes are removed and a long line is cut. Each line is passed to <paramref name="emit"/> once.
/// </summary>
public sealed class OutputPreview(int maxLines, int maxLineChars, Action<string> emit)
{
    private const int MaxPartialChars = 4000;

    private readonly Lock _gate = new();
    private readonly StringBuilder _partial = new();
    private int _taken;

    public void Add(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        List<string> ready = [];
        lock (_gate)
        {
            if (_taken >= maxLines)
            {
                return;
            }

            _partial.Append(chunk);
            var text = _partial.ToString();
            var start = 0;
            int newline;
            while (_taken + ready.Count < maxLines && (newline = text.IndexOf('\n', start)) >= 0)
            {
                Take(text[start..newline], ready);
                start = newline + 1;
            }

            _taken += ready.Count;
            _partial.Clear();
            if (_taken < maxLines && start < text.Length)
            {
                _partial.Append(text, start, Math.Min(text.Length - start, MaxPartialChars));
            }
        }

        foreach (var line in ready)
        {
            emit(line);
        }
    }

    /// <summary>Called when the run ends: a last line without a line break still counts. Nothing is taken after this.</summary>
    public void Complete()
    {
        List<string> ready = [];
        lock (_gate)
        {
            if (_taken < maxLines && _partial.Length > 0)
            {
                Take(_partial.ToString(), ready);
            }

            _partial.Clear();
            _taken = maxLines;
        }

        foreach (var line in ready)
        {
            emit(line);
        }
    }

    private void Take(string raw, List<string> into)
    {
        var plain = AnsiText.Plain(raw).Trim();
        if (plain.Length == 0)
        {
            return;
        }

        into.Add(plain.Length <= maxLineChars ? plain : plain[..maxLineChars] + "...");
    }
}
