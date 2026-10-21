using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;

namespace Waymark.Host.Auth;

/// <summary>Revoking a client ends all of its tokens and drops any cached credentials at once.</summary>
public sealed class ClientService(IOAuthClientStore clients, IAuditStore audit, IEventBus events, AuthCache cache, TimeProvider time)
{
    public async Task<bool> RevokeAsync(string clientId, CancellationToken ct)
    {
        var revoked = await clients.RevokeAsync(clientId, time.GetUtcNow(), ct);
        cache.Invalidate();

        await audit.AppendAsync(
            AuditEntries.Create(
                time,
                AuditCategory.Auth,
                "client.revoked",
                AuthDefaults.AdminActor,
                clientId,
                revoked ? AuditOutcome.Ok : AuditOutcome.Error,
                error: revoked ? null : "Unknown or already revoked."),
            ct);

        if (revoked)
        {
            await events.PublishAsync(new CredentialsRevoked());
        }

        return revoked;
    }

    /// <summary>Removes disconnected apps and everything stored for them. Active apps are never touched.</summary>
    public async Task<int> ClearDisconnectedAsync(CancellationToken ct)
    {
        var removed = await clients.ClearDisconnectedAsync(time.GetUtcNow(), ct);
        if (removed > 0)
        {
            await audit.AppendAsync(
                AuditEntries.Create(time, AuditCategory.Auth, "clients.cleared", AuthDefaults.AdminActor, args: $"removed={removed}"),
                ct);
        }

        return removed;
    }
}
