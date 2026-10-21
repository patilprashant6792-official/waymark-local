using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waymark.Host.Ui;

public static class UiServiceCollectionExtensions
{
    /// <summary>Registers the services behind the admin pages.</summary>
    public static IServiceCollection AddWaymarkUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ShellStatus>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ContextUsageService>();
        services.AddSingleton<Waymark.Core.Tools.IContextMeter, Waymark.Host.Mcp.OpenAiContextMeter>();
        services.AddHostedService<AuditRetentionService>();
        services.AddSingleton<Waymark.Host.Vault.VaultService>();
        services.AddSingleton<Waymark.Host.Security.AccessRuleService>();
        services.AddSingleton<Waymark.Host.Security.AccessBrowser>();
        services.AddSingleton<Waymark.Host.NuGetAdmin.NuGetCacheService>();
        services.AddSingleton<Waymark.Host.NuGetAdmin.NuGetExplorerService>();
        services.AddSingleton<Waymark.Host.Commands.CommandAdminService>();
        return services;
    }
}
