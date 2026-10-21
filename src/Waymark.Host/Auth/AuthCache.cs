using System.Collections.Concurrent;
using System.Security.Claims;

namespace Waymark.Host.Auth;

/// <summary>
/// Remembers the result of a successful credential lookup for a short time so every MCP request is not a database
/// read. Any revocation bumps the epoch, which drops every entry at once, so access ends on the next request.
/// </summary>
public sealed class AuthCache(TimeProvider time)
{
    private const int MaxEntries = 1000;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _touches = new();
    private long _epoch;

    /// <summary>Read this before the database lookup and pass it to Set, so a revocation in between wins.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    public ClaimsPrincipal? TryGet(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.Epoch != Epoch || entry.ValidUntil <= time.GetUtcNow())
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry.Principal;
    }

    public void Set(string key, ClaimsPrincipal principal, DateTimeOffset validUntil, long epochAtLookup)
    {
        if (epochAtLookup != Epoch)
        {
            return;
        }

        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[key] = new Entry(principal, validUntil, epochAtLookup);
    }

    /// <summary>Call after any revocation.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _epoch);
        _entries.Clear();
    }

    /// <summary>True at most once a minute per id, to keep last-used writes cheap.</summary>
    public bool ShouldTouch(string id)
    {
        var now = time.GetUtcNow();
        if (_touches.TryGetValue(id, out var last) && now - last < TouchInterval)
        {
            return false;
        }

        _touches[id] = now;
        return true;
    }

    private sealed record Entry(ClaimsPrincipal Principal, DateTimeOffset ValidUntil, long Epoch);
}
