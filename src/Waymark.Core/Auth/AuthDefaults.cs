namespace Waymark.Core.Auth;

/// <summary>Fixed names and lifetimes of the OAuth server and API keys.</summary>
public static class AuthDefaults
{
    public const string AccessPrefix = "sma_";
    public const string RefreshPrefix = "smr_";
    public const string ApiKeyPrefix = "smk_";

    public const string McpScope = "mcp";
    public const string OfflineAccessScope = "offline_access";

    /// <summary>The actor recorded for decisions made in the admin UI.</summary>
    public const string AdminActor = "admin-ui";

    public const int MaxPendingApprovals = 5;

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ApiKeyDefaultLifetime = TimeSpan.FromDays(90);
}
