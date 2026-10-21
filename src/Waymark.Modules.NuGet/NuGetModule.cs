using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Modules;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;
using Waymark.Modules.NuGet.Tools;

namespace Waymark.Modules.NuGet;

/// <summary>Provides the NuGet module registration boundary.</summary>
public sealed class NuGetModule : IMcpModule
{
    public string Name => "nuget";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(NuGetLoadOptions.FromConfiguration(configuration));
        services.TryAddSingleton<PackageFeed>();
        services.TryAddSingleton<AssemblyReflector>();
        services.TryAddSingleton<PackageLoadPipeline>();
        services.TryAddSingleton<IPackageLoader, PackageLoader>();
        services.TryAddSingleton(NuGetIndexOptions.FromConfiguration(configuration));
        services.TryAddSingleton<IPackageIndexer, PackageIndexer>();
        services.TryAddSingleton<INuGetSymbolService, NuGetSymbolService>();
        services.TryAddSingleton<Waymark.Core.NuGetIndex.INuGetReloader, NuGetReloader>();
        services.TryAddSingleton<Waymark.Core.NuGetIndex.INuGetExplorer, NuGetExplorer>();
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<NuGetTools>();
    }
}
