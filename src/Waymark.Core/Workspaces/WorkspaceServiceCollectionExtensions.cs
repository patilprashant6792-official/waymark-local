using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waymark.Core.Workspaces;

public static class WorkspaceServiceCollectionExtensions
{
    /// <summary>Registers the workspace service. Needs IWorkspaceStore, IEventBus and TimeProvider from elsewhere.</summary>
    public static IServiceCollection AddWaymarkWorkspaces(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(WorkspaceOptions.FromConfiguration(configuration));
        services.TryAddSingleton<IWorkspaceService, WorkspaceService>();
        return services;
    }
}
