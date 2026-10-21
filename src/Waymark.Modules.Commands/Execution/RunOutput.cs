using System.Globalization;
using System.Text;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// The output of one run while it runs. New text collects in a pending buffer and is moved into the kept text by <see cref="Flush"/>,
/// which numbers every flush. A viewer that takes a <see cref="Snapshot"/> and then listens to the flushes after its number never misses or repeats text.
/// Only the first and the last megabyte are kept; the middle of a huge output is dropped and marked.
/// </summary>
public sealed class RunOutput
{
    private const int HeadLimit = 1_000_000;
    private const int TailLimit = 1_000_000;

    private readonly object _gate = new();
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private readonly StringBuilder _pending = new();
    private long _omitted;
    private long _seq;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Append(text);
        }
    }

    /// <summary>Moves the pending text into the kept text. Null when nothing was pending.</summary>
    public (long Seq, string Text)? Flush()
    {
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return null;
            }

            var text = _pending.ToString();
            _pending.Clear();
            Keep(text);
            _seq++;
            return (_seq, text);
        }
    }

    public (string Text, long Seq) Snapshot()
    {
        lock (_gate)
        {
            return (Compose(), _seq);
        }
    }

    private void Keep(string text)
    {
        var remaining = text.AsSpan();
        var room = HeadLimit - _head.Length;
        if (room > 0)
        {
            var take = Math.Min(room, remaining.Length);
            _head.Append(remaining[..take]);
            remaining = remaining[take..];
        }

        if (remaining.Length == 0)
        {
            return;
        }

        _tail.Append(remaining);
        if (_tail.Length > TailLimit * 2)
        {
            var drop = _tail.Length - TailLimit;
            _tail.Remove(0, drop);
            _omitted += drop;
        }
    }

    private string Compose()
    {
        if (_omitted == 0)
        {
            return _head.ToString() + _tail;
        }

        var marker = string.Create(CultureInfo.InvariantCulture, $"\n[... {_omitted:N0} characters left out of the middle ...]\n");
        return _head + marker + _tail;
    }
}
