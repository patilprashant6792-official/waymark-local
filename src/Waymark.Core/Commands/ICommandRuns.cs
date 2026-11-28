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

    /// <summary>
    /// Deletes one finished one-time run with its stored output, on the owner's behalf; the deletion is audited. False when the run does not exist,
    /// is still waiting, queued or running, or belongs to a terminal (a terminal's runs go with the terminal).
    /// </summary>
    Task<bool> DeleteAsync(long runId, CancellationToken ct);

    /// <summary>
    /// Clears the one-time history of one place, a workspace or the computer itself (<paramref name="workspaceId"/> null): every finished run and its
    /// stored output. Active runs and terminals stay. The clearing is audited. Returns how many runs were deleted.
    /// </summary>
    Task<int> ClearHistoryAsync(long? workspaceId, CancellationToken ct);
}
