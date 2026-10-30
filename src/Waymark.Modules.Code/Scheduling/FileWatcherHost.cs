using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.LiveFeed;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// Watches every enabled workspace for source file changes (C#, Python) and feeds the index scheduler.
/// The watcher callbacks only put the event on a queue, so the operating system's event buffer is emptied at once; one reader then judges each event
/// (policy, language, folder or file) and notes it for its workspace. A workspace has one timer that is pushed back by every new event, so a burst
/// (a save, a branch switch, a build) is handed to the scheduler once, after it is quiet for <c>WatcherDebounceMs</c>.
/// Changes made by Waymark's own file tools arrive as FileChanged and FolderChanged events. A watcher buffer overflow or error triggers a reconcile of that workspace.
/// </summary>
internal sealed class FileWatcherHost(
    IWorkspaceService workspaceService,
    IIndexScheduler scheduler,
    FileAccessPolicy policy,
    LanguageIndexers languages,
    IEventBus events,
    CodeIndexOptions options,
    ILogger<FileWatcherHost> logger) : BackgroundService
{
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<long, string> _names = [];
    private readonly Dictionary<long, PendingChanges> _pending = [];
    private readonly Channel<DiskEvent> _diskEvents = Channel.CreateUnbounded<DiskEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly WatchLog _watchLog = new();
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stopping = stoppingToken;
        using var workspaceSubscription = events.Subscribe<WorkspaceChanged>(OnWorkspaceChangedAsync);
        using var fileSubscription = events.Subscribe<FileChanged>(change => OnToolFileChanged(change.WorkspaceId, change.RelativePath));
        using var folderSubscription = events.Subscribe<FolderChanged>(change => OnToolFolderChanged(change.WorkspaceId, change.RelativePath));
        var reader = Task.Run(() => ReadEventsAsync(stoppingToken), CancellationToken.None);
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
            _diskEvents.Writer.TryComplete();
            await reader.ConfigureAwait(false);
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
        if (languages.Handles(relativePath) && CodeFileWalker.IsIndexable(relativePath, policy))
        {
            Mark(workspaceId, relativePath, null);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnToolFolderChanged(long workspaceId, string relativePath)
    {
        Mark(workspaceId, null, relativePath);
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
            // LastWrite already reports a changed file, so the Size notification would only double the events of every save.
            watcher = new FileSystemWatcher(workspace.Path)
            {
                Filter = "*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
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
        watcher.Created += (_, e) => Post(new DiskEvent(id, root, e.FullPath, DirectoryMayBeNew: true, Removed: false));
        watcher.Changed += (_, e) => Post(new DiskEvent(id, root, e.FullPath, DirectoryMayBeNew: false, Removed: false));
        watcher.Deleted += (_, e) => Post(new DiskEvent(id, root, e.FullPath, DirectoryMayBeNew: false, Removed: true));
        watcher.Renamed += (_, e) =>
        {
            Post(new DiskEvent(id, root, e.OldFullPath, DirectoryMayBeNew: false, Removed: true));
            Post(new DiskEvent(id, root, e.FullPath, DirectoryMayBeNew: true, Removed: false));
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
        PendingChanges? pending;
        lock (_gate)
        {
            _watchers.Remove(workspaceId, out watcher);
            _names.Remove(workspaceId);
            _pending.Remove(workspaceId, out pending);
        }

        pending?.Debounce.Dispose();
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void StopAll()
    {
        long[] ids;
        lock (_gate)
        {
            ids = [.. _watchers.Keys.Concat(_pending.Keys).Distinct()];
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

    // Runs on the watcher's own thread: nothing here may touch the disk or take long, or the operating system's buffer overflows.
    private void Post(DiskEvent change) => _diskEvents.Writer.TryWrite(change);

    private async Task ReadEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var change in _diskEvents.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Classify(change);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // A path of an indexed language is one file. Any other path may be a folder that was created, moved or deleted with source files inside;
    // those events do not name the files, so that folder alone is compared with the index (folders the policy refuses, like bin and obj,
    // are ignored). A removed folder whose name has a dot is repaired by the next reconcile or re-index.
    private void Classify(DiskEvent change)
    {
        try
        {
            var relative = CodeFileWalker.ToRelative(change.Root, change.FullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || !CodeFileWalker.IsIndexable(relative, policy))
            {
                return;
            }

            if (languages.Handles(relative))
            {
                Mark(change.WorkspaceId, relative, null);
            }
            else if ((change.DirectoryMayBeNew && Directory.Exists(change.FullPath)) || (change.Removed && Path.GetExtension(relative).Length == 0))
            {
                Mark(change.WorkspaceId, null, relative);
            }
        }
        catch (Exception ex)
        {
            logger.LogWatch(LogLevel.Warning, "event failed", NameOf(change.WorkspaceId), ex.Message, ex);
        }
    }

    private string NameOf(long workspaceId)
    {
        lock (_gate)
        {
            return _names.TryGetValue(workspaceId, out var name) ? name : string.Create(CultureInfo.InvariantCulture, $"#{workspaceId}");
        }
    }

    // Notes one changed file or folder and pushes the workspace's timer back. A folder of "" or "." is the whole workspace.
    private void Mark(long workspaceId, string? file, string? folder)
    {
        string? normalized = null;
        var whole = false;
        if (folder is not null)
        {
            normalized = folder.Replace('\\', '/').Trim('/');
            whole = normalized.Length == 0 || normalized == ".";
        }

        lock (_gate)
        {
            if (!_pending.TryGetValue(workspaceId, out var pending))
            {
                pending = new PendingChanges(new Timer(_ => Flush(workspaceId), null, Timeout.Infinite, Timeout.Infinite));
                _pending[workspaceId] = pending;
            }

            if (file is not null)
            {
                pending.Files.Add(file);
            }

            if (whole)
            {
                pending.Whole = true;
            }
            else if (normalized is not null)
            {
                pending.Folders.Add(normalized);
            }

            pending.Debounce.Change(options.WatcherDebounceMs, Timeout.Infinite);
        }
    }

    // The workspace has been quiet for the debounce time: everything noted so far goes to the scheduler in one request.
    private void Flush(long workspaceId)
    {
        string[] files;
        string[] folders;
        bool whole;
        lock (_gate)
        {
            if (!_pending.TryGetValue(workspaceId, out var pending))
            {
                return;
            }

            files = [.. pending.Files];
            folders = [.. pending.Folders];
            whole = pending.Whole;
            pending.Files.Clear();
            pending.Folders.Clear();
            pending.Whole = false;
        }

        if (files.Length == 0 && folders.Length == 0 && !whole)
        {
            return;
        }

        try
        {
            var name = NameOf(workspaceId);
            if (whole)
            {
                scheduler.RequestReconcile(workspaceId);
                logger.LogWatch(LogLevel.Information, "folders changed", name, "checking the workspace again");
                return;
            }

            scheduler.RequestChanges(workspaceId, files, folders);
            foreach (var file in files)
            {
                _watchLog.Changed(logger, name, file);
            }

            if (folders.Length > 0)
            {
                logger.LogWatch(LogLevel.Information, "folders changed", name, string.Create(CultureInfo.InvariantCulture, $"checking {folders.Length} folder(s) again"));
            }
        }
        catch (Exception ex)
        {
            logger.LogWatch(LogLevel.Warning, "queue failed", NameOf(workspaceId), ex.Message, ex);
        }
    }

    public override void Dispose()
    {
        StopAll();
        base.Dispose();
    }

    private readonly record struct DiskEvent(long WorkspaceId, string Root, string FullPath, bool DirectoryMayBeNew, bool Removed);

    private sealed class PendingChanges(Timer debounce)
    {
        public Timer Debounce { get; } = debounce;

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Whole { get; set; }
    }
}