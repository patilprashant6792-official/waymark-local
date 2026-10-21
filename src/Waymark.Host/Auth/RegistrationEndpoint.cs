using System.Globalization;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Host.Edge;

namespace Waymark.Host.Auth;

/// <summary>Dynamic Client Registration (RFC 7591) for public clients, limited to the redirect allowlist.</summary>
public static class RegistrationEndpoint
{
    private const int MaxRedirectUris = 5;
    private const int MaxNameLength = 100;
    private static readonly string[] SupportedGrants = ["authorization_code", "refresh_token"];

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/register", RegisterAsync)
            .RequireRateLimiting(RateLimitPolicies.Register)
            .DisableAntiforgery();

    private static async Task<IResult> RegisterAsync(
        ClientRegistrationRequest? request,
        IOAuthClientStore clients,
        IAuditStore audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request is null)
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "A JSON body is required.");
        }

        var uris = request.RedirectUris?.Select(u => u.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (uris.Count == 0 || uris.Count > MaxRedirectUris || uris.Any(u => !RedirectUriPolicy.IsAllowedForRegistration(u)))
        {
            return OAuthResults.Error(
                StatusCodes.Status400BadRequest,
                "invalid_redirect_uri",
                "Only https://claude.ai/api/mcp/auth_callback and http://localhost or http://127.0.0.1 callbacks are accepted.");
        }

        if (request.TokenEndpointAuthMethod is not null && request.TokenEndpointAuthMethod != "none")
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) are supported.");
        }

        if (request.GrantTypes is { } grants && grants.Any(g => !SupportedGrants.Contains(g)))
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Only authorization_code and refresh_token grants are supported.");
        }

        if (!OAuthScopes.TryNormalize(request.Scope, out var scope))
        {
            return OAuthResults.Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Unsupported scope.");
        }

        var now = time.GetUtcNow();
        var name = CleanName(request.ClientName);
        var client = new OAuthClient(RandomValues.WithPrefix("wm_", 16), name, uris, now, null, null);
        await clients.AddAsync(client, ct);

        await audit.AppendAsync(
            AuditEntries.Create(time, AuditCategory.Auth, "client.registered", client.ClientId, name, args: string.Join(',', uris)),
            ct);

        return OAuthResults.Json(StatusCodes.Status201Created, new ClientRegistrationResponse(
            client.ClientId,
            now.ToUnixTimeSeconds(),
            name,
            uris,
            SupportedGrants,
            ["code"],
            "none",
            scope));
    }

    /// <summary>The name is shown on the approval card, so strip control characters and cap the length.</summary>
    private static string CleanName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return "Unnamed client";
        }

        return cleaned.Length <= MaxNameLength ? cleaned : cleaned[..MaxNameLength];
    }
}
