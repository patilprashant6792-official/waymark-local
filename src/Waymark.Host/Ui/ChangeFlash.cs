namespace Waymark.Host.Ui;

/// <summary>
/// Decides which parts of a page just changed, so they can flash a highlight that fades away: a card, a table row, a
/// list item, a figure or a log line. The page asks <see cref="Phase"/> for each part while it draws, with a key that
/// names the part and a token that changes whenever its content changes.
/// <para>
/// The answer is null (do not flash), "a" or "b". It flips between "a" and "b" on each change, which the style sheet maps
/// to two identical animations with different names: the browser restarts an animation only when its name changes, so
/// a part that changes again while it is still flashing restarts the highlight without being redrawn. A part stops
/// flashing after <see cref="Lifetime"/>, so moving or recreating its element later does not replay an old highlight.
/// </para>
/// <para>
/// What is on the page when it first settles is only the baseline. Call <see cref="Arm"/> once the first data is loaded and
/// <see cref="EndPass"/> after every draw; nothing flashes until the first draw that follows <see cref="Arm"/> is done.
/// One instance belongs to one page and is used from the page's own render thread.
/// </para>
/// </summary>
public sealed class ChangeFlash(TimeProvider time)
{
    /// <summary>How long a part keeps its highlight attribute; a little longer than the animation itself.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _armed;
    private bool _drawnSinceArm;
    private bool _baselined;
    private DateTimeOffset _lastPrune = time.GetUtcNow();

    /// <summary>The data the page starts with is loaded: the next draw sets the baseline instead of flashing.</summary>
    public void Arm()
    {
        _armed = true;
        _drawnSinceArm = false;
    }

    /// <summary>
    /// "a" or "b" while the part is flashing, otherwise null. <paramref name="key"/> names the part (stable for the life of
    /// the part); <paramref name="token"/> is anything that changes when its content does. A key seen for the first time
    /// after the baseline is a new part and flashes.
    /// </summary>
    public string? Phase(string key, string token)
    {
        var now = time.GetUtcNow();
        if (_armed)
        {
            _drawnSinceArm = true;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(token, now);
            _entries[key] = entry;
            if (_baselined)
            {
                entry.Changes = 1;
                entry.ChangedAt = now;
            }
        }
        else
        {
            entry.LastSeen = now;
            if (!string.Equals(entry.Token, token, StringComparison.Ordinal))
            {
                entry.Token = token;
                if (_baselined)
                {
                    entry.Changes++;
                    entry.ChangedAt = now;
                }
            }
        }

        if (entry.Changes == 0 || now - entry.ChangedAt > Lifetime)
        {
            return null;
        }

        return entry.Changes % 2 == 1 ? "a" : "b";
    }

    /// <summary>Call after every draw of the page.</summary>
    public void EndPass()
    {
        if (_armed && _drawnSinceArm)
        {
            _baselined = true;
        }

        var now = time.GetUtcNow();
        if (now - _lastPrune < PruneEvery)
        {
            return;
        }

        // Parts that left the page long ago (old log lines, calls that scrolled off the list) are forgotten.
        _lastPrune = now;
        foreach (var key in _entries.Where(pair => now - pair.Value.LastSeen > ForgetAfter).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry(string token, DateTimeOffset seen)
    {
        public string Token { get; set; } = token;

        public int Changes { get; set; }

        public DateTimeOffset ChangedAt { get; set; }

        public DateTimeOffset LastSeen { get; set; } = seen;
    }
}
