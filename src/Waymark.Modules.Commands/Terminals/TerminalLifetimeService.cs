using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Audit;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// The terminals around Waymark's own start and stop. At start-up, before anything else of the commands module, a terminal still marked as
/// running belongs to a Waymark that ended without stopping it (a crash, a forced quit): its program may still run and hold its port, so it is ended
/// (only when the process with that id still started at the same time, so another program that got the id later is never touched), the end is
/// written to the audit log, and the terminal shows that Waymark stopped. Nothing is started again by itself. When Waymark stops normally, every
/// terminal program is stopped cleanly first.
/// </summary>
public sealed class TerminalLifetimeService(
    ITerminalStore terminals,
    ICommandRunStore runs,
    TerminalCoordinator coordinator,
    CommandCoordinator commands,
    IEventBus events,
    IAuditStore audit,
    CommandsOptions options,
    TimeProvider time,
    ILogger<TerminalLifetimeService> logger) : IHostedService
{
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(12);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = coordinator; // Created now, so it follows workspace changes from the start.
        try
        {
            var live = await terminals.ListAsync(new TerminalQuery(LiveOnly: true), cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            foreach (var terminal in live)
            {
                var ended = false;
                if (terminal.Pid is { } pid && ProcessTree.IsSameProcess(pid, terminal.PidStartUtc))
                {
                    ProcessTree.KillTree(pid);
                    ended = true;
                    logger.LogWarning("Ended the program of terminal {Terminal} (process {Pid}) that a previous Waymark left running.", terminal.Name, pid);
                    await audit.AppendAsync(
                        AuditEntries.Create(time, AuditCategory.Tool, "terminal.cleanup", TerminalActors.Waymark, terminal.Scope, AuditOutcome.Ok,
                            $"terminal {terminal.Name}: ended process {pid} left running by the previous Waymark ({terminal.CommandLine})"),
                        cancellationToken).ConfigureAwait(false);
                }

                await terminals.UpdateAsync(terminal.Id, new TerminalUpdate
                {
                    State = TerminalState.WaymarkStopped,
                    ClearCurrentRun = true,
                    EndedUtc = now,
                    StoppedBy = TerminalActors.Waymark,
                    StateNote = ended
                        ? "Ended: Waymark stopped while it ran, so Waymark ended the program it left behind."
                        : "Ended: Waymark stopped while it ran.",
                }, cancellationToken).ConfigureAwait(false);
                await events.PublishAsync(new TerminalChanged(terminal.Id, terminal.WorkspaceId, TerminalState.WaymarkStopped)).ConfigureAwait(false);
            }

            var closed = await runs.EndInterruptedTerminalRunsAsync(now, options.RunsDirectory, cancellationToken).ConfigureAwait(false);
            if (live.Count > 0 || closed > 0)
            {
                logger.LogWarning("{Terminals} terminal(s) and {Runs} terminal run(s) were left open by the last shutdown and are now closed.", live.Count, closed);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down while starting.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The terminals left open by the last shutdown could not be closed.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var running = commands.ActiveTerminalRuns();
            if (running.Count == 0)
            {
                return;
            }

            logger.LogInformation("Stopping {Count} terminal program(s) before Waymark stops.", running.Count);
            await commands.StopTerminalsAsync(null, allScopes: true, TerminalActors.Waymark, "Ended: Waymark stopped.", CancellationToken.None).ConfigureAwait(false);
            await commands.WaitEndedAsync(running, StopBudget).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The terminal programs could not all be stopped before Waymark stopped.");
        }
    }
}
