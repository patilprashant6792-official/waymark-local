using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Waymark.Core.Auth;
using Waymark.Host.Edge;

namespace Waymark.Host.Auth;

/// <summary>
/// Accepts Waymark's own credentials on /mcp: an OAuth access value (sma_...) or an API key (smk_...). It is written by
/// hand instead of using the MCP SDK's authentication handler because the challenge must name the metadata address
/// that belongs to the request (the saved public address, or localhost), and the SDK takes one fixed address.
/// </summary>
public sealed class BearerAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IGrantStore grants,
    IAccessKeyStore keys,
    IOAuthClientStore clients,
    AuthCache cache,
    EdgeUrls urls,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Waymark";
    public const string ScopeClaim = "scope";
    public const string ClientClaim = "client_id";
    public const string KindClaim = "credential_kind";

    private const string BearerPrefix = "Bearer ";
    private const int MinLength = 20;
    private const int MaxLength = 200;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private bool _invalidPresented;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header[BearerPrefix.Length..].Trim();
        var principal = await ValidateAsync(value);
        if (principal is null)
        {
            _invalidPresented = true;
            return AuthenticateResult.Fail("The credential is not valid.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var parts = new List<string>();
        if (urls.BaseFor(Request) is { } baseUrl)
        {
            parts.Add($"resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"");
        }

        if (_invalidPresented)
        {
            parts.Add("error=\"invalid_token\"");
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = parts.Count == 0 ? "Bearer" : "Bearer " + string.Join(", ", parts);
        return Task.CompletedTask;
    }

    private async Task<ClaimsPrincipal?> ValidateAsync(string value)
    {
        if (value.Length is < MinLength or > MaxLength)
        {
            return null;
        }

        var hash = Sha256Hasher.Hash(value);
        var cacheKey = Convert.ToHexString(hash);
        if (cache.TryGet(cacheKey) is { } cached)
        {
            return cached;
        }

        var epoch = cache.Epoch;
        var now = time.GetUtcNow();
        var ct = Context.RequestAborted;

        (ClaimsPrincipal Principal, DateTimeOffset ValidUntil)? found = null;
        if (value.StartsWith(AuthDefaults.AccessPrefix, StringComparison.Ordinal))
        {
            found = await FromAccessAsync(hash, now, ct);
        }
        else if (value.StartsWith(AuthDefaults.ApiKeyPrefix, StringComparison.Ordinal))
        {
            found = await FromKeyAsync(hash, now, ct);
        }

        if (found is null)
        {
            return null;
        }

        var (principal, validUntil) = found.Value;
        var cap = now + CacheLifetime;
        cache.Set(cacheKey, principal, validUntil < cap ? validUntil : cap, epoch);
        return principal;
    }

    private async Task<(ClaimsPrincipal, DateTimeOffset)?> FromAccessAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        var token = await grants.FindActiveAccessAsync(hash, now, ct);
        if (token is null)
        {
            return null;
        }

        // A credential issued for one address must not work on another.
        if (!string.Equals(urls.McpResourceFor(Request), token.Resource, StringComparison.Ordinal))
        {
            return null;
        }

        await TouchAsync("client:" + token.ClientId, () => clients.TouchAsync(token.ClientId, now, ct));
        return (BuildPrincipal(token.ClientId, token.ClientId, token.Scope, "oauth"), token.ExpiresUtc);
    }

    private async Task<(ClaimsPrincipal, DateTimeOffset)?> FromKeyAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        var key = await keys.FindActiveAsync(hash, now, ct);
        if (key is null)
        {
            return null;
        }

        await TouchAsync("key:" + key.Id, () => keys.TouchAsync(key.Id, now, ct));
        return (BuildPrincipal(key.Id, key.Id, key.Scope, "api_key"), key.ExpiresUtc ?? DateTimeOffset.MaxValue);
    }

    /// <summary>Last-used is a convenience for the admin lists; a failed write must never turn into a failed sign-in.</summary>
    private async Task TouchAsync(string id, Func<Task> write)
    {
        if (!cache.ShouldTouch(id))
        {
            return;
        }

        try
        {
            await write();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not record last use for {Id}", id);
        }
    }

    private static ClaimsPrincipal BuildPrincipal(string subject, string clientId, string scope, string kind)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(ClientClaim, clientId),
                new Claim(ScopeClaim, scope),
                new Claim(KindClaim, kind)
            ],
            SchemeName);
        return new ClaimsPrincipal(identity);
    }
}
