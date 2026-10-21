using Microsoft.AspNetCore.Authentication;

namespace Waymark.Host.Auth;

public static class AuthPolicies
{
    /// <summary>A signed-in caller whose credential includes the mcp scope.</summary>
    public const string Mcp = "waymark-mcp";
}

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddWaymarkAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(BearerAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthHandler>(BearerAuthHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Mcp, policy => policy
                .AddAuthenticationSchemes(BearerAuthHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.FindFirst(BearerAuthHandler.ScopeClaim)?.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(Waymark.Core.Auth.AuthDefaults.McpScope) == true));

        services.AddSingleton<AccessKeyService>();
        return services;
    }
}
