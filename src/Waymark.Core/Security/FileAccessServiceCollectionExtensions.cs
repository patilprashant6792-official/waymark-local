using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waymark.Core.Security;

public static class FileAccessServiceCollectionExtensions
{
    /// <summary>Registers the file access policy, the per-workspace rule cache and the path guard. Needs IWorkspaceStore, IAccessRuleStore and IEventBus from elsewhere.</summary>
    public static IServiceCollection AddWaymarkFileAccess(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(FileAccessPolicy.FromConfiguration(configuration));
        services.TryAddSingleton<IAccessRuleCache, AccessRuleCache>();
        services.TryAddSingleton<IPathGuard, PathGuard>();
        return services;
    }
}
