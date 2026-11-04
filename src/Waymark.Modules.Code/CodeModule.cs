using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Waymark.Core.Modules;
using Waymark.Core.Code;
using Waymark.Core.Memory;
using Waymark.Modules.Code.Content;
using Waymark.Modules.Code.Reading;
using Waymark.Modules.Code.Scheduling;
using Waymark.Modules.Code.Rewriting;
using Waymark.Modules.Code.Search;
using Waymark.Modules.Code.Memory;
using Waymark.Modules.Code.Memory.Semantic;
using Waymark.Modules.Code.Skeleton;
using Waymark.Languages.Python;
using Waymark.Languages.TypeScript;
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
        services.TryAddSingleton<CodeIndexLimits>(provider => provider.GetRequiredService<CodeIndexOptions>().Limits);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageIndexer, CSharpIndexer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageIndexer, PythonIndexer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILanguageIndexer, TypeScriptIndexer>());
        services.TryAddSingleton<LanguageIndexers>();
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
        services.TryAddSingleton<ICSharpRewriter, CSharpRewriter>();
        services.TryAddSingleton(FlowOptions.FromConfiguration(configuration));
        services.TryAddSingleton<FlowStepReader>();
        services.TryAddSingleton<FlowLinkChecker>();
        services.TryAddSingleton<FlowDriftFinder>();
        services.TryAddSingleton<FlowInspector>();
        services.TryAddSingleton<FlowRecallService>();
        services.TryAddSingleton<FlowSaveService>();
        services.TryAddSingleton<IFeatureMemoryService, FeatureMemoryService>();
        services.TryAddSingleton<IFeatureComponentChecker, FeatureComponentChecker>();

        // Search by meaning over flows is not built yet: the empty index is registered so that saving, deleting, importing and listing flows are already wired to it.
        services.TryAddSingleton<IFlowSemanticIndex, NoFlowSemanticIndex>();
        services.AddHostedService<FlowSemanticSync>();

        // The files module registers a silent advisor with TryAdd; this one replaces it, whichever module registers first.
        services.Replace(ServiceDescriptor.Singleton<IFlowEditAdvisor, FlowEditAdvisor>());
    }

    public void AddTools(IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.WithTools<CodeSkeletonTools>();
        mcp.WithTools<CodeReadTools>();
        mcp.WithTools<CodeSearchTools>();
        mcp.WithTools<CodeGrepTools>();
        mcp.WithTools<MemoryTools>();
    }
}
