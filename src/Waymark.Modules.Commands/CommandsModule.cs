using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Commands;
using Waymark.Core.Modules;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Policy;
using Waymark.Modules.Commands.Recipes;
using Waymark.Modules.Commands.Tools;

namespace Waymark.Modules.Commands;

/// <summary>Provides the commands module: typed dotnet, git and process commands and a system shell, all run and approved through the coordinator.</summary>
public sealed class CommandsModule : IMcpModule
{
    public string Name => "commands";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(CommandsOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ExecutableLocator>();
        services.TryAddSingleton<ShellLocator>();
        services.TryAddSingleton<ProcessRunner>();
        services.TryAddSingleton<ListeningPorts>();
        services.TryAddSingleton<ProcRecipes>();
        services.TryAddSingleton<CommandPolicy>();
        services.TryAddSingleton<CommandCoordinator>();
        services.TryAddSingleton<ICommandRuns>(provider => provider.GetRequiredService<CommandCoordinator>());
        services.TryAddSingleton<CommandService>();
        services.AddHostedService<CommandMaintenanceService>();
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<CommandRunTools>();
        mcp.WithTools<CommandReadTools>();
    }
}
