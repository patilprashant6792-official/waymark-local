using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Guides;

namespace Waymark.Host.Mcp;

/// <summary>Tools that belong to Waymark itself rather than to one of its modules.</summary>
[McpServerToolType]
public sealed class WaymarkTools
{
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string IsoOffset = "yyyy-MM-dd'T'HH:mm:sszzz";

    // The overview itself is added to this result by the call pipeline (it always counts as asked for), after the secret check and the size limit.
    [McpServerTool(Name = GuideRules.OverviewTool, ReadOnly = true, Idempotent = true)]
    [Description("Returns the Waymark overview: what each tool group does, how approvals and long-running commands work, the owner's own instructions and what is switched on now. Call it once before your first Waymark call in a conversation, and again only when a result says it changed.")]
    public static string Overview() => "The Waymark overview is in the block above this line.";

    [McpServerTool(Name = "waymark_ping", ReadOnly = true, Idempotent = true)]
    [Description("Checks that Waymark is reachable and signed in. Returns the server time in UTC.")]
    public static string Ping() =>
        $"Waymark is running. Server time (UTC): {TimeProvider.System.GetUtcNow().ToString(Iso, CultureInfo.InvariantCulture)}";

    [McpServerTool(Name = "util_datetime", ReadOnly = true, Idempotent = true)]
    [Description("Gets the current date and time in UTC and in this computer's time zone, or in a specific time zone.")]
    public static string CurrentDateTime(
        [Description("Optional time zone id such as 'Europe/London' or 'India Standard Time'. Leave empty for UTC and local time.")]
        string? timeZoneId = null)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
            return $"UTC: {now.ToString(Iso, CultureInfo.InvariantCulture)}\nLocal ({TimeZoneInfo.Local.Id}): {local.ToString(IsoOffset, CultureInfo.InvariantCulture)}";
        }

        var id = timeZoneId.Trim();
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return $"{zone.Id}: {TimeZoneInfo.ConvertTime(now, zone).ToString(IsoOffset, CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new McpException($"Unknown time zone '{id}'. Use an IANA id such as 'Europe/London'.");
        }
    }
}
