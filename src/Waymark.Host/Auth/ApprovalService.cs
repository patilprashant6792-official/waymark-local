using Waymark.Core.Approvals;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;

namespace Waymark.Host.Auth;

/// <summary>Approve or deny a pending pairing from the admin UI: store the decision, audit it, tell the UI.</summary>
public sealed class ApprovalService(IApprovalStore approvals, IAuditStore audit, IEventBus events, TimeProvider time)
{
    public async Task<bool> DecideAsync(long approvalId, bool approve, CancellationToken ct)
    {
        var approval = await approvals.GetAsync(approvalId, ct);
        var decided = await approvals.DecideAsync(approvalId, approve, AuthDefaults.AdminActor, null, time.GetUtcNow(), ct);

        await audit.AppendAsync(
            AuditEntries.Create(
                time,
                AuditCategory.Approval,
                approve ? "approval.approved" : "approval.denied",
                AuthDefaults.AdminActor,
                approval?.ClientId,
                decided ? AuditOutcome.Ok : AuditOutcome.Error,
                approval is null ? null : $"redirect={approval.RedirectHost}",
                decided ? null : "The request was no longer pending."),
            ct);

        if (decided)
        {
            await events.PublishAsync(new ApprovalDecided(approvalId, approve ? ApprovalStatus.Approved : ApprovalStatus.Denied));
        }

        return decided;
    }

    /// <summary>Removes finished requests from the list. Waiting requests and approvals of apps that are still connected stay.</summary>
    public async Task<int> ClearHistoryAsync(CancellationToken ct)
    {
        var removed = await approvals.ClearFinishedAsync(time.GetUtcNow(), ct);
        if (removed > 0)
        {
            await audit.AppendAsync(
                AuditEntries.Create(time, AuditCategory.Approval, "approvals.cleared", AuthDefaults.AdminActor, args: $"removed={removed}"),
                ct);
        }

        return removed;
    }
}
