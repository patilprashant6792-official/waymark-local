namespace Waymark.Core.Commands;

/// <summary>
/// What the admin UI and the guides can see of terminals, and the only thing the owner can do to one: stop it. Starting, typing and restarting are
/// Claude's, through its tools; the Console is Claude's console, shown to the owner.
/// </summary>
public interface ITerminals
{
    Task<IReadOnlyList<TerminalInfo>> ListAsync(TerminalQuery query, CancellationToken ct);

    Task<TerminalInfo?> GetAsync(long id, CancellationToken ct);

    /// <summary>The end of the terminal's earlier runs and the output of the run now. Null when the terminal does not exist.</summary>
    Task<TerminalOutput?> GetOutputAsync(long id, CancellationToken ct);

    /// <summary>The runs started in a terminal, newest first.</summary>
    Task<IReadOnlyList<CommandRun>> ListRunsAsync(long id, int limit, CancellationToken ct);

    /// <summary>Stops what runs in the terminal (Ctrl+C, then terminate, then kill). False when nothing runs.</summary>
    Task<bool> StopAsync(long id, string actor, CancellationToken ct);

    /// <summary>Stops every running terminal. Returns how many were asked to stop.</summary>
    Task<int> StopAllAsync(string actor, CancellationToken ct);
}
