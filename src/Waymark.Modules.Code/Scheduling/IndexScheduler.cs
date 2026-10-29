using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// The single worker behind the code index. Requests are merged per workspace (a reconcile covers any pending file or folder,
/// too many pending files or folders turn into a reconcile) and processed one workspace at a time. Inside a workspace the files
/// are read and parsed in parallel and stored in batches (see <see cref="WorkspaceIndexer"/>).
/// </summary>
internal sealed class IndexScheduler(
    IWorkspaceService workspaceService,
    WorkspaceIndexer indexer,
    IEventBus events,
    CodeIndexOptions options,
    ILogger<IndexScheduler> logger) : BackgroundService, IIndexScheduler
{
    private const int MaxPendingFolders = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, PendingWork> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);

    public void RequestReconcile(long workspaceId) => Enqueue(workspaceId, work => work.Reconcile = true);

    public void RequestRebuild(long workspaceId) => Enqueue(workspaceId, work =>
    {
        work.Rebuild = true;
        work.Reconcile = true;
        work.Files.Clear();
        work.Folders.Clear();
    });

    public void RequestFile(long workspaceId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        RequestChanges(workspaceId, [relativePath], []);
    }

    public void RequestFolder(long workspaceId, string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        RequestChanges(workspaceId, [], [relativeFolder]);
    }

    public void RequestChanges(long workspaceId, IReadOnlyCollection<string> files, IReadOnlyCollection<string> folders)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(folders);
        if (files.Count == 0 && folders.Count == 0)
        {
            return;
        }

        Enqueue(workspaceId, work => work.Merge(files, folders, options.MaxPendingFiles));
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

            // A folder is compared as a whole, so a changed file inside it needs no second look.
            var folders = OutermostFolders(work.Folders);
            foreach (var folder in folders)
            {
                await indexer.ReconcileAsync(workspace, false, folder, ct).ConfigureAwait(false);
            }

            var files = work.Files.Where(file => !folders.Any(folder => IsInside(file, folder))).ToList();
            if (files.Count > 0)
            {
                await indexer.IndexFilesAsync(workspace, files, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Index work for workspace {WorkspaceId} failed", workspaceId);
        }
    }

    private static List<string> OutermostFolders(IEnumerable<string> folders)
    {
        var kept = new List<string>();
        foreach (var folder in folders.OrderBy(candidate => candidate.Length))
        {
            if (!kept.Any(parent => IsInside(folder, parent)))
            {
                kept.Add(folder);
            }
        }

        return kept;
    }

    private static bool IsInside(string path, string folder) =>
        path.Length > folder.Length
        && path[folder.Length] == '/'
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

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

        public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds changes; too many of them, or a change of the workspace root, become one reconcile of the whole workspace.</summary>
        public void Merge(IReadOnlyCollection<string> files, IReadOnlyCollection<string> folders, int maxFiles)
        {
            if (Reconcile)
            {
                return;
            }

            foreach (var raw in folders)
            {
                var folder = raw.Replace('\\', '/').Trim('/');
                if (folder.Length == 0 || folder == ".")
                {
                    WholeWorkspace();
                    return;
                }

                Folders.Add(folder);
            }

            foreach (var file in files)
            {
                Files.Add(file);
            }

            if (Files.Count > maxFiles || Folders.Count > MaxPendingFolders)
            {
                WholeWorkspace();
            }
        }

        private void WholeWorkspace()
        {
            Reconcile = true;
            Files.Clear();
            Folders.Clear();
        }
    }
}