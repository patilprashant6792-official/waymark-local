using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Files.Replace;

/// <summary>
/// The writing half of file_replace. Every planned file is read again and must have the hash the preview saw; the new bytes of every file are
/// built and checked in memory; then all files are written together, or none. The caller holds the file write gate.
/// </summary>
internal sealed class ReplaceApplier(IPathGuard guard, IContentInspector inspector, IEventBus events, FilesOptions filesOptions, ILogger<ReplaceApplier> logger)
{
    private sealed record LoadedFile(FilePlan Plan, GuardedPath Path, byte[] Bytes);

    /// <exception cref="ReplaceRefusedException">A file changed since the preview, cannot be read, would add a secret or grow past the size limit, or the write failed (every file was restored).</exception>
    public async Task<IReadOnlyList<AppliedFile>> ApplyAsync(Workspace workspace, IReadOnlyList<FilePlan> files, string replacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(replacement);

        var loaded = await LoadUnchangedAsync(workspace, files, ct).ConfigureAwait(false);
        var pending = new List<PendingFile>(loaded.Count);
        var applied = new List<AppliedFile>(loaded.Count);
        foreach (var file in loaded)
        {
            var format = TextFileFormat.Detect(file.Bytes);
            string text;
            try
            {
                text = format.DecodeStrict(file.Bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new ReplaceRefusedException($"{file.Plan.RelativePath} is no longer valid text. Nothing was written.");
            }

            var ranges = file.Plan.Spans.Select(span => new TextRange(span.Start, span.Length)).ToList();
            var updated = SpanApplier.Apply(text, ranges, replacement);
            await RejectNewSecretsAsync(text, file.Plan, replacement, ct).ConfigureAwait(false);

            var bytes = format.EncodeExact(updated);
            if (bytes.Length > filesOptions.MaxWriteBytes)
            {
                throw new ReplaceRefusedException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{file.Plan.RelativePath} would grow past the limit of {filesOptions.MaxWriteBytes} bytes. Nothing was written."));
            }

            pending.Add(new PendingFile(workspace, file.Path, file.Bytes, bytes));
            applied.Add(new AppliedFile(file.Path.RelativePath, ranges.Count));
        }

        try
        {
            // From here on the call timeout must not stop the write halfway: every file is written, or every file is restored.
            await CopyTransaction.CommitAsync(pending, logger, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PatchValidationException ex)
        {
            throw new ReplaceRefusedException(ex.Message);
        }

        foreach (var file in pending)
        {
            await events.PublishAsync(new FileChanged(workspace.Id, file.Path.RelativePath, FileChangeKind.Modified)).ConfigureAwait(false);
        }

        return applied;
    }

    private async Task<List<LoadedFile>> LoadUnchangedAsync(Workspace workspace, IReadOnlyList<FilePlan> files, CancellationToken ct)
    {
        var loaded = new List<LoadedFile>(files.Count);
        var changed = new List<string>();
        foreach (var plan in files)
        {
            GuardedPath path;
            try
            {
                path = guard.Resolve(workspace, plan.RelativePath);
                LinkGuard.EnsureNoLinks(workspace.Path, path.RelativePath);
            }
            catch (FileAccessDeniedException ex)
            {
                throw new ReplaceRefusedException($"{plan.RelativePath}: {ex.Message} Nothing was written.");
            }

            if (!File.Exists(path.AbsolutePath))
            {
                changed.Add($"{plan.RelativePath} (deleted)");
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await FileBytes.ReadAsync(path.AbsolutePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "file_replace could not read {Workspace}/{Path}", workspace.Name, plan.RelativePath);
                throw new ReplaceRefusedException($"{plan.RelativePath} could not be read (it may be open in another program). Nothing was written.");
            }

            if (!string.Equals(FileBytes.Hash(bytes), plan.Sha256, StringComparison.Ordinal))
            {
                changed.Add(plan.RelativePath);
                continue;
            }

            loaded.Add(new LoadedFile(plan, path, bytes));
        }

        if (changed.Count > 0)
        {
            throw new ReplaceRefusedException(
                $"These files changed after the preview, so nothing was written: {string.Join(", ", changed)}. Run the preview again.");
        }

        return loaded;
    }

    // Each changed line is checked before and after; only a secret the replacement adds is refused, so a line that already held one
    // (a test fixture, say) can still be renamed.
    private async Task RejectNewSecretsAsync(string text, FilePlan plan, string replacement, CancellationToken ct)
    {
        var map = new LineMap(text);
        foreach (var group in plan.Spans.GroupBy(span => span.Line))
        {
            var start = map.StartOf(group.Key);
            var before = map.LineText(group.Key);
            var local = group.Select(span => new TextRange(span.Start - start, span.Length)).ToList();
            var after = SpanApplier.Apply(before, local, replacement);
            var found = await inspector.InspectAsync(after, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                continue;
            }

            var existing = await inspector.InspectAsync(before, ct).ConfigureAwait(false);
            var added = found.Where(finding => finding.Count > existing.Where(e => e.Kind == finding.Kind).Sum(e => e.Count)).ToList();
            if (added.Count > 0)
            {
                throw new ReplaceRefusedException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{plan.RelativePath} line {group.Key}: the replacement would add {SensitiveDataScanner.Summarize(added)}. Waymark does not write secrets. Nothing was written."));
            }
        }
    }
}
