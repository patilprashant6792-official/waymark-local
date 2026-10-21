using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Core.Guides;

namespace Waymark.Core.Tools;

public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tool registry (built from the given assemblies), the guide catalog and the call limits.
    /// The host adds <see cref="ISetupSummary"/>, which the overview needs to describe what is switched on.
    /// </summary>
    public static IServiceCollection AddWaymarkTools(this IServiceCollection services, IConfiguration configuration, IEnumerable<Assembly> toolAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(toolAssemblies);
        var assemblies = toolAssemblies.Distinct().ToList();
        var registry = new ToolRegistry(assemblies);
        services.TryAddSingleton<IToolRegistry>(registry);
        services.TryAddSingleton<IHostPrompt, HostPrompt>();
        services.TryAddSingleton<IGuideCatalog>(provider => new GuideCatalog(
            registry,
            assemblies,
            provider.GetRequiredService<IHostPrompt>()));
        services.TryAddSingleton(ToolPipelineOptions.FromConfiguration(configuration));
        services.TryAddSingleton(SafetyOptions.FromConfiguration(configuration));
        return services;
    }
}
