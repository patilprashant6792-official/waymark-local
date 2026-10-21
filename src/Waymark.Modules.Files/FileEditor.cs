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
    ILogger<FileEditor> logger) : IFileEditor
{
    public async Task<IReadOnlyList<FileWriteResult>> WriteAsync(string? workspaceName, IReadOnlyList<WriteFileEntry> files, CancellationToken ct)
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
        using (await gate.EnterAsync(ct).ConfigureAwait(false))
        {
            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await WriteOneAsync(workspace, entry, ct).ConfigureAwait(false));
            }
        }

        return results;
    }

    public async Task<FileWriteResult> EditLinesAsync(string? workspaceName, string? relativeFilePath, IReadOnlyList<PatchOperation> patches, CancellationToken ct)
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
            return await EditOneAsync(workspace, relativeFilePath, patches, ct).ConfigureAwait(false);
        }
    }

    private async Task<FileWriteResult> EditOneAsync(Workspace workspace, string? relativeFilePath, IReadOnlyList<PatchOperation> patches, CancellationToken ct)
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
            var document = LineDocument.Parse(format.DecodeStrict(original));
            var affected = LinePatcher.Apply(document.Lines, patches);
            var bytes = format.Encode(document.ToText());
            if (bytes.Length > options.MaxWriteBytes)
            {
                return FileWriteResult.Failure(label, $"The edited file would be larger than the limit of {options.MaxWriteBytes} bytes.");
            }

            await ReplaceAtomicallyAsync(target.AbsolutePath, bytes, ct).ConfigureAwait(false);

            logger.LogInformation("file_edit_lines {Workspace}/{Path} ({Patches} patches, {Lines} lines)", workspace.Name, target.RelativePath, patches.Count, document.Lines.Count);
            await events.PublishAsync(new FileChanged(workspace.Id, target.RelativePath, FileChangeKind.Modified)).ConfigureAwait(false);

            return new FileWriteResult(
                target.RelativePath,
                true,
                null,
                document.Lines.Count,
                bytes.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(target.AbsolutePath), TimeSpan.Zero),
                affected);
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

    private async Task<FileWriteResult> WriteOneAsync(Workspace workspace, WriteFileEntry entry, CancellationToken ct)
    {
        var label = entry.RelativeFilePath ?? string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.RelativeFilePath))
            {
                return FileWriteResult.Failure(label, "relativeFilePath is required.");
            }

            if (entry.Content is null)
            {
                return FileWriteResult.Failure(label, "content is required; use an empty string for an empty file.");
            }

            if (Encoding.UTF8.GetByteCount(entry.Content) > options.MaxWriteBytes)
            {
                return FileWriteResult.Failure(label, $"The content is larger than the limit of {options.MaxWriteBytes} bytes.");
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

            Directory.CreateDirectory(Path.GetDirectoryName(target.AbsolutePath)!);
            var format = exists ? TextFileFormat.Detect(target.AbsolutePath) : TextFileFormat.ForNewFile;
            var bytes = format.Encode(entry.Content);
            await ReplaceAtomicallyAsync(target.AbsolutePath, bytes, ct).ConfigureAwait(false);

            logger.LogInformation("file_write {Workspace}/{Path} ({Mode}, {Bytes} bytes)", workspace.Name, target.RelativePath, entry.Mode, bytes.Length);
            await events.PublishAsync(new FileChanged(workspace.Id, target.RelativePath, exists ? FileChangeKind.Modified : FileChangeKind.Created)).ConfigureAwait(false);

            return new FileWriteResult(
                target.RelativePath,
                true,
                null,
                CountLines(entry.Content),
                bytes.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(target.AbsolutePath), TimeSpan.Zero));
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

    // The new content is written next to the target and then moved over it, so a failure never leaves a half-written file.
    private static async Task ReplaceAtomicallyAsync(string absolutePath, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(absolutePath)!, $".waymark-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, absolutePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
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
