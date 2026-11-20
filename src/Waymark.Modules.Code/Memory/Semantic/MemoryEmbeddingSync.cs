using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// Keeps the vectors of notes and flows in step with what they say. It is the one listener: every writer of a note, a flow, the settings or a workspace publishes an event,
/// so none can forget the index. Changes are collected for 2 seconds of quiet, then each changed workspace gets one pass (<see cref="MemoryWorkspaceIndexer"/>).
/// <para>
/// Off, or no key: nothing is read or sent. An excluded workspace: nothing is sent and its vectors are deleted. A refused key: nothing more is sent until the settings change.
/// A rate limit or a network failure: the pass waits (2 s doubling to 5 min, with jitter, or the provider's Retry-After) and the work stays pending, because pending is
/// computed from the stored hashes, never kept in memory. A start-up sweep covers what changed while Waymark was off.
/// </para>
/// </summary>
internal sealed class MemoryEmbeddingSync(
    IEventBus events,
    IWorkspaceStore workspaces,
    IMemorySettings settings,
    IEmbeddingProviders providers,
    IMemoryEmbeddingStore store,
    MemoryWorkspaceIndexer indexer,
    MemoryVectorCache cache,
    QueryEmbedder queries,
    MemoryIndexStatus status,
    TimeProvider time,
    ILogger<MemoryEmbeddingSync> logger) : BackgroundService
{
    /// <summary>A signal for every workspace (start-up, settings changed).</summary>
    private const long AllWorkspaces = 0;

    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Channel<long> _signals = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<IDisposable> _subscriptions = [];
    private int _failures;
    private volatile bool _keyRejected;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(events.Subscribe<NoteChanged>(changed => Signal(changed.WorkspaceId)));
        _subscriptions.Add(events.Subscribe<FlowChanged>(changed => Signal(changed.WorkspaceId)));
        _subscriptions.Add(events.Subscribe<FeatureMapsChanged>(changed => Signal(changed.WorkspaceId)));
        _subscriptions.Add(events.Subscribe<WorkspaceChanged>(changed => OnWorkspaceChangedAsync(changed)));
        _subscriptions.Add(events.Subscribe<MemorySettingsChanged>(_ => OnSettingsChangedAsync()));
        _signals.Writer.TryWrite(AllWorkspaces);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _signals.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var due = new HashSet<long>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (due.Count == 0 && !await _signals.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                await CollectAsync(due, stoppingToken).ConfigureAwait(false);
                var retryIn = await RunAsync(due, stoppingToken).ConfigureAwait(false);
                if (retryIn is { } wait)
                {
                    // The work stays in 'due'; a new signal does not cut the wait short, so a failing provider is not hammered.
                    await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The search-by-meaning index stopped a pass unexpectedly; it tries again on the next change.");
                due.Clear();
            }
        }
    }

    // Takes signals until none came for 2 seconds, so a burst of saves is one pass.
    private async Task CollectAsync(HashSet<long> due, CancellationToken ct)
    {
        while (true)
        {
            while (_signals.Reader.TryRead(out var workspaceId))
            {
                due.Add(workspaceId);
            }

            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(Quiet);
            try
            {
                if (!await _signals.Reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One pass over the due workspaces. Returns how long to wait before trying again, or null when everything due was handled.</summary>
    private async Task<TimeSpan?> RunAsync(HashSet<long> due, CancellationToken ct)
    {
        var active = await settings.GetAsync(ct).ConfigureAwait(false);
        var provider = providers.Find(active.Provider);
        var apiKey = active.IsActive && provider is not null ? await settings.GetKeyAsync(active.Provider, ct).ConfigureAwait(false) : null;
        if (!active.IsActive || provider is null || apiKey is null)
        {
            due.Clear();
            await status.SetAsync(now => now with { State = MemoryIndexState.Off, Pending = 0, RetryAtUtc = null }).ConfigureAwait(false);
            return null;
        }

        if (_keyRejected)
        {
            due.Clear();
            return null;
        }

        var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
        IReadOnlyList<Workspace> targets = due.Contains(AllWorkspaces) ? all : [.. all.Where(w => due.Contains(w.Id))];
        await status.SetAsync(now => now with { State = MemoryIndexState.Working, RetryAtUtc = null }).ConfigureAwait(false);
        foreach (var workspace in targets)
        {
            try
            {
                await SyncAsync(workspace, active, provider, apiKey, ct).ConfigureAwait(false);
                due.Remove(workspace.Id);
                _failures = 0;
            }
            catch (EmbeddingException ex) when (ex.Kind == EmbeddingFailure.Unauthorized)
            {
                _keyRejected = true;
                due.Clear();
                logger.LogWarning("Search by meaning stopped: {Provider} refused the key. Enter it again on the Setup page.", provider.DisplayName);
                await status.SetAsync(now => now with { State = MemoryIndexState.KeyRejected, LastError = ex.Message, RetryAtUtc = null }).ConfigureAwait(false);
                return null;
            }
            catch (EmbeddingException ex)
            {
                var wait = Backoff(ex.RetryAfter);
                logger.LogWarning("Search by meaning will try again in {Seconds:0} s: {Reason}", wait.TotalSeconds, ex.Message);
                await status.SetAsync(now => now with { State = MemoryIndexState.Backoff, LastError = ex.Message, RetryAtUtc = time.GetUtcNow() + wait }).ConfigureAwait(false);
                return wait;
            }
        }

        due.Remove(AllWorkspaces);
        var embedded = await store.CountAsync(active.ModelId, ct).ConfigureAwait(false);
        await status.SetAsync(now => now with { State = MemoryIndexState.Idle, Embedded = embedded, Pending = 0, RetryAtUtc = null }).ConfigureAwait(false);
        return null;
    }

    private async Task SyncAsync(Workspace workspace, MemorySettings active, IEmbeddingProvider provider, string apiKey, CancellationToken ct)
    {
        // A workspace that is off is not searched, so nothing of it is sent; its vectors stay for when it is on again.
        if (!workspace.Enabled)
        {
            return;
        }

        if (await settings.IsExcludedAsync(workspace.Id, ct).ConfigureAwait(false))
        {
            if (await store.DeleteWorkspaceAsync(workspace.Id, ct).ConfigureAwait(false) > 0)
            {
                logger.LogInformation("Deleted the search-by-meaning vectors of workspace {Workspace}: it is excluded.", workspace.Name);
            }

            cache.ForgetWorkspace(workspace.Id);
            return;
        }

        var pending = (await indexer.PendingAsync(workspace, active.ModelId, ct).ConfigureAwait(false)).Count;
        await status.SetAsync(now => now with { Pending = pending }).ConfigureAwait(false);
        var pass = await indexer.RunAsync(
            workspace,
            active,
            provider,
            apiKey,
            done => status.SetAsync(now => now with { Pending = Math.Max(0, pending - done), LastSuccessUtc = time.GetUtcNow() }),
            ct).ConfigureAwait(false);
        if (pass.Embedded > 0)
        {
            logger.LogInformation("Search by meaning embedded {Count} item(s) of workspace {Workspace}.", pass.Embedded, workspace.Name);
        }
    }

    private TimeSpan Backoff(TimeSpan? retryAfter)
    {
        var exponent = Math.Min(_failures, 10);
        _failures++;
        var wait = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks * (1L << exponent)));
        var jitter = TimeSpan.FromMilliseconds(wait.TotalMilliseconds * Random.Shared.NextDouble() * 0.25);
        wait += jitter;
        if (retryAfter is { } asked && asked > wait)
        {
            wait = asked;
        }

        return wait > MaxBackoff ? MaxBackoff : wait;
    }

    private ValueTask Signal(long workspaceId)
    {
        _signals.Writer.TryWrite(workspaceId);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnWorkspaceChangedAsync(WorkspaceChanged changed)
    {
        if (changed.Kind == WorkspaceChangeKind.Removed)
        {
            // The rows go with the workspace (the table cascades); only the copy in memory is left to drop.
            cache.ForgetWorkspace(changed.WorkspaceId);
            return;
        }

        await Signal(changed.WorkspaceId).ConfigureAwait(false);
    }

    // A new key, provider, model or switch: a refused key may work now, cached query vectors may be of another model, and every workspace is checked again.
    private ValueTask OnSettingsChangedAsync()
    {
        _keyRejected = false;
        _failures = 0;
        indexer.ForgetRefusals();
        queries.Clear();
        cache.ForgetAll();
        return Signal(AllWorkspaces);
    }
}
