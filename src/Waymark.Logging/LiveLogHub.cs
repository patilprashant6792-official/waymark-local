using System.Threading.Channels;

namespace Waymark.Logging;

/// <summary>
/// The only place log entries live: a fixed-size ring in memory plus a live feed to any number of viewers.
/// Nothing is written to disk, and the oldest entries are overwritten when the ring is full.
/// </summary>
public sealed class LiveLogHub
{
    private readonly Lock _gate = new();
    private readonly LogEntry?[] _ring;
    private readonly List<LiveLogSubscription> _subscribers = [];
    private int _next;
    private int _count;
    private long _lastId;

    public LiveLogHub(LiveLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Capacity, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Capacity, 50_000);
        _ring = new LogEntry?[options.Capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>Stores the entry, gives it the next id and offers it to every viewer without ever waiting for one.</summary>
    public LogEntry Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var stored = entry with { Id = ++_lastId };
            _ring[_next] = stored;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }

            foreach (var subscriber in _subscribers)
            {
                subscriber.Offer(stored);
            }

            return stored;
        }
    }

    /// <summary>The newest entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot(int max)
    {
        lock (_gate)
        {
            return SnapshotLocked(max);
        }
    }

    /// <summary>Forgets every kept entry. Ids keep counting, and viewers that are open keep what they already show.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Starts a live feed. The snapshot and the subscription are taken together, so a viewer sees every entry exactly
    /// once: the snapshot first, then whatever arrives on the subscription. Dispose the subscription when done.
    /// </summary>
    public LiveLogSubscription Subscribe(int snapshotMax, out IReadOnlyList<LogEntry> snapshot, int bufferSize = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 16);
        var subscription = new LiveLogSubscription(this, bufferSize);
        lock (_gate)
        {
            snapshot = SnapshotLocked(snapshotMax);
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    internal void Remove(LiveLogSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    private List<LogEntry> SnapshotLocked(int max)
    {
        var take = Math.Clamp(max, 0, _count);
        var result = new List<LogEntry>(take);
        var start = (_next - take + _ring.Length) % _ring.Length;
        for (var i = 0; i < take; i++)
        {
            result.Add(_ring[(start + i) % _ring.Length]!);
        }

        return result;
    }
}

/// <summary>One viewer's live feed. A viewer that falls behind loses its oldest pending entries and can see how many.</summary>
public sealed class LiveLogSubscription : IDisposable
{
    private readonly LiveLogHub _hub;
    private readonly Channel<LogEntry> _channel;
    private long _dropped;

    internal LiveLogSubscription(LiveLogHub hub, int bufferSize)
    {
        _hub = hub;
        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(bufferSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    /// <summary>How many entries were skipped because this viewer could not keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal void Offer(LogEntry entry) => _channel.Writer.TryWrite(entry);

    public void Dispose()
    {
        _hub.Remove(this);
        _channel.Writer.TryComplete();
    }
}
