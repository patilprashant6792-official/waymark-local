using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waymark.Core.Approvals;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Commands;
using Waymark.Core.Code;
using Waymark.Core.Guides;
using Waymark.Core.NuGetIndex;
using Waymark.Core.Settings;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Core.Vault;
using Waymark.Core.Security;
using Waymark.Data.Sqlite;
using Waymark.Data.Stores;

namespace Waymark.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Registers the data layer with fixed options.</summary>
    public static IServiceCollection AddWaymarkData(this IServiceCollection services, WaymarkDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddWaymarkData(_ => options);
    }

    /// <summary>
    /// Registers the SQLite connection factory, the single-writer queue, the migrator and the stores.
    /// The options are built when first needed, so they see the final configuration.
    /// The container disposes the write queue on shutdown, which drains pending writes.
    /// Call DatabaseInitializer.InitializeAsync once before the listeners start.
    /// </summary>
    public static IServiceCollection AddWaymarkData(this IServiceCollection services, Func<IServiceProvider, WaymarkDataOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(optionsFactory);
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<SqliteWriteQueue>();
        services.AddSingleton<IWriteQueue>(sp => sp.GetRequiredService<SqliteWriteQueue>());
        services.AddSingleton<Migrator>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddSingleton<IConfigStore, ConfigStore>();
        services.AddSingleton<IOAuthClientStore, OAuthClientStore>();
        services.AddSingleton<IApprovalStore, ApprovalStore>();
        services.AddSingleton<IAuditStore, AuditStore>();
        services.AddSingleton<IGrantStore, GrantStore>();
        services.AddSingleton<IAccessKeyStore, AccessKeyStore>();
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.AddSingleton<IToolSettingsStore, ToolSettingsStore>();
        services.AddSingleton<ICodeIndexStore, CodeIndexStore>();
        services.AddSingleton<ICodeSearchStore, CodeSearchStore>();
        services.AddSingleton<INuGetIndexStore, NuGetIndexStore>();
        services.AddSingleton<INuGetMetadataStore, NuGetMetadataStore>();
        services.AddSingleton<IVaultStore, VaultStore>();
        services.AddSingleton<INuGetCacheStore, NuGetCacheStore>();
        services.AddSingleton<IAccessRuleStore, AccessRuleStore>();
        services.AddSingleton<ICommandRunStore, CommandRunStore>();
        services.AddSingleton<ICommandPolicyStore, CommandPolicyStore>();
        services.AddSingleton<IGuideStore, GuideStore>();
        return services;
    }
}
