using Microsoft.Extensions.DependencyInjection;
using Waymark.Core.Guides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Core.Security;

namespace Waymark.Host.Mcp;
public static class ToolPipelineExtensions
{
    /// <summary>Puts the audit, limit, pause and per-tool switch pipeline around every tool call and tool list.</summary>
    public static IMcpServerBuilder WithWaymarkToolPipeline(this IMcpServerBuilder mcp)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.Services.AddSingleton<ToolPipeline>();
        mcp.Services.AddSingleton<ToolResultGuard>();
        // The copy tools check text they move on the server (without Claude reading it) with the same vault and credential checks as the result guard.
        mcp.Services.TryAddSingleton<IContentInspector, Waymark.Host.Vault.ContentInspector>();
        mcp.Services.AddSingleton<GuideDeliveries>();
        // The overview describes what is switched on, so it needs the host's view of the tool, workspace and command settings.
        mcp.Services.AddSingleton<ISetupSummary, SetupSummary>();
        mcp.Services.AddSingleton<ToolGuideDelivery>();
        return mcp.WithRequestFilters(filters => filters
            .AddListToolsFilter(next => (context, ct) => context.Services!.GetRequiredService<ToolPipeline>().ListAsync(context, next, ct))
            .AddCallToolFilter(next => (context, ct) => context.Services!.GetRequiredService<ToolPipeline>().CallAsync(context, next, ct)));
    }
}
