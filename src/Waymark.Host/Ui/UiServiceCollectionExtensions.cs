using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Host.Notifications;

namespace Waymark.Host.Ui;

public static class UiServiceCollectionExtensions
{
    /// <summary>Registers the services behind the admin pages.</summary>
    public static IServiceCollection AddWaymarkUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ShellStatus>();
        services.AddScoped<CallPanelState>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ContextUsageService>();
        services.AddSingleton<Waymark.Core.Tools.IContextMeter, Waymark.Host.Mcp.OpenAiContextMeter>();
        services.AddHostedService<AuditRetentionService>();
        services.AddSingleton<Waymark.Host.Vault.VaultService>();
        services.AddSingleton<Waymark.Host.Security.AccessRuleService>();
        services.AddSingleton<Waymark.Host.Security.AccessBrowser>();
        services.AddSingleton<Waymark.Host.NuGetAdmin.NuGetCacheService>();
        services.AddSingleton<Waymark.Host.NuGetAdmin.NuGetExplorerService>();
        services.AddSingleton<Waymark.Host.Commands.CommandAdminService>();
        services.AddSingleton<Waymark.Host.FeatureMaps.FeatureMapAdminService>();
        services.AddSingleton<Waymark.Host.Transfer.WorkspaceBundleService>();

        // Feedback: an email the owner's own mail app sends. FeedbackOptions comes from the configuration (Program.cs).
        services.AddSingleton<Waymark.Host.Feedback.IOutboundTextScrubber, Waymark.Host.Feedback.GuardTextScrubber>();
        services.AddSingleton(Waymark.Host.Feedback.FeedbackSystemInfo.ForThisProcess());
        services.AddSingleton<Waymark.Host.Feedback.FeedbackComposer>();

        // Desktop notifications about waiting approvals: they only point to the admin UI, where deciding stays.
        services.AddSingleton<IDesktopNotifier>(_ => DesktopNotifiers.ForThisComputer());
        services.AddSingleton<DesktopNotificationSettings>();
        services.AddSingleton<IDesktopNotificationSettings>(provider => provider.GetRequiredService<DesktopNotificationSettings>());
        services.AddSingleton<IPendingDecisionSource, PendingDecisionSource>();
        services.AddHostedService<ApprovalNudgeService>();
        return services;
    }
}
