using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

internal sealed class FileManager(
    IPathGuard guard,
    IEventBus events,
    FileWriteGate gate,
    FilesOptions options,
    ILogger<FileManager> logger) : IFileManager
{
    // A folder path is judged as if a file with this name lived inside it: the policy then checks every folder segment
    // as a folder, and a folder called 'Waymark.Core' is not mistaken for a file with a disallowed extension.
    private const string FolderProbeName = "waymark-folder-probe";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record MovePlan(string Label, GuardedPath Source, GuardedPath Destination);

    private sealed record FolderTarget(string Label, string RelativePath, string AbsolutePath);

    public async Task<IReadOnlyList<FileOperationResult>> CreateFoldersAsync(string? workspaceName, IReadOnlyList<string?> folders, CancellationToken ct)
    {
        EnsureBatchSize(folders.Count, "folders");
        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        var results = new List<FileOperationResult>(folders.Count);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await RunAsync("folder_create", workspace, folder, async () =>
                {
                    var target = ResolveFolder(workspace, folder);
                    if (File.Exists(target.AbsolutePath))
                    {
                        throw new FileOperationException("A file with this name already exists.");
                    }

                    var existed = Directory.Exists(target.AbsolutePath);
                    Directory.CreateDirectory(target.AbsolutePath);
                    if (existed)
                    {
                        return FileOperationResult.Ok(target.Label, "already existed");
                    }

                    logger.LogInformation("folder_create {Workspace}/{Path}", workspace.Name, target.RelativePath);
                    await events.PublishAsync(new FolderChanged(workspace.Id, target.RelativePath, FileChangeKind.Created)).ConfigureAwait(false);
                    return FileOperationResult.Ok(target.Label);
                }).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<FileOperationResult>> DeleteFilesAsync(string? workspaceName, IReadOnlyList<string?> files, CancellationToken ct)
    {
        EnsureBatchSize(files.Count, "files");
        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        var results = new List<FileOperationResult>(files.Count);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await RunAsync("file_delete", workspace, file, async () =>
                {
                    var target = ResolveFile(workspace, file);
                    if (!File.Exists(target.AbsolutePath))
                    {
                        throw new FileOperationException(Directory.Exists(target.AbsolutePath)
                            ? "The path is a folder; use folder_delete."
                            : "File not found.");
                    }

                    File.Delete(target.AbsolutePath);
                    logger.LogInformation("file_delete {Workspace}/{Path}", workspace.Name, target.RelativePath);
                    await events.PublishAsync(new FileChanged(workspace.Id, target.RelativePath, FileChangeKind.Deleted)).ConfigureAwait(false);
                    return FileOperationResult.Ok(file!);
                }).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<FileOperationResult>> MoveFilesAsync(string? workspaceName, IReadOnlyList<MoveEntry> moves, CancellationToken ct)
    {
        EnsureBatchSize(moves.Count, "moves");
        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            var plans = new List<MovePlan>(moves.Count);
            var failures = new List<FileOperationResult>();
            var destinations = new HashSet<string>(PathComparer());
            foreach (var move in moves)
            {
                var label = move?.From ?? string.Empty;
                MovePlan? plan = null;
                var outcome = await RunAsync("file_move", workspace, label, () =>
                {
                    plan = BuildMovePlan(workspace, move, destinations);
                    return Task.FromResult(FileOperationResult.Ok(label));
                }).ConfigureAwait(false);
                if (plan is null)
                {
                    failures.Add(outcome);
                }
                else
                {
                    plans.Add(plan);
                }
            }

            if (failures.Count > 0)
            {
                return AbortBatch(plans, failures);
            }

            var results = new List<FileOperationResult>(plans.Count);
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await RunAsync("file_move", workspace, plan.Label, async () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination.AbsolutePath)!);
                    File.Move(plan.Source.AbsolutePath, plan.Destination.AbsolutePath);
                    logger.LogInformation("file_move {Workspace} {From} -> {To}", workspace.Name, plan.Source.RelativePath, plan.Destination.RelativePath);
                    await events.PublishAsync(new FileChanged(workspace.Id, plan.Source.RelativePath, FileChangeKind.Deleted)).ConfigureAwait(false);
                    await events.PublishAsync(new FileChanged(workspace.Id, plan.Destination.RelativePath, FileChangeKind.Created)).ConfigureAwait(false);
                    return FileOperationResult.Ok(plan.Label, $"moved to {plan.Destination.RelativePath}");
                }).ConfigureAwait(false));
            }

            return results;
        }
    }

    public async Task<FileOperationResult> MoveFolderAsync(string? workspaceName, string? from, string? to, CancellationToken ct)
    {
        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            return await RunAsync("folder_move", workspace, from, async () =>
            {
                var source = ResolveFolder(workspace, from);
                var destination = ResolveFolder(workspace, to);
                if (!Directory.Exists(source.AbsolutePath))
                {
                    throw new FileOperationException($"Source folder not found: {from}");
                }

                var caseOnly = !string.Equals(source.AbsolutePath, destination.AbsolutePath, StringComparison.Ordinal)
                    && string.Equals(source.AbsolutePath, destination.AbsolutePath, PathComparison);
                if (string.Equals(source.AbsolutePath, destination.AbsolutePath, StringComparison.Ordinal))
                {
                    throw new FileOperationException("The source and the destination are the same folder.");
                }

                if (!caseOnly && (Directory.Exists(destination.AbsolutePath) || File.Exists(destination.AbsolutePath)))
                {
                    throw new FileOperationException($"Destination already exists: {to}");
                }

                if (destination.AbsolutePath.StartsWith(source.AbsolutePath + Path.DirectorySeparatorChar, PathComparison))
                {
                    throw new FileOperationException("A folder cannot be moved into itself.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination.AbsolutePath)!);
                Directory.Move(source.AbsolutePath, destination.AbsolutePath);
                logger.LogInformation("folder_move {Workspace} {From} -> {To}", workspace.Name, source.RelativePath, destination.RelativePath);
                await events.PublishAsync(new FolderChanged(workspace.Id, source.RelativePath, FileChangeKind.Deleted)).ConfigureAwait(false);
                await events.PublishAsync(new FolderChanged(workspace.Id, destination.RelativePath, FileChangeKind.Created)).ConfigureAwait(false);
                return FileOperationResult.Ok(source.Label, $"moved to {destination.RelativePath}");
            }).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<FileOperationResult>> DeleteFoldersAsync(string? workspaceName, IReadOnlyList<string?> folders, CancellationToken ct)
    {
        EnsureBatchSize(folders.Count, "folders");
        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        var results = new List<FileOperationResult>(folders.Count);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            var targets = new List<FolderTarget>(folders.Count);
            foreach (var folder in folders)
            {
                FolderTarget? target = null;
                var outcome = await RunAsync("folder_delete", workspace, folder, () =>
                {
                    target = ResolveFolder(workspace, folder);
                    return Task.FromResult(FileOperationResult.Ok(folder!));
                }).ConfigureAwait(false);
                if (target is null)
                {
                    results.Add(outcome);
                }
                else
                {
                    targets.Add(target);
                }
            }

            // Deepest first, so a child listed together with its parent is deleted before the parent goes.
            foreach (var target in targets.OrderByDescending(t => t.RelativePath.Count(c => c == '/')))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await RunAsync("folder_delete", workspace, target.Label, () => DeleteFolderAsync(workspace, target)).ConfigureAwait(false));
            }
        }

        return results;
    }

    private async Task<FileOperationResult> DeleteFolderAsync(Workspace workspace, FolderTarget target)
    {
        if (!Directory.Exists(target.AbsolutePath))
        {
            return File.Exists(target.AbsolutePath)
                ? throw new FileOperationException("The path is a file; use file_delete.")
                : FileOperationResult.Ok(target.Label, "already gone");
        }

        var blocker = FolderTreeScanner.FindDeleteBlocker(target.AbsolutePath, options.MaxFolderEntries);
        if (blocker is not null)
        {
            throw new FileOperationException(blocker);
        }

        Directory.Delete(target.AbsolutePath, recursive: true);
        logger.LogInformation("folder_delete {Workspace}/{Path}", workspace.Name, target.RelativePath);
        await events.PublishAsync(new FolderChanged(workspace.Id, target.RelativePath, FileChangeKind.Deleted)).ConfigureAwait(false);
        return FileOperationResult.Ok(target.Label);
    }

    private MovePlan BuildMovePlan(Workspace workspace, MoveEntry? move, HashSet<string> destinations)
    {
        if (move is null)
        {
            throw new FileOperationException("The entry is empty; give from and to.");
        }

        var source = ResolveFile(workspace, move.From);
        var destination = ResolveFile(workspace, move.To);
        if (!File.Exists(source.AbsolutePath))
        {
            throw new FileOperationException(Directory.Exists(source.AbsolutePath)
                ? "The source is a folder; use folder_move."
                : "Source file not found.");
        }

        if (string.Equals(source.AbsolutePath, destination.AbsolutePath, StringComparison.Ordinal))
        {
            throw new FileOperationException("The source and the destination are the same file.");
        }

        var caseOnly = string.Equals(source.AbsolutePath, destination.AbsolutePath, PathComparison);
        if (!caseOnly && (File.Exists(destination.AbsolutePath) || Directory.Exists(destination.AbsolutePath)))
        {
            throw new FileOperationException($"Destination already exists: {move.To}");
        }

        if (!destinations.Add(destination.AbsolutePath))
        {
            throw new FileOperationException($"Another entry in this batch moves a file to the same destination: {move.To}");
        }

        return new MovePlan(move.From!, source, destination);
    }

    // Nothing was moved: entries that were fine are reported as skipped so the caller sees why the whole batch stopped.
    private static IReadOnlyList<FileOperationResult> AbortBatch(List<MovePlan> plans, List<FileOperationResult> failures)
    {
        var skipped = plans.Select(p => FileOperationResult.Failure(p.Label, "Not moved: another entry in this batch failed validation."));
        return [.. failures, .. skipped];
    }

    private async Task<FileOperationResult> RunAsync(string tool, Workspace workspace, string? label, Func<Task<FileOperationResult>> action)
    {
        var shown = label ?? string.Empty;
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (FileOperationException ex)
        {
            return FileOperationResult.Failure(shown, ex.Message);
        }
        catch (FileAccessDeniedException ex)
        {
            return FileOperationResult.Failure(shown, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FileOperationResult.Failure(shown, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "{Tool} refused by the operating system: {Workspace}/{Path}", tool, workspace.Name, shown);
            return FileOperationResult.Failure(shown, "The operating system refused access (read-only item or missing permission).");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "{Tool} failed: {Workspace}/{Path}", tool, workspace.Name, shown);
            return FileOperationResult.Failure(shown, "The operation failed. A file in it may be open in another program.");
        }
    }

    private GuardedPath ResolveFile(Workspace workspace, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileOperationException("A path is required.");
        }

        var target = guard.Resolve(workspace, path);
        if (target.RelativePath.Length == 0)
        {
            throw new FileOperationException("The path is the workspace folder; give a file path.");
        }

        LinkGuard.EnsureNoLinks(workspace.Path, target.RelativePath);
        return target;
    }

    private FolderTarget ResolveFolder(Workspace workspace, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileOperationException("A folder path is required.");
        }

        var probe = guard.Resolve(workspace, $"{path.Trim().TrimEnd('/', '\\')}/{FolderProbeName}");
        var cut = probe.RelativePath.LastIndexOf('/');
        if (cut < 0)
        {
            throw new FileOperationException("The path is the workspace folder; give a subfolder.");
        }

        var relative = probe.RelativePath[..cut];
        LinkGuard.EnsureNoLinks(workspace.Path, relative);
        return new FolderTarget(path, relative, Path.GetDirectoryName(probe.AbsolutePath)!);
    }

    private void EnsureBatchSize(int count, string what)
    {
        if (count == 0)
        {
            throw new ArgumentException($"Give at least one entry in {what}.", what);
        }

        if (count > options.MaxBatchEntries)
        {
            throw new ArgumentException($"At most {options.MaxBatchEntries} entries can be handled in one call.", what);
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
