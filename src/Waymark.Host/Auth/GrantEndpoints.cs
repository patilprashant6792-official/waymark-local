using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Host.Edge;

namespace Waymark.Host.Auth;

/// <summary>
/// POST /token (authorization code with PKCE, and refresh with rotation) and POST /revoke. Both take
/// application/x-www-form-urlencoded bodies, as Claude sends them, and both are for public clients.
/// </summary>
public static class GrantEndpoints
{
    private const int MaxFormBytes = 8192;
    private const string BearerType = "Bearer";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/token", ExchangeAsync).RequireRateLimiting(RateLimitPolicies.Token).DisableAntiforgery();
        app.MapPost("/revoke", RevokeAsync).RequireRateLimiting(RateLimitPolicies.Token).DisableAntiforgery();
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext http,
        IOAuthClientStore clients,
        IGrantStore grants,
        IAuditStore audit,
        AuthCache cache,
        TimeProvider time,
        CancellationToken ct)
    {
        var form = await ReadFormAsync(http, ct);
        if (form is null)
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_request", "A form-encoded body (application/x-www-form-urlencoded) is required.");
        }

        var clientId = form["client_id"].ToString();
        var client = clientId.Length == 0 ? null : await clients.GetAsync(clientId, ct);
        if (client is null || client.IsRevoked)
        {
            await Denied(audit, time, "token.rejected", clientId, "invalid_client", ct);
            return OAuthResults.Error(StatusCodes.Status401Unauthorized, "invalid_client", "Unknown or revoked client.");
        }

        var now = time.GetUtcNow();
        return form["grant_type"].ToString() switch
        {
            "authorization_code" => await ExchangeCodeAsync(form, client, grants, audit, cache, time, now, ct),
            "refresh_token" => await RefreshAsync(form, client, grants, audit, cache, time, now, ct),
            _ => OAuthResults.Error(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Supported grants are authorization_code and refresh_token.")
        };
    }

    private static async Task<IResult> ExchangeCodeAsync(
        IFormCollection form,
        OAuthClient client,
        IGrantStore grants,
        IAuditStore audit,
        AuthCache cache,
        TimeProvider time,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var presented = form["code"].ToString();
        if (presented.Length is < 20 or > 128)
        {
            await Denied(audit, time, "token.rejected", client.ClientId, "invalid_grant", ct);
            return InvalidGrant("The authorization code is not valid.");
        }

        var result = await grants.ConsumeCodeAsync(Sha256Hasher.Hash(presented), now, ct);
        if (result.Status == CodeConsumeStatus.AlreadyConsumed && result.Code is { } reused)
        {
            // A code that is used twice means it was intercepted: end everything that was issued from it.
            await grants.RevokeByApprovalAsync(reused.ApprovalId, now, ct);
            cache.Invalidate();
            await Denied(audit, time, "token.code_reused", client.ClientId, "code_reused", ct);
            return InvalidGrant("The authorization code was already used.");
        }

        if (result.Status != CodeConsumeStatus.Consumed || result.Code is not { } code)
        {
            await Denied(audit, time, "token.rejected", client.ClientId, "invalid_grant", ct);
            return InvalidGrant("The authorization code is not valid or has expired.");
        }

        var resource = form["resource"].ToString();
        var matches = code.ClientId == client.ClientId
            && string.Equals(form["redirect_uri"].ToString(), code.RedirectUri, StringComparison.Ordinal)
            && Pkce.Verify(form["code_verifier"].ToString(), code.CodeChallenge)
            && (resource.Length == 0 || string.Equals(resource, code.Resource, StringComparison.Ordinal));
        if (!matches)
        {
            await Denied(audit, time, "token.rejected", client.ClientId, "grant_mismatch", ct);
            return InvalidGrant("The authorization code does not match this request.");
        }

        var familyId = RandomValues.Hex(16);
        var access = RandomValues.WithPrefix(AuthDefaults.AccessPrefix);
        var refresh = RandomValues.WithPrefix(AuthDefaults.RefreshPrefix);
        await grants.IssuePairAsync(
            Token(access, TokenKind.Access, familyId, client.ClientId, code, now, AuthDefaults.AccessLifetime),
            Token(refresh, TokenKind.Refresh, familyId, client.ClientId, code, now, AuthDefaults.RefreshLifetime),
            ct);

        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Auth, "token.issued", client.ClientId, args: $"approval={code.ApprovalId}"),
            ct);
        return Issued(access, refresh, code.Scope);
    }

    private static async Task<IResult> RefreshAsync(
        IFormCollection form,
        OAuthClient client,
        IGrantStore grants,
        IAuditStore audit,
        AuthCache cache,
        TimeProvider time,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var presented = form["refresh_token"].ToString();
        if (!presented.StartsWith(AuthDefaults.RefreshPrefix, StringComparison.Ordinal) || presented.Length > 200)
        {
            await Denied(audit, time, "token.rejected", client.ClientId, "invalid_grant", ct);
            return InvalidGrant("The refresh value is not valid.");
        }

        var access = RandomValues.WithPrefix(AuthDefaults.AccessPrefix);
        var refresh = RandomValues.WithPrefix(AuthDefaults.RefreshPrefix);
        var result = await grants.RotateRefreshAsync(
            new RotationRequest(
                client.ClientId,
                Sha256Hasher.Hash(presented),
                Sha256Hasher.Hash(access),
                Sha256Hasher.Hash(refresh),
                now + AuthDefaults.AccessLifetime,
                now + AuthDefaults.RefreshLifetime),
            now,
            ct);

        if (result.Status == RotationStatus.ReuseDetected)
        {
            cache.Invalidate();
        }

        if (result.Status != RotationStatus.Rotated || result.Token is not { } rotated)
        {
            // A dead refresh value must answer invalid_grant: that is what makes the client start a fresh sign-in.
            await Denied(audit, time, "token.rejected", client.ClientId, result.Status.ToString(), ct);
            return InvalidGrant("The refresh value is no longer valid. Sign in again.");
        }

        await audit.AppendAsync(AuditEntries.Create(time, AuditCategory.Auth, "token.refreshed", client.ClientId), ct);
        return Issued(access, refresh, rotated.Scope);
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext http,
        IGrantStore grants,
        IAuditStore audit,
        AuthCache cache,
        TimeProvider time,
        CancellationToken ct)
    {
        var form = await ReadFormAsync(http, ct);
        var value = form?["token"].ToString() ?? string.Empty;
        var clientId = form?["client_id"].ToString() ?? string.Empty;
        if (form is null || value.Length == 0 || value.Length > 200 || clientId.Length == 0)
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_request", "token and client_id are required in a form-encoded body.");
        }

        var found = await grants.RevokeTokenAsync(Sha256Hasher.Hash(value), clientId, time.GetUtcNow(), ct);
        cache.Invalidate();
        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Auth, "token.revoked", clientId, outcome: found ? AuditOutcome.Ok : AuditOutcome.Error),
            ct);

        // RFC 7009: the answer is the same whether or not the value was known.
        return OAuthResults.Json(StatusCodes.Status200OK, new { });
    }

    private static async Task<IFormCollection?> ReadFormAsync(HttpContext http, CancellationToken ct)
    {
        var request = http.Request;
        if (!request.HasFormContentType || request.ContentLength is > MaxFormBytes)
        {
            return null;
        }

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxFormBytes;
        }

        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static NewToken Token(string value, TokenKind kind, string familyId, string clientId, StoredAuthCode code, DateTimeOffset now, TimeSpan lifetime) =>
        new(Sha256Hasher.Hash(value), kind, familyId, clientId, code.ApprovalId, code.Scope, code.Resource, now, now + lifetime);

    private static IResult Issued(string access, string refresh, string scope) =>
        OAuthResults.Json(StatusCodes.Status200OK, new GrantResponse(access, BearerType, (int)AuthDefaults.AccessLifetime.TotalSeconds, refresh, scope));

    private static IResult InvalidGrant(string description) =>
        OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_grant", description);

    private static Task Denied(IAuditStore audit, TimeProvider time, string action, string clientId, string reason, CancellationToken ct) =>
        audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Auth, action, clientId.Length == 0 ? null : clientId, outcome: AuditOutcome.Denied, error: reason),
            ct);

    internal sealed record GrantResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope);
}
