namespace Waymark.Core.Commands;

/// <summary>
/// What the admin UI can do with command runs. Approving is possible only here: the tools Claude calls have no way to approve,
/// and the admin UI listens on this computer only.
/// </summary>
public interface ICommandRuns
{
    Task<IReadOnlyList<CommandRun>> ListAsync(CommandRunQuery query, CancellationToken ct);

    /// <summary>Runs that wait for a decision, oldest first.</summary>
    Task<IReadOnlyList<CommandRun>> ListPendingAsync(CancellationToken ct);

    Task<CommandRun?> GetAsync(long id, CancellationToken ct);

    /// <summary>The output so far (running) or the stored output (finished). Null when the run does not exist.</summary>
    Task<RunOutputSnapshot?> GetOutputAsync(long runId, CancellationToken ct);

    /// <summary>
    /// Approves or denies a run that waits. <paramref name="editedScript"/> replaces the script of a shell run before it starts; recipes cannot be edited.
    /// False when the run no longer waits.
    /// </summary>
    Task<bool> DecideAsync(long runId, bool approve, string? reason, string? editedScript, CancellationToken ct);

    /// <summary>Ends a run that waits, is queued or runs. False when it is not active.</summary>
    Task<bool> StopAsync(long runId, CancellationToken ct);
}
