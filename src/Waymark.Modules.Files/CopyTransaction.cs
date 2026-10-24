using Microsoft.Extensions.Logging;

namespace Waymark.Modules.Files;

/// <summary>Writes several files so that either all of them change or none does (a move of lines from one file to another).</summary>
internal static class CopyTransaction
{
    /// <summary>Writes the files in order. If a write fails, the files already written get their original bytes back.</summary>
    /// <exception cref="PatchValidationException">A write failed; the message says whether everything was restored.</exception>
    public static async Task CommitAsync(IReadOnlyList<PendingFile> files, ILogger logger, CancellationToken ct)
    {
        var written = new List<PendingFile>(files.Count);
        try
        {
            foreach (var file in files)
            {
                await AtomicFile.ReplaceAsync(file.Path.AbsolutePath, file.Updated, ct).ConfigureAwait(false);
                written.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var failed = Restore(written, logger);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(ex, "A multi-file write failed after {Count} file(s)", written.Count);
            throw new PatchValidationException(failed.Count == 0
                ? "A file could not be written (it may be open in another program or read-only). Every file was restored; nothing changed."
                : $"A file could not be written and these files could not be restored, check them with git: {string.Join(", ", failed)}.");
        }
    }

    private static List<string> Restore(List<PendingFile> written, ILogger logger)
    {
        var failed = new List<string>();
        for (var i = written.Count - 1; i >= 0; i--)
        {
            var file = written[i];
            try
            {
                AtomicFile.ReplaceAsync(file.Path.AbsolutePath, file.Original, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not restore {Path}", file.Path.RelativePath);
                failed.Add(file.Path.RelativePath);
            }
        }

        return failed;
    }
}
