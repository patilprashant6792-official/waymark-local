using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// Brings the code index of one workspace in line with the disk: a reconcile of the whole folder or of one folder inside it, or a list of files.
/// All three run the same pipeline: files are read and parsed by several workers at once, and one writer stores the results in batches,
/// one transaction per batch, so parsing and storing overlap.
/// </summary>
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
    private const int GoneChunkSize = 500;
    private const int MaxBatchFiles = 5_000;

    private enum ParsedKind
    {
        Skipped,
        Indexed,
        Remove,
        Failed,
    }

    private readonly record struct WorkItem(string RelativePath, string AbsolutePath, DateTimeOffset? ModifiedUtc, string? KnownHash, bool WasIndexed);

    private readonly record struct ParsedFile(string RelativePath, ParsedKind Kind, CodeFileIndex? Entry, bool Truncated);

    private readonly record struct WriteStats(int Processed, int Changed, int Failed, long WriteTicks);

    private readonly record struct PipelineResult(int Processed, int Changed, int Failed, long ParseTicks, long WriteTicks);

    private int BatchFiles => Math.Min(options.WriteBatchFiles, MaxBatchFiles);

    public Task ReconcileAsync(Workspace workspace, bool rebuild, CancellationToken ct) =>
        ReconcileAsync(workspace, rebuild, null, ct);

    /// <summary>
    /// Parses only files whose size or modified time differs from the index (the content hash then decides whether the
    /// parse is needed), stores them in batches, and removes indexed files that are gone. With a folder, only that folder is walked and compared.
    /// Progress goes to the event bus. A failure is logged and reported as a Failed event; cancellation propagates.
    /// </summary>
    public async Task ReconcileAsync(Workspace workspace, bool rebuild, string? folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var scope = NormalizeFolder(folder);
        var clock = Stopwatch.StartNew();
        await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Started, 0, 0, 0, 0)).ConfigureAwait(false);
        var total = 0;
        var processed = 0;
        var changed = 0;
        var failed = 0;
        try
        {
            if (rebuild && scope is null)
            {
                await store.ClearWorkspaceAsync(workspace.Id, ct).ConfigureAwait(false);
            }

            var known = scope is null
                ? await store.ListStampsAsync(workspace.Id, ct).ConfigureAwait(false)
                : await store.ListStampsUnderAsync(workspace.Id, scope, ct).ConfigureAwait(false);
            var stamps = known.ToDictionary(stamp => stamp.RelativePath, StringComparer.OrdinalIgnoreCase);
            var (files, truncated) = await Task.Run(
                () => CodeFileWalker.Enumerate(workspace.Path, scope, policy, options.MaxFilesPerWorkspace, languages, ct), ct).ConfigureAwait(false);
            var walkMs = clock.ElapsedMilliseconds;
            if (truncated)
            {
                logger.LogWarning(
                    "Workspace {Workspace} has more than {Max} source files; the rest are not indexed (Waymark:Code:MaxFilesPerWorkspace).",
                    workspace.Name,
                    options.MaxFilesPerWorkspace);
            }

            total = files.Count;
            var work = new List<WorkItem>();
            foreach (var file in files)
            {
                stamps.Remove(file.RelativePath, out var stamp);
                if (stamp is not null && IsCurrent(stamp, file))
                {
                    processed++;
                    continue;
                }

                work.Add(new WorkItem(file.RelativePath, file.AbsolutePath, file.ModifiedUtc, stamp?.Hash, stamp is not null));
            }

            var run = await RunPipelineAsync(workspace, work, total, processed, publishProgress: true, ct).ConfigureAwait(false);
            processed = run.Processed;
            changed = run.Changed;
            failed = run.Failed;

            // A truncated walk did not see every file, so a missing path proves nothing.
            if (!truncated && stamps.Count > 0)
            {
                foreach (var chunk in stamps.Keys.Chunk(GoneChunkSize))
                {
                    var removal = await store.ApplyBatchAsync(workspace.Id, [], chunk, time.GetUtcNow(), ct).ConfigureAwait(false);
                    changed += removal.Removed;
                }
            }

            await workspaces.MarkIndexedAsync(workspace.Id, time.GetUtcNow(), ct).ConfigureAwait(false);
            await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Completed, total, processed, changed, failed)).ConfigureAwait(false);
            logger.LogInformation(
                "Indexed workspace {Workspace}{Scope}: {Total} files, {Changed} updated, {Failed} unreadable in {Elapsed} ms (walk {Walk} ms, read and parse {Parse} ms summed over {Workers} workers, write {Write} ms)",
                workspace.Name,
                scope is null ? string.Empty : $" (folder {scope})",
                total,
                changed,
                failed,
                clock.ElapsedMilliseconds,
                walkMs,
                (long)Stopwatch.GetElapsedTime(0, run.ParseTicks).TotalMilliseconds,
                options.EffectiveParallelism,
                (long)Stopwatch.GetElapsedTime(0, run.WriteTicks).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Indexing workspace {Workspace} failed", workspace.Name);
            await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Failed, total, processed, changed, failed)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-reads the given files together: removed from the index when they are gone or refused by the policy, left alone when they are not source files;
    /// otherwise parsed again when their content changed. Their stamps are read in one query, and the results are stored in as few transactions as possible.
    /// </summary>
    public async Task IndexFilesAsync(Workspace workspace, IReadOnlyCollection<string> relativePaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(relativePaths);
        var refused = new List<string>();
        var resolved = new List<(string Relative, string Absolute)>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            try
            {
                var guarded = pathGuard.Resolve(workspace, relativePath);
                if (languages.Handles(guarded.RelativePath))
                {
                    resolved.Add((guarded.RelativePath, guarded.AbsolutePath));
                }
            }
            catch (FileAccessDeniedException)
            {
                refused.Add(relativePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                logger.LogWarning(ex, "Cannot resolve {Workspace}:{Path}; the index keeps its earlier version", workspace.Name, relativePath);
            }
        }

        if (refused.Count > 0)
        {
            await store.ApplyBatchAsync(workspace.Id, [], refused, time.GetUtcNow(), ct).ConfigureAwait(false);
        }

        if (resolved.Count == 0)
        {
            return;
        }

        var stamps = await store.GetStampsAsync(workspace.Id, resolved.Select(path => path.Relative).ToList(), ct).ConfigureAwait(false);
        var work = new List<WorkItem>(resolved.Count);
        foreach (var (relative, absolute) in resolved)
        {
            stamps.TryGetValue(relative, out var stamp);
            work.Add(new WorkItem(relative, absolute, null, stamp?.Hash, stamp is not null));
        }

        var run = await RunPipelineAsync(workspace, work, work.Count, 0, publishProgress: false, ct).ConfigureAwait(false);
        logger.LogDebug(
            "Re-read {Count} files of {Workspace}: {Changed} updated, {Failed} failed",
            work.Count,
            workspace.Name,
            run.Changed,
            run.Failed);
    }

    private static bool IsCurrent(CodeFileStamp stamp, DiskFile file) =>
        stamp.Size == file.Size && stamp.ModifiedUtc.ToUnixTimeMilliseconds() == file.ModifiedUtc.ToUnixTimeMilliseconds();

    // A folder that tries to leave the workspace, or names no folder at all, is not worth a partial walk: the whole workspace is compared instead.
    private static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var trimmed = folder.Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0 || trimmed == ".")
        {
            return null;
        }

        foreach (var segment in trimmed.Split('/'))
        {
            if (segment is ".." or "." or "")
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Parses the items with several workers and stores the results through one writer. The first failure (a database error, a cancellation)
    /// stops both sides and is thrown; a file that cannot be read or stored is counted as failed and does not stop the others.
    /// </summary>
    private async Task<PipelineResult> RunPipelineAsync(
        Workspace workspace,
        IReadOnlyList<WorkItem> items,
        int total,
        int processedAlready,
        bool publishProgress,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return new PipelineResult(processedAlready, 0, 0, 0, 0);
        }

        var channel = Channel.CreateBounded<ParsedFile>(new BoundedChannelOptions(BatchFiles * 2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long parseTicks = 0;

        var writer = Task.Run(
            async () =>
            {
                try
                {
                    return await WriteLoopAsync(workspace, channel.Reader, total, processedAlready, publishProgress, pipeline.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Without a reader the parsers would wait forever on a full channel.
                    await pipeline.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            },
            CancellationToken.None);

        Exception? failure = null;
        try
        {
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = pipeline.Token },
                async (item, token) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    var parsed = await ParseAsync(workspace, item, token).ConfigureAwait(false);
                    Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - started);
                    await channel.Writer.WriteAsync(parsed, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        channel.Writer.TryComplete(failure);

        WriteStats stats = default;
        try
        {
            stats = await writer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The writer's own failure explains a cancelled parse side; any other first failure is the one to report.
            if (failure is null || failure is OperationCanceledException)
            {
                failure = ex;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new PipelineResult(stats.Processed, stats.Changed, stats.Failed, Interlocked.Read(ref parseTicks), stats.WriteTicks);
    }

    private async Task<ParsedFile> ParseAsync(Workspace workspace, WorkItem item, CancellationToken ct)
    {
        if (!languages.TryGet(item.RelativePath, out var indexer))
        {
            return new ParsedFile(item.RelativePath, ParsedKind.Skipped, null, false);
        }

        try
        {
            var result = await indexer.IndexFileAsync(item.AbsolutePath, item.RelativePath, item.KnownHash, item.ModifiedUtc, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case SyntaxIndexOutcome.Indexed:
                    return new ParsedFile(item.RelativePath, ParsedKind.Indexed, result.File, result.Truncated);
                case SyntaxIndexOutcome.TooLarge:
                case SyntaxIndexOutcome.NotText:
                    logger.LogDebug("{Workspace}:{Path} is not indexed ({Outcome})", workspace.Name, item.RelativePath, result.Outcome);
                    return new ParsedFile(item.RelativePath, item.WasIndexed ? ParsedKind.Remove : ParsedKind.Skipped, null, false);
                case SyntaxIndexOutcome.Unavailable:
                    logger.LogDebug("{Workspace}:{Path} is not indexed (its language indexer is unavailable)", workspace.Name, item.RelativePath);
                    return new ParsedFile(item.RelativePath, ParsedKind.Skipped, null, false);
                default:
                    return new ParsedFile(item.RelativePath, ParsedKind.Skipped, null, false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogDebug("{Workspace}:{Path} is gone from the disk, so it leaves the code index", workspace.Name, item.RelativePath);
            return new ParsedFile(item.RelativePath, item.WasIndexed ? ParsedKind.Remove : ParsedKind.Skipped, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot read {Workspace}:{Path}; the index keeps its earlier version", workspace.Name, item.RelativePath);
            return new ParsedFile(item.RelativePath, ParsedKind.Failed, null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One file that an indexer cannot handle must not stop the others, whatever language it is.
            logger.LogError(ex, "Indexing {Workspace}:{Path} failed; the index keeps its earlier version", workspace.Name, item.RelativePath);
            return new ParsedFile(item.RelativePath, ParsedKind.Failed, null, false);
        }
    }

    /// <summary>
    /// The only reader of the channel. Collects results until the batch is full or has waited long enough, then stores the batch in one transaction.
    /// While parsers keep up the batches fill at once; when files arrive slowly a batch is stored after <c>WriteBatchMs</c>.
    /// </summary>
    private async Task<WriteStats> WriteLoopAsync(
        Workspace workspace,
        ChannelReader<ParsedFile> reader,
        int total,
        int processedAlready,
        bool publishProgress,
        CancellationToken ct)
    {
        var batchFiles = BatchFiles;
        var replace = new List<CodeFileIndex>(batchFiles);
        var remove = new List<string>();
        var processed = processedAlready;
        var reported = processedAlready;
        var changed = 0;
        var failed = 0;
        long writeTicks = 0;
        var completed = false;

        void Take(ParsedFile parsed)
        {
            processed++;
            switch (parsed.Kind)
            {
                case ParsedKind.Indexed:
                    replace.Add(parsed.Entry!);
                    if (parsed.Truncated)
                    {
                        logger.LogWarning("{Workspace}:{Path} exceeds the per-file member or call-site limit; the rest of it is not indexed", workspace.Name, parsed.RelativePath);
                    }

                    break;
                case ParsedKind.Remove:
                    remove.Add(parsed.RelativePath);
                    break;
                case ParsedKind.Failed:
                    failed++;
                    break;
            }
        }

        while (!completed && await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var batchStarted = Stopwatch.GetTimestamp();
            while (replace.Count + remove.Count < batchFiles)
            {
                if (reader.TryRead(out var parsed))
                {
                    Take(parsed);
                    continue;
                }

                var remaining = TimeSpan.FromMilliseconds(options.WriteBatchMs) - Stopwatch.GetElapsedTime(batchStarted);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(remaining);
                try
                {
                    if (!await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                    {
                        completed = true;
                        break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (replace.Count > 0 || remove.Count > 0)
            {
                var writeStarted = Stopwatch.GetTimestamp();
                var batchSize = replace.Count + remove.Count;
                try
                {
                    var result = await store.ApplyBatchAsync(workspace.Id, replace.ToArray(), remove.ToArray(), time.GetUtcNow(), ct).ConfigureAwait(false);
                    changed += result.Replaced + result.Removed;
                    failed += result.Failed.Count;
                    foreach (var failure in result.Failed)
                    {
                        logger.LogError(
                            "Storing {Workspace}:{Path} in the code index failed ({Reason}); the index keeps its earlier version",
                            workspace.Name,
                            failure.RelativePath,
                            failure.Message);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed += batchSize;
                    logger.LogError(ex, "Storing a batch of {Count} files of {Workspace} in the code index failed; the index keeps its earlier versions", batchSize, workspace.Name);
                }

                writeTicks += Stopwatch.GetTimestamp() - writeStarted;
                replace.Clear();
                remove.Clear();
            }

            if (publishProgress && processed - reported >= options.ProgressEveryFiles)
            {
                reported = processed;
                await events.PublishAsync(new CodeIndexProgress(workspace.Id, CodeIndexPhase.Running, total, processed, changed, failed)).ConfigureAwait(false);
            }
        }

        return new WriteStats(processed, changed, failed, writeTicks);
    }
}