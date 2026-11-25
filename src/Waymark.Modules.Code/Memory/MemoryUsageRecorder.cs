using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// Remembers which flows and notes models opened and writes the times in batches: every <see cref="FlushEvery"/>, sooner when <see cref="FlushAt"/> uses are pending,
/// and once more when Waymark stops. <see cref="Touch"/> only updates a dictionary, so a read tool never waits for the write queue and never fails because of it.
/// If Waymark stops abruptly, the uses of the last few seconds are lost: last used is a hint for the owner, not a record.
/// </summary>
internal sealed class MemoryUsageRecorder(IMemoryUsageStore store, IEventBus events, TimeProvider time, ILogger<MemoryUsageRecorder> logger)
    : BackgroundService, IMemoryUsage
{
    internal static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(15);
    internal const int FlushAt = 256;
    private const int MaxPending = 10_000;
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _full = new(0, 1);
    private ConcurrentDictionary<(MemoryItemKind Kind, long Id), MemoryUse> _pending = new();

    public void Touch(long workspaceId, MemoryItemKind kind, IReadOnlyCollection<long> ids)
    {
        try
        {
            if (ids is null || ids.Count == 0)
            {
                return;
            }

            var now = time.GetUtcNow();
            var pending = _pending;
            foreach (var id in ids)
            {
                var use = new MemoryUse(workspaceId, kind, id, now);
                pending.AddOrUpdate((kind, id), use, (_, old) => old.UsedUtc >= now ? old : use);
            }

            if (pending.Count >= FlushAt && _full.CurrentCount == 0)
            {
                try
                {
                    _full.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Another touch already asked for a flush.
                }
            }

            if (pending.Count > MaxPending)
            {
                // The flush loop is behind: drop the oldest picture rather than grow without bound.
                logger.LogWarning("More than {Max} memory uses are waiting to be written; the pending uses are dropped.", MaxPending);
                Interlocked.Exchange(ref _pending, new ConcurrentDictionary<(MemoryItemKind Kind, long Id), MemoryUse>());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A memory use could not be recorded; the read itself is not affected.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _full.WaitAsync(FlushEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StopFlushTimeout);
        await FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _full.Dispose();
        base.Dispose();
    }

    /// <summary>Writes what is pending in one batch. Internal for tests.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        var taken = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<(MemoryItemKind Kind, long Id), MemoryUse>());
        if (taken.IsEmpty)
        {
            return;
        }

        var uses = taken.Values.ToList();
        try
        {
            await store.TouchAsync(uses, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("{Count} memory uses were not written: Waymark is stopping.", uses.Count);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Count} memory uses could not be written and were dropped.", uses.Count);
            return;
        }

        var workspaceIds = uses.Select(use => use.WorkspaceId).Distinct().ToList();
        await events.PublishAsync(new MemoryUsageRecorded(workspaceIds)).ConfigureAwait(false);
    }
}
