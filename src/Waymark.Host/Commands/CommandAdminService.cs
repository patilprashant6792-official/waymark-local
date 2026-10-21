using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Commands;
using Waymark.Core.Events;

namespace Waymark.Host.Commands;

/// <summary>
/// The owner's changes to how commands are treated: the Off/Ask/Auto gate of each operation, per workspace or for all of them, and forgetting the
/// approved state of a workspace's project files. Every change is written to the audit trail.
/// </summary>
public sealed class CommandAdminService(ICommandPolicyStore policy, IAuditStore audit, IEventBus events, TimeProvider time)
{
    public Task<IReadOnlyList<CommandGateSetting>> ListGatesAsync(CancellationToken ct) => policy.ListGatesAsync(ct);

    /// <param name="workspaceId"><see cref="CommandCatalog.AllWorkspaces"/> for a rule that applies to every workspace.</param>
    /// <exception cref="ArgumentException">The gate is above what the operation allows (a shell script can never be Auto).</exception>
    public async Task SetGateAsync(long workspaceId, string scopeName, string operation, CommandGate gate, CancellationToken ct)
    {
        var info = CommandCatalog.Get(operation);
        if (gate > info.MaxGate)
        {
            throw new ArgumentException($"'{info.Label}' can be set to {info.MaxGate} at most.");
        }

        await policy.SetGateAsync(workspaceId, info.Key, gate, time.GetUtcNow(), ct).ConfigureAwait(false);
        await AuditAsync("config.command_gate", scopeName, $"{info.Key}={gate}", ct).ConfigureAwait(false);
        await events.PublishAsync(new CommandPolicyChanged(workspaceId)).ConfigureAwait(false);
    }

    /// <summary>Removes the owner's choice, so the operation follows the next level again (all workspaces, then the default).</summary>
    public async Task<bool> ClearGateAsync(long workspaceId, string scopeName, string operation, CancellationToken ct)
    {
        var info = CommandCatalog.Get(operation);
        var removed = await policy.ClearGateAsync(workspaceId, info.Key, ct).ConfigureAwait(false);
        if (removed)
        {
            await AuditAsync("config.command_gate_reset", scopeName, info.Key, ct).ConfigureAwait(false);
            await events.PublishAsync(new CommandPolicyChanged(workspaceId)).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>Removes every choice made at one level. Returns how many there were.</summary>
    public async Task<int> ClearScopeAsync(long workspaceId, string scopeName, CancellationToken ct)
    {
        var removed = 0;
        foreach (var operation in CommandCatalog.Operations)
        {
            if (await policy.ClearGateAsync(workspaceId, operation.Key, ct).ConfigureAwait(false))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            await AuditAsync("config.command_gate_reset", scopeName, $"{removed} choices", ct).ConfigureAwait(false);
            await events.PublishAsync(new CommandPolicyChanged(workspaceId)).ConfigureAwait(false);
        }

        return removed;
    }

    public Task<CommandTrust?> GetTrustAsync(long workspaceId, CancellationToken ct) => policy.GetTrustAsync(workspaceId, ct);

    /// <summary>The next command that loads project files asks again, and approving it makes the files trusted anew.</summary>
    public async Task<bool> ForgetTrustAsync(long workspaceId, string workspaceName, CancellationToken ct)
    {
        var removed = await policy.ClearTrustAsync(workspaceId, ct).ConfigureAwait(false);
        if (removed)
        {
            await AuditAsync("config.command_trust_reset", workspaceName, null, ct).ConfigureAwait(false);
            await events.PublishAsync(new CommandPolicyChanged(workspaceId)).ConfigureAwait(false);
        }

        return removed;
    }

    private Task AuditAsync(string action, string target, string? args, CancellationToken ct) =>
        audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Config, action, AuthDefaults.AdminActor, target, AuditOutcome.Ok, args), ct);
}
