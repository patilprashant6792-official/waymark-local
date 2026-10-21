namespace Waymark.Core.Commands;

public interface ICommandRunStore
{
    Task<CommandRun> CreateAsync(NewCommandRun run, CancellationToken ct);

    Task<CommandRun?> GetAsync(long id, CancellationToken ct);

    Task<IReadOnlyList<CommandRun>> ListAsync(CommandRunQuery query, CancellationToken ct);

    /// <summary>Runs that wait for approval, wait for their turn or run, oldest first.</summary>
    Task<IReadOnlyList<CommandRun>> ListActiveAsync(CancellationToken ct);

    /// <summary>False when the run does not exist.</summary>
    Task<bool> UpdateAsync(long id, CommandRunUpdate update, CancellationToken ct);

    /// <summary>At start-up: runs that were waiting or running when Waymark stopped can never finish; they are marked failed. Returns how many.</summary>
    Task<int> FailInterruptedAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Deletes finished runs that ended before the cutoff and returns the output files that belonged to them.</summary>
    Task<IReadOnlyList<string>> DeleteEndedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);
}
