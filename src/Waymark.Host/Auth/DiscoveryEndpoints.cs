using System.Text.Json;
using Waymark.Core.Auth;
using Waymark.Core.Settings;
using Waymark.Host.Edge;

namespace Waymark.Host.Auth;

/// <summary>
/// The documents Claude reads to find the authorization server, and the instance probe Setup uses to prove a
/// tunnel really reaches this installation. The advertised addresses follow the request (see EdgeUrls).
/// </summary>
public static class DiscoveryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/waymark-instance", InstanceAsync);
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResource);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServer);
    }

    private static async Task<IResult> InstanceAsync(IConfigStore config, CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.InstanceId, ct);
        var instance = setting is null ? null : JsonSerializer.Deserialize<string>(setting.ValueJson);
        return OAuthResults.Json(StatusCodes.Status200OK, new { instance });
    }

    private static IResult ProtectedResource(HttpRequest request, EdgeUrls urls)
    {
        var baseUrl = urls.BaseFor(request);
        if (baseUrl is null)
        {
            return NotConfigured();
        }

        return OAuthResults.Json(StatusCodes.Status200OK, new
        {
            resource = baseUrl + "/mcp",
            authorization_servers = new[] { baseUrl },
            scopes_supported = new[] { AuthDefaults.McpScope },
            bearer_methods_supported = new[] { "header" },
            resource_name = "Waymark"
        });
    }

    private static IResult AuthorizationServer(HttpRequest request, EdgeUrls urls)
    {
        var baseUrl = urls.BaseFor(request);
        if (baseUrl is null)
        {
            return NotConfigured();
        }

        // No client_id_metadata_document_supported flag: Claude then registers itself through /register (DCR).
        return OAuthResults.Json(StatusCodes.Status200OK, new
        {
            issuer = baseUrl,
            authorization_endpoint = baseUrl + "/authorize",
            token_endpoint = baseUrl + "/token",
            registration_endpoint = baseUrl + "/register",
            revocation_endpoint = baseUrl + "/revoke",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            revocation_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = new[] { AuthDefaults.McpScope, AuthDefaults.OfflineAccessScope }
        });
    }

    private static IResult NotConfigured() =>
        OAuthResults.Error(
            StatusCodes.Status503ServiceUnavailable,
            "not_configured",
            "Waymark has no public address yet. Finish Setup in the admin page.");
}
