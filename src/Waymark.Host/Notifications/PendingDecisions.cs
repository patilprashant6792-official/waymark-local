using Waymark.Core.Approvals;
using Waymark.Core.Commands;

namespace Waymark.Host.Notifications;

/// <summary>How many requests wait for the owner: commands that ask for approval, and apps that ask to connect.</summary>
public readonly record struct PendingDecisions(int Commands, int Pairings)
{
    public int Total => Commands + Pairings;
}

/// <summary>Counts what waits for the owner right now, from the stores rather than from a page, so it works with no browser open.</summary>
public interface IPendingDecisionSource
{
    Task<PendingDecisions> CountAsync(CancellationToken ct);
}

public sealed class PendingDecisionSource(ICommandRuns commands, IApprovalStore approvals, TimeProvider time) : IPendingDecisionSource
{
    public async Task<PendingDecisions> CountAsync(CancellationToken ct)
    {
        var waiting = await commands.ListPendingAsync(ct).ConfigureAwait(false);
        var pairings = await approvals.ListPendingAsync(time.GetUtcNow(), ct).ConfigureAwait(false);
        return new PendingDecisions(waiting.Count, pairings.Count);
    }
}
