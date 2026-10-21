using System.Collections.Concurrent;
using Waymark.Core.Guides;

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
/// </summary>
public sealed class GuideDeliveries(IGuideStore store, TimeProvider time) : IDisposable
{
    private readonly ConcurrentDictionary<(string Client, string Module), string> _delivered = new();
    private readonly ConcurrentDictionary<(string Client, string Module, string Hash), byte> _told = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _loaded;

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
        await store.RecordDeliveryAsync(client, module, hash, time.GetUtcNow(), ct);
    }

    public void Dispose() => _gate.Dispose();

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

            foreach (var delivery in await store.ListDeliveriesAsync(ct))
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
