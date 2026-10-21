using Waymark.Core.Approvals;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Events;
using Waymark.Host.Edge;

namespace Waymark.Host.Auth;

/// <summary>
/// The browser half of the OAuth flow. /authorize validates the request and shows a pairing code; the page then
/// reloads /authorize/wait until the person approves in the admin UI, at which point the one-time code is minted
/// and the browser is sent back to the client.
/// </summary>
public static class AuthorizeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/authorize", AuthorizeAsync).RequireRateLimiting(RateLimitPolicies.Authorize);
        app.MapGet("/authorize/wait", WaitAsync).RequireRateLimiting(RateLimitPolicies.Wait);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        IOAuthClientStore clients,
        IApprovalStore approvals,
        IAuditStore audit,
        IEventBus events,
        EdgeUrls urls,
        EdgeOptions edge,
        TimeProvider time,
        CancellationToken ct)
    {
        var query = http.Request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();

        var resource = urls.McpResourceFor(http.Request);
        if (resource is null)
        {
            return PairingPages.Message(StatusCodes.Status503ServiceUnavailable, "Waymark is not set up", "Open Waymark on the computer where it runs, finish Setup, and try again.");
        }

        var client = clientId.Length == 0 ? null : await clients.GetAsync(clientId, ct);
        if (client is null || client.IsRevoked)
        {
            await Audit(audit, time, "authorize.rejected", clientId, "unknown_client", ct);
            return PairingPages.Message(StatusCodes.Status400BadRequest, "Unknown client", "This request names a client Waymark does not know. Start again from Claude.");
        }

        if (!RedirectUriPolicy.Matches(client.RedirectUris, redirectUri))
        {
            await Audit(audit, time, "authorize.rejected", clientId, "redirect_uri_mismatch", ct);
            return PairingPages.Message(StatusCodes.Status400BadRequest, "Redirect address not registered", "The address this request would return to was not registered by the client. Nothing was sent.");
        }

        // From here on the redirect address is trusted, so problems are reported back to the client.
        var state = query["state"].ToString();
        var returnedState = state.Length == 0 ? null : state;
        IResult Fail(string error, string description) => OAuthResults.RedirectError(redirectUri, error, description, returnedState);

        if (query["response_type"] != "code")
        {
            return Fail("unsupported_response_type", "Only response_type=code is supported.");
        }

        var challenge = query["code_challenge"].ToString();
        if (query["code_challenge_method"] != "S256" || !Pkce.IsValidChallenge(challenge))
        {
            return Fail("invalid_request", "PKCE with code_challenge_method S256 is required.");
        }

        if (!OAuthScopes.TryNormalize(query["scope"], out var scope))
        {
            return Fail("invalid_scope", "Supported scopes are mcp and offline_access.");
        }

        var requestedResource = query["resource"].ToString();
        if (requestedResource.Length != 0 && !string.Equals(requestedResource, resource, StringComparison.Ordinal))
        {
            return Fail("invalid_target", "The resource does not match this server.");
        }

        var now = time.GetUtcNow();
        if ((await approvals.ListPendingAsync(now, ct)).Count >= AuthDefaults.MaxPendingApprovals)
        {
            return Fail("temporarily_unavailable", "Too many connection requests are waiting for approval.");
        }

        var pollSecret = RandomValues.Base64UrlString(32);
        var redirectHost = new Uri(redirectUri).Host;
        var approval = await approvals.CreateAsync(
            new NewApproval(
                Sha256Hasher.Hash(pollSecret),
                RandomValues.PairingCode(),
                client.ClientId,
                client.ClientName,
                redirectUri,
                redirectHost,
                returnedState,
                challenge,
                scope,
                resource,
                now,
                now + AuthDefaults.PairingLifetime),
            ct);

        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Approval, "approval.requested", client.ClientId, redirectHost, args: $"approval={approval.Id}"),
            ct);
        await events.PublishAsync(new ApprovalRequested(approval.Id));

        return PairingPages.Waiting(approval.PairingCode, approval.ClientNameClaimed, redirectHost, approval.ExpiresUtc, now, WaitPath(pollSecret), edge.AdminPort);
    }

    private static async Task<IResult> WaitAsync(
        HttpContext http,
        IApprovalStore approvals,
        IAuditStore audit,
        EdgeOptions edge,
        TimeProvider time,
        CancellationToken ct)
    {
        var pollSecret = http.Request.Query["p"].ToString();
        if (pollSecret.Length is < 20 or > 128)
        {
            return PairingPages.Message(StatusCodes.Status404NotFound, "Request not found", "This pairing request does not exist. Start again from Claude.");
        }

        var now = time.GetUtcNow();
        var plainCode = RandomValues.Base64UrlString(32);
        var result = await approvals.TryMintCodeAsync(
            Sha256Hasher.Hash(pollSecret),
            Sha256Hasher.Hash(plainCode),
            now + AuthDefaults.CodeLifetime,
            now,
            ct);

        var approval = result.Approval;
        switch (result.Status)
        {
            case CodeMintStatus.Pending when approval is not null:
                return PairingPages.Waiting(approval.PairingCode, approval.ClientNameClaimed, approval.RedirectHost, approval.ExpiresUtc, now, WaitPath(pollSecret), edge.AdminPort);

            case CodeMintStatus.Issued when approval is not null:
                await audit.AppendAsync(
                    AuditEntries.Create(time, AuditCategory.Approval, "approval.code_issued", approval.ClientId, approval.RedirectHost, args: $"approval={approval.Id}"),
                    ct);
                return OAuthResults.Redirect(approval.RedirectUri, new Dictionary<string, string?>
                {
                    ["code"] = plainCode,
                    ["state"] = approval.State
                });

            case CodeMintStatus.Denied when approval is not null:
                return OAuthResults.RedirectError(approval.RedirectUri, "access_denied", "The request was denied in Waymark.", approval.State);

            case CodeMintStatus.Expired when approval is not null:
                return OAuthResults.RedirectError(approval.RedirectUri, "access_denied", "The request expired before it was approved.", approval.State);

            case CodeMintStatus.AlreadyIssued:
                return PairingPages.Message(StatusCodes.Status410Gone, "Already used", "This approval was already used. Start again from Claude if you need to reconnect.");

            default:
                return PairingPages.Message(StatusCodes.Status404NotFound, "Request not found", "This pairing request does not exist. Start again from Claude.");
        }
    }

    private static string WaitPath(string pollSecret) => $"/authorize/wait?p={pollSecret}";

    private static Task Audit(IAuditStore audit, TimeProvider time, string action, string clientId, string reason, CancellationToken ct) =>
        audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Auth, action, clientId.Length == 0 ? null : clientId, outcome: AuditOutcome.Denied, error: reason),
            ct);
}
