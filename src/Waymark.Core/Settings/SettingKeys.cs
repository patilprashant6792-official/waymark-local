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
}
