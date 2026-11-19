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

    // The guide itself is added to this result by the call pipeline (it always counts as asked for), after the secret check and the size limit.
    // With a workspace the pipeline sends that workspace's guide instead, and refuses an unknown or switched-off workspace before this runs.
    [McpServerTool(Name = GuideRules.OverviewTool, ReadOnly = true, Idempotent = true)]
    [Description(
        "Returns the Waymark overview: what each tool group (code, packages, files, commands) does, how approvals and long-running commands work, the owner's own instructions, the workspaces and which tools and gates are switched on now. " +
        "Call it once before your first Waymark call in a conversation, and again only when a result says it changed; each group's detailed usage guide comes separately, with guide: true on your first call to that group. " +
        "With workspace set it returns that workspace's guide instead: the owner's instructions for it, its saved flows and its command gates; call that once before you change anything in the workspace. " +
        "Reads only. An unknown or switched-off workspace is an error.")]
    public static string Overview(
        [Description("Optional: a workspace name as shown in the overview, to receive that workspace's guide. Leave empty (or '*') for the overview.")]
        string? workspace = null) =>
        string.IsNullOrWhiteSpace(workspace) || workspace.Trim() == "*"
            ? "The Waymark overview is in the block above this line."
            : "The guide of that workspace is in the block above this line.";

    [McpServerTool(Name = "waymark_ping", ReadOnly = true, Idempotent = true)]
    [Description(
        "Checks that Waymark is reachable and that this connection is accepted; returns 'Waymark is running' and the server time in UTC (ISO 8601). Takes no arguments, reads nothing and changes nothing. " +
        "Use it only to test the connection, for example after a Waymark call failed with a network or sign-in error. " +
        "Do not use it to start work (call waymark_guide) or to learn the date or the owner's local time (call util_datetime).")]
    public static string Ping() =>
        $"Waymark is running. Server time (UTC): {TimeProvider.System.GetUtcNow().ToString(Iso, CultureInfo.InvariantCulture)}";

    [McpServerTool(Name = "util_datetime", ReadOnly = true, Idempotent = true)]
    [Description(
        "Gets the current date and time from this computer's clock. " +
        "Without timeZoneId it returns two lines: UTC, and the local time with its zone id and offset (ISO 8601). " +
        "With timeZoneId it returns one line: that zone's id and its time with offset; an unknown id is an error that names the expected form. Reads only. " +
        "Use it whenever an answer depends on today's date, a weekday, a deadline or the owner's local time, instead of guessing; " +
        "and to turn a local time into the UTC timestamp code_changes_since expects. waymark_ping only gives the server's UTC time.")]
    public static string CurrentDateTime(
        [Description("Optional time zone id. IANA ids such as 'Europe/London' or 'Asia/Kolkata' work on every system; Windows ids such as 'India Standard Time' work too. Leave empty for UTC plus this computer's local time.")]
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
