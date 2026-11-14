using Microsoft.AspNetCore.DataProtection;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Core.Edge;
using Waymark.Core.Events;
using Waymark.Core.Vault;
using Waymark.Host.Auth;
using Waymark.Host.Vault.MasterKey;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace Waymark.Host.Edge;

public static class EdgeServiceCollectionExtensions
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddWaymarkEdge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IListenerClassifier, PortListenerClassifier>();
        services.AddSingleton<IPublicUrlProvider, PublicUrlProvider>();
        services.AddSingleton<EdgeUrls>();
        services.AddSingleton<AuthCache>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<ClientService>();
        services.AddSingleton<PublicUrlService>();
        services.AddSingleton<NgrokAgentClient>();
        services.AddSingleton(sp => NgrokOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<NgrokLocator>();
        services.AddSingleton<NgrokInstaller>();
        services.AddSingleton<INgrokAuthStore, NgrokAuthStore>();
        services.AddSingleton<NgrokTunnel>();
        services.AddHostedService<NgrokTunnelHostedService>();

        services.AddHttpClient(PublicUrlService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Waymark", "0.1"));
            client.DefaultRequestHeaders.TryAddWithoutValidation("ngrok-skip-browser-warning", "1");
        });
        services.AddHttpClient(NgrokAgentClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(2));

        // ngrok connects from this machine, so its X-Forwarded-* headers can be trusted (loopback is the default trusted proxy).
        services.Configure<ForwardedHeadersOptions>(options =>
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Register, context => PerClient(context, 10));
            options.AddPolicy(RateLimitPolicies.Authorize, context => PerClient(context, 30));
            options.AddPolicy(RateLimitPolicies.Wait, context => PerClient(context, 120));
            options.AddPolicy(RateLimitPolicies.Token, context => PerClient(context, 60));
        });

        return services;
    }

    /// <summary>
    /// Data Protection, which encrypts the vault and the saved ngrok authtoken. On Windows the keys are files next to the database, themselves
    /// encrypted with DPAPI, so only the Windows account that saved them can read them. On macOS the keys live in the database, encrypted
    /// with a master key kept in the login Keychain, so a copied data folder or backup cannot be read without this Mac account.
    /// Elsewhere the key files are only as private as the data folder.
    /// </summary>
    public static IServiceCollection AddWaymarkDataProtection(this IServiceCollection services, string keysDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(keysDirectory);

        var builder = services.AddDataProtection().SetApplicationName("Waymark");
        if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<IMasterKeySource, KeychainMasterKey>();
            services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider => new ConfigureOptions<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new SqliteXmlRepository(provider.GetRequiredService<IDataProtectionKeyStore>(), provider.GetRequiredService<TimeProvider>());
                options.XmlEncryptor = new MasterKeyXmlEncryptor(provider.GetRequiredService<IMasterKeySource>());
            }));
            return services;
        }

        builder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi();
        }

        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = RateWindow, QueueLimit = 0 });
}
