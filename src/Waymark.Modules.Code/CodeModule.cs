using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Modules;
using Waymark.Modules.Code.Content;
using Waymark.Modules.Code.Reading;
using Waymark.Modules.Code.Scheduling;
using Waymark.Modules.Code.Search;
using Waymark.Modules.Code.Skeleton;
using Waymark.Modules.Code.Tools;

namespace Waymark.Modules.Code;

/// <summary>Provides the code module registration boundary.</summary>
public sealed class CodeModule : IMcpModule
{
    public string Name => "code";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(CodeIndexOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ISyntaxIndexer, SyntaxIndexer>();
        services.TryAddSingleton<WorkspaceIndexer>();
        services.TryAddSingleton<IndexScheduler>();
        services.TryAddSingleton<IIndexScheduler>(provider => provider.GetRequiredService<IndexScheduler>());
        services.AddHostedService(provider => provider.GetRequiredService<IndexScheduler>());
        services.AddHostedService<FileWatcherHost>();
        services.TryAddSingleton(SkeletonOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ISkeletonService, SkeletonService>();
        services.TryAddSingleton(CodeReadOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ICodeReadService, CodeReadService>();
        services.TryAddSingleton(CodeSearchOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ICodeSearchService, CodeSearchService>();
        services.TryAddSingleton<ICodeCallersService, CodeCallersService>();
        services.TryAddSingleton<ICodeTypesService, CodeTypesService>();
        services.TryAddSingleton(ContentOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ITextSearchService, TextSearchService>();
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<CodeSkeletonTools>();
        mcp.WithTools<CodeReadTools>();
        mcp.WithTools<CodeSearchTools>();
        mcp.WithTools<CodeGrepTools>();
    }
}
