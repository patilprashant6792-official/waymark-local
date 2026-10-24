using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Modules;
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
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<FileWriteTool>();
        mcp.WithTools<FileEditLinesTool>();
        mcp.WithTools<FileOperationTools>();
    }
}
