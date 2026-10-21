using System.Collections.Concurrent;
using Waymark.Core.Events;

namespace Waymark.Core.Security;

/// <summary>Holds each workspace's compiled rules so the path guard does not query the database for every path.</summary>
public interface IAccessRuleCache
{
    Task<WorkspaceAccessRules> GetAsync(long workspaceId, CancellationToken ct);

    /// <summary>The rules from the cache. A workspace that is not cached yet is loaded first; the guard warms the cache before it calls this.</summary>
    WorkspaceAccessRules Get(long workspaceId);

    /// <summary>Drops the workspace's rules; the next lookup reads them again.</summary>
    void Invalidate(long workspaceId);
}

public sealed class AccessRuleCache : IAccessRuleCache, IDisposable
{
    private readonly ConcurrentDictionary<long, WorkspaceAccessRules> _entries = new();
    private readonly IAccessRuleStore _store;
    private readonly IDisposable _subscription;
    private long _generation;

    public AccessRuleCache(IAccessRuleStore store, IEventBus events)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        _store = store;
        // Ids can be reused after a workspace is removed, so any workspace change drops that workspace's entry.
        _subscription = events.Subscribe<WorkspaceChanged>(change =>
        {
            Invalidate(change.WorkspaceId);
            return ValueTask.CompletedTask;
        });
    }

    public async Task<WorkspaceAccessRules> GetAsync(long workspaceId, CancellationToken ct)
    {
        if (_entries.TryGetValue(workspaceId, out var cached))
        {
            return cached;
        }

        var generation = Interlocked.Read(ref _generation);
        var loaded = new WorkspaceAccessRules(await _store.ListAsync(workspaceId, ct).ConfigureAwait(false));

        // A change that arrived while the rules were being read makes them stale; hand them out once but do not keep them.
        if (generation == Interlocked.Read(ref _generation))
        {
            _entries[workspaceId] = loaded;
        }

        return loaded;
    }

    // Task.Run keeps a caller with a synchronisation context (a Blazor circuit) from deadlocking. Fails closed: a read error throws.
    public WorkspaceAccessRules Get(long workspaceId) =>
        _entries.TryGetValue(workspaceId, out var cached)
            ? cached
            : Task.Run(() => GetAsync(workspaceId, CancellationToken.None)).GetAwaiter().GetResult();

    public void Invalidate(long workspaceId)
    {
        Interlocked.Increment(ref _generation);
        _entries.TryRemove(workspaceId, out _);
    }

    public void Dispose() => _subscription.Dispose();
}
