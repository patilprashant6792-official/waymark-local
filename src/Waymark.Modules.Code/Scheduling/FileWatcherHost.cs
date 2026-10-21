using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.LiveFeed;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// Watches every enabled workspace for C# changes and feeds the index scheduler. Events are debounced per file
/// (editors fire several per save). Changes made by Waymark's own file tools arrive as FileChanged and FolderChanged
/// events. A watcher buffer overflow or error triggers a reconcile of that workspace.
/// </summary>
internal sealed class FileWatcherHost(
    IWorkspaceService workspaceService,
    IIndexScheduler scheduler,
    FileAccessPolicy policy,
    IEventBus events,
    CodeIndexOptions options,
    ILogger<FileWatcherHost> logger) : BackgroundService
{
    private const int WatcherBufferBytes = 64 * 1024;
    private const string WholeWorkspaceKey = "*";

    private readonly Lock _gate = new();
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<(long WorkspaceId, string Path), CancellationTokenSource> _debounce = [];
    private CancellationToken _stopping;
    private readonly Dictionary<long, string> _names = [];
    private readonly WatchLog _watchLog = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stopping = stoppingToken;
        using var workspaceSubscription = events.Subscribe<WorkspaceChanged>(OnWorkspaceChangedAsync);
        using var fileSubscription = events.Subscribe<FileChanged>(change => OnToolFileChanged(change.WorkspaceId, change.RelativePath));
        using var folderSubscription = events.Subscribe<FolderChanged>(change => OnToolFolderChanged(change.WorkspaceId));
        try
        {
            await StartAllAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopAll();
            logger.LogInformation("Code file watcher stopped");
        }
    }

    // One workspace that cannot be watched, or a failure to list them, must not stop the others or the service: it is reported and skipped.
    private async Task StartAllAsync(CancellationToken ct)
    {
        try
        {
            foreach (var workspace in await workspaceService.ListAsync(ct).ConfigureAwait(false))
            {
                if (workspace.Enabled)
                {
                    TryStart(workspace);
                }
            }

            logger.LogInformation("Code file watcher watching {Count} workspace(s)", WatcherCount());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWatch(LogLevel.Error, "could not start", "all workspaces", ex.Message, ex);
        }
    }

    private void TryStart(Workspace workspace)
    {
        try
        {
            StartWatching(workspace);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWatch(LogLevel.Error, "could not watch", workspace.Name, workspace.Path, ex);
        }
    }

    private async ValueTask OnWorkspaceChangedAsync(WorkspaceChanged change)
    {
        switch (change.Kind)
        {
            case WorkspaceChangeKind.Added:
            case WorkspaceChangeKind.Enabled:
                var workspace = (await workspaceService.ListAsync(_stopping).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Id == change.WorkspaceId);
                if (workspace is { Enabled: true })
                {
                    StartWatching(workspace);
                }

                break;
            case WorkspaceChangeKind.Removed:
            case WorkspaceChangeKind.Disabled:
                StopWatching(change.WorkspaceId);
                break;
        }
    }

    private ValueTask OnToolFileChanged(long workspaceId, string relativePath)
    {
        if (relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && CodeFileWalker.IsIndexable(relativePath, policy))
        {
            ScheduleFile(workspaceId, relativePath);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnToolFolderChanged(long workspaceId)
    {
        ScheduleWorkspace(workspaceId);
        return ValueTask.CompletedTask;
    }

    private void StartWatching(Workspace workspace)
    {
        if (!Directory.Exists(workspace.Path))
        {
            logger.LogWatch(LogLevel.Warning, "cannot watch", workspace.Name, $"folder {workspace.Path} does not exist");
            return;
        }

        StopWatching(workspace.Id);
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(workspace.Path)
            {
                Filter = "*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = WatcherBufferBytes,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            logger.LogWatch(LogLevel.Warning, "cannot watch", workspace.Name, workspace.Path, ex);
            return;
        }

        var id = workspace.Id;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path));
        watcher.Created += (_, e) => OnDiskEvent(id, root, e.FullPath, directoryMayBeNew: true);
        watcher.Changed += (_, e) => OnDiskEvent(id, root, e.FullPath, directoryMayBeNew: false);
        watcher.Deleted += (_, e) => OnDiskEvent(id, root, e.FullPath, directoryMayBeNew: false, removed: true);
        watcher.Renamed += (_, e) =>
        {
            OnDiskEvent(id, root, e.OldFullPath, directoryMayBeNew: false, removed: true);
            OnDiskEvent(id, root, e.FullPath, directoryMayBeNew: true);
        };
        watcher.Error += (_, e) =>
        {
            logger.LogWatch(LogLevel.Warning, "lost events", NameOf(id), "checking the workspace again", e.GetException());
            scheduler.RequestReconcile(id);
        };

        lock (_gate)
        {
            _watchers[id] = watcher;
            _names[id] = workspace.Name;
        }

        watcher.EnableRaisingEvents = true;
        logger.LogWatch(LogLevel.Information, "watching", workspace.Name, workspace.Path);
    }

    private void StopWatching(long workspaceId)
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            _watchers.Remove(workspaceId, out watcher);
            _names.Remove(workspaceId);
        }

        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        CancelDebounce(workspaceId);
    }

    private void StopAll()
    {
        long[] ids;
        lock (_gate)
        {
            ids = [.. _watchers.Keys];
        }

        foreach (var id in ids)
        {
            StopWatching(id);
        }
    }

    private int WatcherCount()
    {
        lock (_gate)
        {
            return _watchers.Count;
        }
    }

    // A file path ending in .cs is one file. Any other path may be a folder that was created, moved or deleted with
    // C# files inside; those events do not name the files, so the workspace is reconciled (folders the policy
    // refuses, like bin and obj, are ignored). Folder names with a dot are repaired by the next reconcile or re-index.
    private void OnDiskEvent(long workspaceId, string root, string fullPath, bool directoryMayBeNew, bool removed = false)
    {
        try
        {
            var relative = CodeFileWalker.ToRelative(root, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || !CodeFileWalker.IsIndexable(relative, policy))
            {
                return;
            }

            if (relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleFile(workspaceId, relative);
            }
            else if ((directoryMayBeNew && Directory.Exists(fullPath)) || (removed && Path.GetExtension(relative).Length == 0))
            {
                ScheduleWorkspace(workspaceId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWatch(LogLevel.Warning, "event failed", NameOf(workspaceId), ex.Message, ex);
        }
    }

    private string NameOf(long workspaceId)
    {
        lock (_gate)
        {
            return _names.TryGetValue(workspaceId, out var name) ? name : string.Create(CultureInfo.InvariantCulture, $"#{workspaceId}");
        }
    }

    private void ScheduleFile(long workspaceId, string relativePath) =>
        Debounce((workspaceId, relativePath.ToLowerInvariant()), () =>
        {
            scheduler.RequestFile(workspaceId, relativePath);
            _watchLog.Changed(logger, NameOf(workspaceId), relativePath);
        });

    private void ScheduleWorkspace(long workspaceId) =>
        Debounce((workspaceId, WholeWorkspaceKey), () =>
        {
            scheduler.RequestReconcile(workspaceId);
            logger.LogWatch(LogLevel.Information, "folders changed", NameOf(workspaceId), "checking the workspace again");
        });

    private void Debounce((long WorkspaceId, string Path) key, Action action)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        lock (_gate)
        {
            if (_debounce.Remove(key, out var superseded))
            {
                superseded.Cancel();
                superseded.Dispose();
            }

            _debounce[key] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(options.WatcherDebounceMs, cts.Token).ConfigureAwait(false);
                action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWatch(LogLevel.Warning, "queue failed", NameOf(key.WorkspaceId), ex.Message, ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (_debounce.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                    {
                        _debounce.Remove(key);
                    }
                }

                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private void CancelDebounce(long workspaceId)
    {
        lock (_gate)
        {
            foreach (var key in _debounce.Keys.Where(key => key.WorkspaceId == workspaceId).ToArray())
            {
                _debounce.Remove(key, out var cts);
                cts?.Cancel();
                cts?.Dispose();
            }
        }
    }

    public override void Dispose()
    {
        StopAll();
        base.Dispose();
    }
}
