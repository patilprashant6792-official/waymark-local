namespace Waymark.Core.Commands;

/// <summary>An owner's choice for one operation. <see cref="WorkspaceId"/> is <see cref="CommandCatalog.AllWorkspaces"/> for every workspace.</summary>
public sealed record CommandGateSetting(long WorkspaceId, string Operation, CommandGate Gate, DateTimeOffset UpdatedUtc);

/// <summary>
/// The build files of a workspace as they were when the owner last approved a command (or an automatic command ran on them).
/// <see cref="FingerprintJson"/> maps each file to its size and time stamp.
/// </summary>
public sealed record CommandTrust(long WorkspaceId, string FingerprintJson, DateTimeOffset ApprovedUtc);

/// <summary>The owner's gate choices and the trusted build-file baseline of each workspace.</summary>
public interface ICommandPolicyStore
{
    Task<IReadOnlyList<CommandGateSetting>> ListGatesAsync(CancellationToken ct);

    Task SetGateAsync(long workspaceId, string operation, CommandGate gate, DateTimeOffset now, CancellationToken ct);

    /// <summary>False when there was no setting. The operation then follows its default again.</summary>
    Task<bool> ClearGateAsync(long workspaceId, string operation, CancellationToken ct);

    Task<CommandTrust?> GetTrustAsync(long workspaceId, CancellationToken ct);

    Task SetTrustAsync(long workspaceId, string fingerprintJson, DateTimeOffset now, CancellationToken ct);

    /// <summary>False when there was no baseline. The next command that loads project files asks again.</summary>
    Task<bool> ClearTrustAsync(long workspaceId, CancellationToken ct);
}
