namespace Waymark.Core.LiveFeed;

/// <summary>One thing that is happening right now.</summary>
public sealed record ActivityItem(long Id, LogKind Kind, string Label, DateTimeOffset StartedUtc);

/// <summary>
/// What is in flight at this moment: running tool calls and command runs that wait, queue or run. An item exists only while the work does;
/// nothing is kept afterwards and nothing is written anywhere.
/// </summary>
public sealed class LiveActivity(TimeProvider time)
{
    private const int MaxLabelChars = 120;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, ActivityItem> _items = [];
    private long _last;

    /// <summary>Raised after an item started, changed its label or ended. Handlers run on the thread of the work; pages must marshal.</summary>
    public event Action? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public IReadOnlyList<ActivityItem> Current()
    {
        lock (_gate)
        {
            return _items.Values.OrderBy(item => item.Id).ToList();
        }
    }

    /// <summary>Starts an item. Dispose the result when the work ends.</summary>
    public ActivityScope Begin(LogKind kind, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        long id;
        lock (_gate)
        {
            id = ++_last;
            _items[id] = new ActivityItem(id, kind, Cut(label), time.GetUtcNow());
        }

        RaiseChanged();
        return new ActivityScope(this, id);
    }

    internal void Rename(long id, string label)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item))
            {
                return;
            }

            _items[id] = item with { Label = Cut(label) };
        }

        RaiseChanged();
    }

    internal void End(long id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(id);
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    private static string Cut(string label) => label.Length <= MaxLabelChars ? label : label[..MaxLabelChars] + "...";

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A page that fails to redraw must never break the work it shows.
        }
    }
}

/// <summary>The handle of one running item. Disposing it ends the item; disposing twice is harmless.</summary>
public sealed class ActivityScope : IDisposable
{
    private readonly LiveActivity _owner;
    private readonly long _id;
    private int _ended;

    internal ActivityScope(LiveActivity owner, long id)
    {
        _owner = owner;
        _id = id;
    }

    public void Update(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (Volatile.Read(ref _ended) == 0)
        {
            _owner.Rename(_id, label);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _owner.End(_id);
        }
    }
}
