using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Memory;
using Waymark.Core.Modules;
using Waymark.Core.Text;
using Waymark.Modules.Files.Replace;
using Waymark.Modules.Files.Tools;

namespace Waymark.Modules.Files;

/// <summary>Provides the files module registration boundary.</summary>
public sealed class FilesModule : IMcpModule
{
    public string Name => "files";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(FilesOptions.FromConfiguration(configuration));
        services.TryAddSingleton<FileWriteGate>();
        services.TryAddSingleton<CopySourceReader>();
        services.TryAddSingleton<CopyResolver>();
        services.TryAddSingleton<FolderCopyPlanner>();
        services.TryAddSingleton<IFileEditor, FileEditor>();
        services.TryAddSingleton<IFileManager, FileManager>();
        services.TryAddSingleton(ReplaceOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReplacePlanStore>();
        services.TryAddSingleton<ReplacePlanner>();
        services.TryAddSingleton<ReplaceApplier>();
        services.TryAddSingleton<IReplaceService, ReplaceService>();

        // Says nothing. The code module registers the advisor that tells the agent when a change touches a step of a saved flow, and replaces this one.
        services.TryAddSingleton<IFlowEditAdvisor, NoFlowEditAdvisor>();

        // Lists nothing. The code module registers the catalog file_replace lists files with (the same rules as code_grep), and replaces this one.
        services.TryAddSingleton<ITextFileCatalog, NoTextFileCatalog>();
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<FileWriteTool>();
        mcp.WithTools<FileEditLinesTool>();
        mcp.WithTools<FileOperationTools>();
        mcp.WithTools<FileReplaceTool>();
    }
}
