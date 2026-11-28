namespace Waymark.Core.Commands;

public interface ITerminalStore
{
    Task<Terminal> CreateAsync(NewTerminal terminal, CancellationToken ct);

    Task<Terminal?> GetAsync(long id, CancellationToken ct);

    /// <summary>Names are unique per workspace and among the system terminals; compared as stored (lower case).</summary>
    Task<Terminal?> GetByNameAsync(long? workspaceId, string name, CancellationToken ct);

    Task<IReadOnlyList<Terminal>> ListAsync(TerminalQuery query, CancellationToken ct);

    /// <summary>False when the terminal does not exist.</summary>
    Task<bool> UpdateAsync(long id, TerminalUpdate update, CancellationToken ct);

    Task<bool> DeleteAsync(long id, CancellationToken ct);

    /// <summary>Forgets the approvals kept for the terminals of one workspace (0 for the system terminals). Returns how many were forgotten.</summary>
    Task<int> ClearApprovalsAsync(long workspaceId, CancellationToken ct);

    /// <summary>Removes terminals that run nothing and were not used since the cutoff. Returns how many.</summary>
    Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);
}
