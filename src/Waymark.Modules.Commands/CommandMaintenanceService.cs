using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Commands;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands;

/// <summary>
/// At start-up: runs that were waiting or running when Waymark stopped can never finish, so they are closed as failed (runs of a terminal are closed
/// by <see cref="Terminals.TerminalLifetimeService"/>, which ends their programs first). Then, every few hours, finished runs older than the retention
/// time are removed together with their stored output (the rotated files of a terminal's log included), and so are terminals nobody used since.
/// </summary>
public sealed class CommandMaintenanceService(
    ICommandRunStore runs,
    ITerminalStore terminals,
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
            foreach (var file in files)
            {
                RunOutputFiles.Delete(file, options.RunsDirectory, options.TerminalLogFiles, logger);
            }

            var removed = await terminals.DeleteUnusedBeforeAsync(cutoff, stop).ConfigureAwait(false);
            if (removed > 0)
            {
                logger.LogInformation("{Count} terminal(s) unused for {Days} days were removed.", removed, options.RetentionDays);
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
}
