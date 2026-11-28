namespace Waymark.Core.Commands;

public interface ICommandRunStore
{
    Task<CommandRun> CreateAsync(NewCommandRun run, CancellationToken ct);

    Task<CommandRun?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<CommandRun>> ListAsync(CommandRunQuery query, CancellationToken ct);

    /// <summary>Runs that wait for approval, wait for their turn or run, oldest first.</summary>
    Task<IReadOnlyList<CommandRun>> ListActiveAsync(CancellationToken ct);

    /// <summary>The runs started in one terminal, newest first.</summary>
    Task<IReadOnlyList<CommandRun>> ListByTerminalAsync(long terminalId, int limit, CancellationToken ct);

    /// <summary>False when the run does not exist.</summary>
    Task<bool> UpdateAsync(long id, CommandRunUpdate update, CancellationToken ct);

    /// <summary>At start-up: runs that were waiting or running when Waymark stopped can never finish; they are marked failed. Runs of a terminal are left to <see cref="EndInterruptedTerminalRunsAsync"/>. Returns how many.</summary>
    Task<int> FailInterruptedAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// At start-up, after their programs were ended: terminal runs that were still active are closed as stopped by Waymark. Their output was streamed
    /// to run-{id}.log in <paramref name="runsDirectory"/> as it came, so that file becomes their stored output (read and deleted like any other).
    /// Returns how many.
    /// </summary>
    Task<int> EndInterruptedTerminalRunsAsync(DateTimeOffset now, string runsDirectory, CancellationToken ct);

    /// <summary>Deletes finished runs that ended before the cutoff and returns the output files that belonged to them.</summary>
    Task<IReadOnlyList<string>> DeleteEndedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>
    /// Deletes one finished one-time run (not waiting, queued or running, and in no terminal) and returns it, so its stored output can be removed
    /// and the deletion audited. Null when there is no such run.
    /// </summary>
    Task<CommandRun?> DeleteFinishedAsync(long id, CancellationToken ct);

    /// <summary>
    /// Deletes every finished one-time run of one place, a workspace or the computer itself (<paramref name="workspaceId"/> null), and returns them.
    /// Active runs and the runs of terminals stay.
    /// </summary>
    Task<IReadOnlyList<CommandRun>> DeleteFinishedInAsync(long? workspaceId, CancellationToken ct);
}
