using System.ComponentModel;
using ModelContextProtocol.Server;
using Waymark.Core.Guides;
using Waymark.Core.Tools;

namespace Waymark.Host.Mcp;

/// <summary>Tools that belong to Waymark itself rather than to one of its modules.</summary>
[McpServerToolType]
public sealed class WaymarkTools
{
    // The guide itself is added to this result by the call pipeline (it always counts as asked for), after the secret check and the size limit.
    // With a workspace the pipeline sends that workspace's guide instead, and refuses an unknown or switched-off workspace before this runs.
    // Internal: always on and not on the Tools page. It is the way in, and a successful call also proves the connection works.
    [McpServerTool(Name = GuideRules.OverviewTool, ReadOnly = true, Idempotent = true)]
    [InternalTool]
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
}
