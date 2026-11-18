using System.Collections.Concurrent;
using Waymark.Core.Events;
using Waymark.Core.Guides;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>What a tool result should tell an agent that did not ask for the guide.</summary>
public enum GuideNotice
{
    None,
    NeverLoaded,
    Changed,
}

/// <summary>
/// Remembers which guide text each client received, by hash, in memory and in the store so a restart forgets nothing.
/// A client whose guide is missing or outdated is told once per guide text; asking for the guide settles it.
/// The module is a tool group, the overview (<c>host</c>) or a workspace (<c>ws:{id}</c>, see <see cref="WorkspaceGuideRules"/>).
/// </summary>
public sealed class GuideDeliveries : IDisposable
{
    private readonly IGuideStore _store;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<(string Client, string Module), string> _delivered = new();
    private readonly ConcurrentDictionary<(string Client, string Module, string Hash), byte> _told = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _loaded;

    public GuideDeliveries(IGuideStore store, IEventBus events, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _time = time;

        // The database forgets the deliveries of a removed workspace (a trigger on workspaces); the copy kept here must too, because the id can be given again.
        _subscription = events.Subscribe<WorkspaceChanged>(change =>
        {
            if (change.Kind == WorkspaceChangeKind.Removed)
            {
                Forget(WorkspaceGuideRules.ModuleKey(change.WorkspaceId));
            }

            return ValueTask.CompletedTask;
        });
    }

    public async Task<GuideNotice> CheckAsync(string client, string module, string hash, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        _delivered.TryGetValue((client, module), out var held);
        if (string.Equals(held, hash, StringComparison.Ordinal) || !_told.TryAdd((client, module, hash), 0))
        {
            return GuideNotice.None;
        }

        return held is null ? GuideNotice.NeverLoaded : GuideNotice.Changed;
    }

    /// <summary>True when the client's last delivery of this module is exactly this text. Changes nothing.</summary>
    public async Task<bool> HoldsAsync(string client, string module, string hash, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _delivered.TryGetValue((client, module), out var held) && string.Equals(held, hash, StringComparison.Ordinal);
    }

    public async Task RecordAsync(string client, string module, string hash, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        _delivered[(client, module)] = hash;
        await _store.RecordDeliveryAsync(client, module, hash, _time.GetUtcNow(), ct);
    }

    /// <summary>Drops what is remembered in memory about one module for every client. The stored rows are the caller's to remove.</summary>
    public void Forget(string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        foreach (var key in _delivered.Keys.Where(key => string.Equals(key.Module, module, StringComparison.Ordinal)).ToList())
        {
            _delivered.TryRemove(key, out _);
        }

        foreach (var key in _told.Keys.Where(key => string.Equals(key.Module, module, StringComparison.Ordinal)).ToList())
        {
            _told.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _gate.Dispose();
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var delivery in await _store.ListDeliveriesAsync(ct))
            {
                _delivered.TryAdd((delivery.ClientId, delivery.Module), delivery.ContentHash);
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
