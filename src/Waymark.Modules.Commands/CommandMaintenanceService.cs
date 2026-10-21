using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Commands;

namespace Waymark.Modules.Commands;

/// <summary>
/// At start-up: runs that were waiting or running when Waymark stopped can never finish, so they are closed as failed.
/// Then, every few hours, finished runs older than the retention time are removed together with their stored output.
/// </summary>
public sealed class CommandMaintenanceService(
    ICommandRunStore runs,
    CommandsOptions options,
    TimeProvider time,
    ILogger<CommandMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var interrupted = await runs.FailInterruptedAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            if (interrupted > 0)
            {
                logger.LogWarning("{Count} command run(s) were interrupted by the last shutdown and were closed as failed.", interrupted);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Interrupted command runs could not be closed.");
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PruneAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stop)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PruneAsync(CancellationToken stop)
    {
        try
        {
            var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.RetentionDays);
            var files = await runs.DeleteEndedBeforeAsync(cutoff, stop).ConfigureAwait(false);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RunsDirectory)) + Path.DirectorySeparatorChar;
            foreach (var file in files)
            {
                DeleteStoredOutput(file, root);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Old command runs could not be removed.");
        }
    }

    // Only files inside the runs folder are ever deleted, whatever the stored path says.
    private void DeleteStoredOutput(string file, string runsRoot)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Path.GetFullPath(file).StartsWith(runsRoot, comparison) && File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The stored output '{File}' could not be deleted.", file);
        }
    }
}
