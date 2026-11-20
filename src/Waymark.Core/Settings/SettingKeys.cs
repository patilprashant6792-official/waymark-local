namespace Waymark.Core.Settings;

/// <summary>Keys used by the edge (tunnel and public URL) and security settings.</summary>
public static class SettingKeys
{
    /// <summary>Random id of this installation; the public-URL round trip must return it.</summary>
    public const string InstanceId = "edge.instance_id";

    public const string PublicUrl = "edge.public_url";

    public const string PublicUrlVerifiedUtc = "edge.public_url_verified_utc";

    public const string TunnelProvider = "edge.tunnel_provider";

    /// <summary>The ngrok authtoken, encrypted (never plain text).</summary>
    public const string NgrokSecret = "edge.ngrok_secret";

    /// <summary>When true every tool call is rejected (the Pause all switch).</summary>
    public const string Paused = "security.paused";

    /// <summary>True (the default) to keep the input and output of every tool call, after secret hiding, for the call's details page.</summary>
    public const string AuditPayloadsEnabled = "audit.payloads_enabled";

    /// <summary>How many days the kept inputs and outputs are kept (1 to 30).</summary>
    public const string AuditPayloadDays = "audit.payload_days";

    /// <summary>True (the default) to show a desktop notification when a command or an app connection waits for the owner's approval.</summary>
    public const string DesktopNotifications = "ui.desktop_notifications";

    /// <summary>True to make notes and flows searchable by meaning through the chosen provider. False by default: nothing is sent.</summary>
    public const string MemorySemanticEnabled = "memory.semantic_enabled";

    /// <summary>The active embedding provider: voyage, openai or gemini.</summary>
    public const string MemoryProvider = "memory.provider";

    /// <summary>The embedding model of the active provider.</summary>
    public const string MemoryModel = "memory.model";

    /// <summary>The vector size asked of the provider (default 1024).</summary>
    public const string MemoryDimension = "memory.dimension";

    /// <summary>The prefix of the per-provider key settings ('memory.key.voyage'); each value is encrypted, never plain text.</summary>
    public const string MemoryKeyPrefix = "memory.key.";

    /// <summary>Workspace scope: true when the owner excluded the workspace from search by meaning.</summary>
    public const string MemorySemanticExcluded = "memory.semantic_excluded";

    public static string MemoryKey(string provider) => MemoryKeyPrefix + provider;
}
