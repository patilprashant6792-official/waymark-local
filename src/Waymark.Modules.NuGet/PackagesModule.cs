using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Waymark.Core.Code;
using Waymark.Core.Modules;
using Waymark.Languages.Python;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;
using Waymark.Modules.NuGet.Pypi;
using Waymark.Modules.NuGet.Tools;

namespace Waymark.Modules.NuGet;

/// <summary>
/// The package lookups for every ecosystem: NuGet (reflection over the compiled package) and PyPI (the wheel's Python source, read by syntax only).
/// The module is named <c>packages</c>; its project and namespaces keep the historic NuGet name.
/// </summary>
public sealed class PackagesModule : IMcpModule
{
    // A package module is read once, never edited: room for the biggest generated module without letting one file fill the index.
    private const int MaxMembersPerPackageFile = 20_000;

    public string Name => "packages";

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
        services.TryAddSingleton<PackageIndexer>();
        services.TryAddSingleton(PypiOptions.FromConfiguration(configuration));
        services.TryAddSingleton<PypiClient>();
        services.TryAddSingleton<PypiPackageLoader>();
        services.TryAddSingleton<PypiPackageSearch>();
        services.TryAddSingleton(provider => new PythonIndexer(
            new CodeIndexLimits((int)provider.GetRequiredService<PypiOptions>().MaxFileBytes, MaxMembersPerPackageFile, 1),
            provider.GetRequiredService<ILogger<PythonIndexer>>()));
        services.TryAddSingleton<PypiPackageIndexer>();
        services.TryAddSingleton<IPackageIndexer, EcosystemPackageIndexer>();
        services.TryAddSingleton<IPackagePins, PythonPackagePins>();
        services.TryAddSingleton<INuGetSymbolService, NuGetSymbolService>();
        services.TryAddSingleton<Waymark.Core.NuGetIndex.INuGetReloader, NuGetReloader>();
        services.TryAddSingleton<Waymark.Core.NuGetIndex.INuGetExplorer, NuGetExplorer>();
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<PackageTools>();
    }
}
