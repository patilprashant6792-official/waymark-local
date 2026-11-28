using Microsoft.Extensions.Logging;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// The files that hold a run's stored output: the output file itself and, for a terminal run, its rotated files and line index. Deleting them is
/// shared by the retention clean-up and by the owner deleting history. Only files inside the runs folder are ever deleted, whatever a stored path
/// says, so a damaged or edited database row cannot point the delete elsewhere.
/// </summary>
public static class RunOutputFiles
{
    /// <summary>Deletes the stored output of one run. A file that is gone is skipped; one that cannot be deleted is logged and left. Returns how many were deleted.</summary>
    public static int Delete(string outputPath, string runsDirectory, int terminalLogFiles, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runsDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runsDirectory)) + Path.DirectorySeparatorChar;
        var deleted = 0;
        foreach (var file in TerminalLog.CompanionPaths(outputPath, terminalLogFiles).Prepend(outputPath))
        {
            if (DeleteInside(file, root, logger))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static bool DeleteInside(string file, string runsRoot, ILogger logger)
    {
        try
        {
            var comparison = Waymark.Core.Platform.PathComparisons.Comparison;
            if (!Path.GetFullPath(file).StartsWith(runsRoot, comparison) || !File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "The stored output '{File}' could not be deleted.", file);
            return false;
        }
    }
}
