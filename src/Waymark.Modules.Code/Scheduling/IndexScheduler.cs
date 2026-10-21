using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// The single worker behind the code index. Requests are merged per workspace (a reconcile covers any pending file,
/// too many pending files turn into a reconcile) and processed one workspace at a time.
/// </summary>
internal sealed class IndexScheduler(
    IWorkspaceService workspaceService,
    WorkspaceIndexer indexer,
    IEventBus events,
    CodeIndexOptions options,
    ILogger<IndexScheduler> logger) : BackgroundService, IIndexScheduler
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PendingWork> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);

    public void RequestReconcile(long workspaceId) => Enqueue(workspaceId, work => work.Reconcile = true);

    public void RequestRebuild(long workspaceId) => Enqueue(workspaceId, work =>
    {
        work.Rebuild = true;
        work.Reconcile = true;
        work.Files.Clear();
    });

    public void RequestFile(long workspaceId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        Enqueue(workspaceId, work =>
        {
            if (work.Reconcile)
            {
                return;
            }

            work.Files.Add(relativePath);
            if (work.Files.Count > options.MaxPendingFiles)
            {
                work.Reconcile = true;
                work.Files.Clear();
            }
        });
    }

    public void Forget(long workspaceId)
    {
        lock (_gate)
        {
            _pending.Remove(workspaceId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var subscription = events.Subscribe<WorkspaceChanged>(OnWorkspaceChanged);
        try
        {
            await QueueEnabledWorkspacesAsync(stoppingToken).ConfigureAwait(false);

            while (true)
            {
                await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                while (TryTake(out var workspaceId, out var work))
                {
                    await ProcessAsync(workspaceId, work, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Code index scheduler stopped");
        }
    }

    // Failing to list the workspaces now must not stop the worker for good: later requests still arrive and are processed.
    private async Task QueueEnabledWorkspacesAsync(CancellationToken ct)
    {
        try
        {
            foreach (var workspace in await workspaceService.ListAsync(ct).ConfigureAwait(false))
            {
                if (workspace.Enabled)
                {
                    RequestReconcile(workspace.Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The code index could not list the workspaces at start-up");
        }
    }

    private ValueTask OnWorkspaceChanged(WorkspaceChanged change)
    {
        switch (change.Kind)
        {
            case WorkspaceChangeKind.Added:
            case WorkspaceChangeKind.Enabled:
                RequestReconcile(change.WorkspaceId);
                break;
            case WorkspaceChangeKind.Removed:
            case WorkspaceChangeKind.Disabled:
                Forget(change.WorkspaceId);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private async Task ProcessAsync(long workspaceId, PendingWork work, CancellationToken ct)
    {
        try
        {
            var workspace = (await workspaceService.ListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Id == workspaceId);
            if (workspace is null || !workspace.Enabled)
            {
                return;
            }

            if (work.Reconcile)
            {
                await indexer.ReconcileAsync(workspace, work.Rebuild, ct).ConfigureAwait(false);
                return;
            }

            foreach (var relativePath in work.Files)
            {
                await indexer.IndexFileAsync(workspace, relativePath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Index work for workspace {WorkspaceId} failed", workspaceId);
        }
    }

    private void Enqueue(long workspaceId, Action<PendingWork> merge)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(workspaceId, out var work))
            {
                work = new PendingWork();
                _pending[workspaceId] = work;
            }

            merge(work);
        }

        _signal.Release();
    }

    private bool TryTake(out long workspaceId, out PendingWork work)
    {
        lock (_gate)
        {
            foreach (var entry in _pending)
            {
                workspaceId = entry.Key;
                work = entry.Value;
                _pending.Remove(workspaceId);
                return true;
            }
        }

        workspaceId = 0;
        work = null!;
        return false;
    }

    private sealed class PendingWork
    {
        public bool Rebuild { get; set; }

        public bool Reconcile { get; set; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
