using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files;

internal sealed class FileEditor(
    IPathGuard guard,
    IEventBus events,
    FileWriteGate gate,
    FilesOptions options,
    CopySourceReader copyReader,
    CopyResolver copyResolver,
    FolderCopyPlanner folderPlanner,
    ILogger<FileEditor> logger) : IFileEditor
{
    public async Task<IReadOnlyList<FileWriteResult>> WriteAsync(string? workspaceName, IReadOnlyList<WriteFileEntry> files, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("Give at least one file to write.", nameof(files));
        }

        if (files.Count > options.MaxBatchEntries)
        {
            throw new ArgumentException($"At most {options.MaxBatchEntries} files can be written in one call.", nameof(files));
        }

        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        var results = new List<FileWriteResult>(files.Count);
        var session = new CopySession();
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FromFolder is not null)
                {
                    results.AddRange(await FolderCopyAsync(workspace, entry, session, dryRun, ct).ConfigureAwait(false));
                    continue;
                }

                results.Add(await WriteOneAsync(workspace, entry, session, dryRun, ct).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<FileWriteResult> EditLinesAsync(string? workspaceName, string? relativeFilePath, IReadOnlyList<PatchOperation> patches, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Count == 0)
        {
            throw new ArgumentException("Give at least one patch operation.", nameof(patches));
        }

        if (patches.Count > options.MaxPatchesPerCall)
        {
            throw new ArgumentException($"At most {options.MaxPatchesPerCall} patch operations can be applied in one call.", nameof(patches));
        }

        var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            return await EditOneAsync(workspace, relativeFilePath, patches, dryRun, ct).ConfigureAwait(false);
        }
    }

    private async Task<FileWriteResult> EditOneAsync(Workspace workspace, string? relativeFilePath, IReadOnlyList<PatchOperation> patches, bool dryRun, CancellationToken ct)
    {
        var label = relativeFilePath ?? string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath))
            {
                return FileWriteResult.Failure(label, "relativeFilePath is required.");
            }

            var target = guard.Resolve(workspace, relativeFilePath);
            if (target.RelativePath.Length == 0 || Directory.Exists(target.AbsolutePath))
            {
                return FileWriteResult.Failure(label, "The path is a folder; give a file path.");
            }

            LinkGuard.EnsureNoLinks(workspace.Path, target.RelativePath);
            var info = new FileInfo(target.AbsolutePath);
            if (!info.Exists)
            {
                return FileWriteResult.Failure(label, "The file does not exist. Use file_write to create it.");
            }

            if (info.Length > options.MaxWriteBytes)
            {
                return FileWriteResult.Failure(label, $"The file is larger than the limit of {options.MaxWriteBytes} bytes.");
            }

            var original = await File.ReadAllBytesAsync(target.AbsolutePath, ct).ConfigureAwait(false);
            var format = TextFileFormat.Detect(original);
            var text = format.DecodeStrict(original);
            var document = LineDocument.Parse(text);
            var session = new CopySession();
            CopySourceReader.Seed(session, workspace, target, original, format, text);
            var plan = await copyResolver.ResolveAsync(session, workspace, target, patches, ct).ConfigureAwait(false);
            var affected = LinePatcher.Apply(document.Lines, plan.Patches);
            var bytes = format.Encode(document.ToText());
            if (bytes.Length > options.MaxWriteBytes)
            {
                return FileWriteResult.Failure(label, $"The edited file would be larger than the limit of {options.MaxWriteBytes} bytes.");
            }

            var note = plan.Notes.Count == 0 ? null : string.Join("; ", plan.Notes);
            if (dryRun)
            {
                return new FileWriteResult(target.RelativePath, true, null, document.Lines.Count, bytes.Length, null, affected, note);
            }

            await CommitEditAsync(workspace, target, original, bytes, plan, ct).ConfigureAwait(false);
            logger.LogInformation("file_edit_lines {Workspace}/{Path} ({Patches} patches, {Lines} lines, {Cuts} other file(s) cut)", workspace.Name, target.RelativePath, patches.Count, document.Lines.Count, plan.Cuts.Count);
            return new FileWriteResult(
                target.RelativePath,
                true,
                null,
                document.Lines.Count,
                bytes.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(target.AbsolutePath), TimeSpan.Zero),
                affected,
                note);
        }
        catch (PatchValidationException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (FileAccessDeniedException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (DecoderFallbackException)
        {
            return FileWriteResult.Failure(label, "The file is not valid text in its encoding (it may be binary); Waymark will not edit it.");
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "file_edit_lines refused by the operating system: {Workspace}/{Path}", workspace.Name, label);
            return FileWriteResult.Failure(label, "The operating system refused access (read-only file or missing permission).");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "file_edit_lines failed: {Workspace}/{Path}", workspace.Name, label);
            return FileWriteResult.Failure(label, "The file could not be edited. It may be open in another program.");
        }
    }

    // The edited file is written first, then the files that lost lines; if any write fails, all of them are restored.
    private async Task CommitEditAsync(Workspace workspace, GuardedPath target, byte[] original, byte[] bytes, ResolvedEdit plan, CancellationToken ct)
    {
        if (plan.Cuts.Count == 0)
        {
            await AtomicFile.ReplaceAsync(target.AbsolutePath, bytes, ct).ConfigureAwait(false);
        }
        else
        {
            var files = new List<PendingFile>(plan.Cuts.Count + 1) { new(workspace, target, original, bytes) };
            files.AddRange(plan.Cuts);
            await CopyTransaction.CommitAsync(files, logger, ct).ConfigureAwait(false);
        }

        await events.PublishAsync(new FileChanged(workspace.Id, target.RelativePath, FileChangeKind.Modified)).ConfigureAwait(false);
        foreach (var cut in plan.Cuts)
        {
            await events.PublishAsync(new FileChanged(cut.Workspace.Id, cut.Path.RelativePath, FileChangeKind.Modified)).ConfigureAwait(false);
        }
    }

    private async Task<FileWriteResult> WriteOneAsync(Workspace workspace, WriteFileEntry entry, CopySession session, bool dryRun, CancellationToken ct)
    {
        var label = entry.RelativeFilePath ?? string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.RelativeFilePath))
            {
                return FileWriteResult.Failure(label, "relativeFilePath is required.");
            }

            var early = ValidateSourceChoice(entry, label);
            if (early is not null)
            {
                return early;
            }

            var target = guard.Resolve(workspace, entry.RelativeFilePath);
            if (target.RelativePath.Length == 0 || Directory.Exists(target.AbsolutePath))
            {
                return FileWriteResult.Failure(label, "The path is a folder; give a file path.");
            }

            LinkGuard.EnsureNoLinks(workspace.Path, target.RelativePath);
            var exists = File.Exists(target.AbsolutePath);
            if (entry.Mode == WriteMode.Create && exists)
            {
                return FileWriteResult.Failure(label, "The file already exists. Use mode overwrite or upsert.");
            }

            if (entry.Mode == WriteMode.Overwrite && !exists)
            {
                return FileWriteResult.Failure(label, "The file does not exist. Use mode create or upsert.");
            }

            var content = entry.Content;
            TextFileFormat? sourceFormat = null;
            string? note = null;
            if (entry.From is not null)
            {
                var piece = await copyReader.ReadAsync(session, workspace, null, entry.From, keepWholeFileExact: true, ct).ConfigureAwait(false);
                content = piece.Text;
                sourceFormat = piece.Source.Format;
                note = piece.Summary;
            }

            if (Encoding.UTF8.GetByteCount(content!) > options.MaxWriteBytes)
            {
                return FileWriteResult.Failure(label, $"The content is larger than the limit of {options.MaxWriteBytes} bytes.");
            }

            // A copy keeps the encoding, byte order mark and line endings of its source; typed content on a new file is UTF-8.
            var format = exists ? TextFileFormat.Detect(target.AbsolutePath) : sourceFormat ?? TextFileFormat.ForNewFile;
            var bytes = format.Encode(content!);
            if (dryRun)
            {
                return new FileWriteResult(target.RelativePath, true, null, CountLines(content!), bytes.Length, null, Note: note);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target.AbsolutePath)!);
            await AtomicFile.ReplaceAsync(target.AbsolutePath, bytes, ct).ConfigureAwait(false);
            session.Snapshots.Remove(target.AbsolutePath);

            logger.LogInformation("file_write {Workspace}/{Path} ({Mode}, {Bytes} bytes)", workspace.Name, target.RelativePath, entry.Mode, bytes.Length);
            await events.PublishAsync(new FileChanged(workspace.Id, target.RelativePath, exists ? FileChangeKind.Modified : FileChangeKind.Created)).ConfigureAwait(false);

            return new FileWriteResult(
                target.RelativePath,
                true,
                null,
                CountLines(content!),
                bytes.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(target.AbsolutePath), TimeSpan.Zero),
                Note: note);
        }
        catch (PatchValidationException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (FileAccessDeniedException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FileWriteResult.Failure(label, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "file_write refused by the operating system: {Workspace}/{Path}", workspace.Name, label);
            return FileWriteResult.Failure(label, "The operating system refused access (read-only file or missing permission).");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "file_write failed: {Workspace}/{Path}", workspace.Name, label);
            return FileWriteResult.Failure(label, "The file could not be written. It may be open in another program.");
        }
    }

    // Plans the whole folder first (nothing is read yet), checks the caller's confirmCount, then copies file by file through the same path as a single copy.
    // The first result describes the copy as a whole; each file after it succeeds or fails on its own.
    private async Task<List<FileWriteResult>> FolderCopyAsync(Workspace workspace, WriteFileEntry entry, CopySession session, bool dryRun, CancellationToken ct)
    {
        var copy = entry.FromFolder!;
        var label = string.IsNullOrWhiteSpace(copy.Folder) ? "fromFolder" : $"{copy.Folder.Trim().TrimEnd('/', '\\')}/**";
        try
        {
            if (entry.Content is not null || entry.From is not null || !string.IsNullOrWhiteSpace(entry.RelativeFilePath))
            {
                return [FileWriteResult.Failure(label, "fromFolder cannot be combined with relativeFilePath, content or from.")];
            }

            var plan = await folderPlanner.PlanAsync(workspace, copy, ct).ConfigureAwait(false);
            if (!dryRun && copy.ConfirmCount != plan.Items.Count)
            {
                var given = copy.ConfirmCount?.ToString(CultureInfo.InvariantCulture) ?? "none";
                return [FileWriteResult.Failure(label, $"confirmCount must equal the number of files to copy, which is {plan.Items.Count} (given: {given}). Run with dryRun=true first and pass its count. Nothing was copied.")];
            }

            var from = plan.SourceFolder.Length == 0 ? "." : plan.SourceFolder;
            var to = plan.TargetFolder.Length == 0 ? "." : plan.TargetFolder;
            var results = new List<FileWriteResult>(plan.Items.Count + 1)
            {
                new(label, true, null, null, null, null, Note: $"{plan.Items.Count} file(s) from {plan.SourceWorkspace.Name}:{from} to {to}; {plan.SkippedItems} blocked or linked item(s) left out; confirmCount={plan.Items.Count}"),
            };
            foreach (var item in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                var source = new CopySource(copy.Workspace, item.SourceFile, Replace: copy.Replace, Csharp: copy.Csharp, Indent: copy.Indent);
                results.Add(await WriteOneAsync(workspace, new WriteFileEntry(item.TargetFile, null, copy.Mode, source), session, dryRun, ct).ConfigureAwait(false));
                session.Snapshots.Clear();
            }

            return results;
        }
        catch (PatchValidationException ex)
        {
            return [FileWriteResult.Failure(label, ex.Message)];
        }
        catch (FileAccessDeniedException ex)
        {
            return [FileWriteResult.Failure(label, ex.Message)];
        }
    }
    // Exactly one source of text: content or from.
    private static FileWriteResult? ValidateSourceChoice(WriteFileEntry entry, string label)
    {
        if (entry.From is null)
        {
            return entry.Content is null
                ? FileWriteResult.Failure(label, "content is required (or from, to copy a file); use an empty string for an empty file.")
                : null;
        }

        if (entry.Content is not null)
        {
            return FileWriteResult.Failure(label, "Give content or from, not both.");
        }

        return entry.From.Remove
            ? FileWriteResult.Failure(label, "from.remove works only with file_edit_lines; use file_move to move a whole file.")
            : null;
    }

    // A trailing line break does not start another line.
    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        var breaks = content.Count(c => c == '\n');
        return content[^1] == '\n' ? breaks : breaks + 1;
    }
}
