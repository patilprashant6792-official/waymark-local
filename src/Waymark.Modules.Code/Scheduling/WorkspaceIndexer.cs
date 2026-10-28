using Microsoft.Extensions.Logging;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>Brings the code index of one workspace in line with the disk: a whole-folder reconcile or a single file.</summary>
internal sealed class WorkspaceIndexer(
    IWorkspaceStore workspaces,
    ICodeIndexStore store,
    LanguageIndexers languages,
    IPathGuard pathGuard,
    FileAccessPolicy policy,
    IEventBus events,
    CodeIndexOptions options,
    TimeProvider time,
    ILogger<WorkspaceIndexer> logger)
{
    private enum Step
    {
        Skipped,
        Changed,
        Failed,
    }

    /// <summary>
    /// Parses only files whose size or modified time differs from the index (the content hash then decides whether the
    /// parse is needed), stores them one by one, and removes indexed files that are gone. Progress goes to the event bus.
    /// A failure is logged and reported as a Failed event; cancellation propagates.
    /// </summary>
    public async Task ReconcileAsync(Workspace workspace, bool rebuild, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Started, 0, 0, 0, 0)).ConfigureAwait(false);
        var total = 0;
        var processed = 0;
        var changed = 0;
        var failed = 0;
        try
        {
            if (rebuild)
            {
                await store.ClearWorkspaceAsync(workspace.Id, ct).ConfigureAwait(false);
            }

            var stamps = (await store.ListStampsAsync(workspace.Id, ct).ConfigureAwait(false))
                .ToDictionary(stamp => stamp.RelativePath, StringComparer.OrdinalIgnoreCase);
            var (files, truncated) = await Task.Run(
                () => CodeFileWalker.Enumerate(workspace.Path, policy, options.MaxFilesPerWorkspace, languages, ct), ct).ConfigureAwait(false);
            if (truncated)
            {
                logger.LogWarning(
                    "Workspace {Workspace} has more than {Max} source files; the rest are not indexed (Waymark:Code:MaxFilesPerWorkspace).",
                    workspace.Name,
                    options.MaxFilesPerWorkspace);
            }

            total = files.Count;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                stamps.Remove(file.RelativePath, out var known);
                switch (await IndexDiskFileAsync(workspace, file, known, ct).ConfigureAwait(false))
                {
                    case Step.Changed:
                        changed++;
                        break;
                    case Step.Failed:
                        failed++;
                        break;
                }

                processed++;
                if (processed % options.ProgressEveryFiles == 0)
                {
                    await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Running, total, processed, changed, failed)).ConfigureAwait(false);
                }
            }

            // A truncated walk did not see every file, so a missing path proves nothing.
            if (!truncated)
            {
                foreach (var gone in stamps.Keys)
                {
                    await store.RemoveFileAsync(workspace.Id, gone, ct).ConfigureAwait(false);
                    changed++;
                }
            }

            await workspaces.MarkIndexedAsync(workspace.Id, time.GetUtcNow(), ct).ConfigureAwait(false);
            await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Completed, total, processed, changed, failed)).ConfigureAwait(false);
            logger.LogInformation(
                "Indexed workspace {Workspace}: {Total} files, {Changed} updated, {Failed} unreadable",
                workspace.Name,
                total,
                changed,
                failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing workspace {Workspace} failed", workspace.Name);
            await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Failed, total, processed, changed, failed)).ConfigureAwait(false);
        }
    }

    /// <summary>Re-reads one file: removed from the index when it is gone or refused by the policy, left alone when it is not a source file; otherwise parsed again when its content changed.</summary>
    public async Task IndexFileAsync(Workspace workspace, string relativePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        GuardedPath guarded;
        try
        {
            guarded = pathGuard.Resolve(workspace, relativePath);
        }
        catch (FileAccessDeniedException)
        {
            await store.RemoveFileAsync(workspace.Id, relativePath, ct).ConfigureAwait(false);
            return;
        }

        if (!languages.TryGet(guarded.RelativePath, out var indexer))
        {
            return;
        }

        var known = await store.GetFileAsync(workspace.Id, guarded.RelativePath, ct).ConfigureAwait(false);
        try
        {
            var result = await indexer.IndexFileAsync(guarded.AbsolutePath, guarded.RelativePath, known?.Stamp.Hash, ct).ConfigureAwait(false);
            await StoreAsync(workspace, guarded.RelativePath, result, known is not null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (await store.RemoveFileAsync(workspace.Id, guarded.RelativePath, ct).ConfigureAwait(false))
            {
                logger.LogDebug("Removed {Workspace}:{Path} from the code index (file is gone)", workspace.Name, guarded.RelativePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot read {Workspace}:{Path}; the index keeps its earlier version", workspace.Name, guarded.RelativePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One file that an indexer cannot handle must not stop the others, whatever language it is.
            logger.LogError(ex, "Indexing {Workspace}:{Path} failed; the index keeps its earlier version", workspace.Name, guarded.RelativePath);
        }
    }

    private async Task<Step> IndexDiskFileAsync(Workspace workspace, DiskFile file, CodeFileStamp? known, CancellationToken ct)
    {
        if (known is not null
            && known.Size == file.Size
            && known.ModifiedUtc.ToUnixTimeMilliseconds() == file.ModifiedUtc.ToUnixTimeMilliseconds())
        {
            return Step.Skipped;
        }

        if (!languages.TryGet(file.RelativePath, out var indexer))
        {
            return Step.Skipped;
        }

        try
        {
            var result = await indexer.IndexFileAsync(file.AbsolutePath, file.RelativePath, known?.Hash, ct).ConfigureAwait(false);
            return await StoreAsync(workspace, file.RelativePath, result, known is not null, ct).ConfigureAwait(false)
                ? Step.Changed
                : Step.Skipped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot read {Workspace}:{Path}; the index keeps its earlier version", workspace.Name, file.RelativePath);
            return Step.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing {Workspace}:{Path} failed; the index keeps its earlier version", workspace.Name, file.RelativePath);
            return Step.Failed;
        }
    }

    /// <summary>True when the index changed.</summary>
    private async Task<bool> StoreAsync(Workspace workspace, string relativePath, SyntaxIndexResult result, bool wasIndexed, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case SyntaxIndexOutcome.Indexed:
                await store.ReplaceFileAsync(workspace.Id, result.File!, time.GetUtcNow(), ct).ConfigureAwait(false);
                if (result.Truncated)
                {
                    logger.LogWarning("{Workspace}:{Path} exceeds the per-file member or call-site limit; the rest of it is not indexed", workspace.Name, relativePath);
                }

                return true;
            case SyntaxIndexOutcome.TooLarge:
            case SyntaxIndexOutcome.NotText:
                logger.LogDebug("{Workspace}:{Path} is not indexed ({Outcome})", workspace.Name, relativePath, result.Outcome);
                return wasIndexed && await store.RemoveFileAsync(workspace.Id, relativePath, ct).ConfigureAwait(false);
            case SyntaxIndexOutcome.Unavailable:
                logger.LogDebug("{Workspace}:{Path} is not indexed (its language indexer is unavailable)", workspace.Name, relativePath);
                return false;
            default:
                return false;
        }
    }
}
