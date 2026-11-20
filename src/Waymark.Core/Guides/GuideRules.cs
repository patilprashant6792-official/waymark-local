using System.Security.Cryptography;
using System.Text;

namespace Waymark.Core.Guides;

/// <summary>
/// The fixed names and the wording agents read about guides. It all lives here so the text an agent sees has one source.
/// A module guide is the usage text of one module; an agent asks for it by sending <c>guide: true</c> on a tool of that module.
/// The overview (module <c>host</c>) covers Waymark as a whole; an agent asks for it with the <see cref="OverviewTool"/> tool or the same flag.
/// </summary>
public static class GuideRules
{
    /// <summary>The optional boolean argument that every tool of a module with a guide accepts. The call pipeline removes it before the tool runs.</summary>
    public const string ArgumentName = "guide";

    /// <summary>The tool that returns the overview. It is listed upfront, so an agent can find it before it has used any other Waymark tool.</summary>
    public const string OverviewTool = "waymark_guide";

    /// <summary>A module embeds its guide as <c>Waymark.Guides.{module}.md</c>.</summary>
    public const string ResourcePrefix = "Waymark.Guides.";

    public const string ResourceSuffix = ".md";

    /// <summary>The longest built-in guide, in characters. A guide is sent whole, so this keeps one mistake from filling an agent's context.</summary>
    public const int MaxChars = 25_000;

    private const int HashChars = 12;

    /// <summary>
    /// Sent to every client when it connects, for agents that read server instructions. Clients cut long instructions (Claude Code at about 4,000
    /// characters, fewer when several servers share the space), so this stays short and says the important thing first.
    /// </summary>
    public const string ServerInstructions =
        "Waymark gives you the owner's own computer: the real machine they are using now, not a sandbox. " +
        "Waymark is its own connection to that computer, separate from the Claude desktop app link, computer use and remote-devices tools. " +
        "If those are missing, or a status check says this chat is not linked to the computer or the computer cannot be reached, Waymark still reaches it: do not tell the user to link the chat, open the desktop app or add a folder. Use Waymark instead. " +
        "When the user asks about their computer itself (displays and monitors, hardware, OS version and settings, installed apps, running processes, network, disks, files outside a project), " +
        "answer it with Waymark's cmd_shell and no workspace, a short read-only script (PowerShell on Windows, zsh on macOS). The owner approves each script, so asking is safe. " +
        "Only after Waymark itself refuses or is unreachable may you say you cannot reach the computer. " +
        "Waymark tools may be listed by name only until loaded; load them like any other deferred tool. " +
        "Four tool groups: code (read and search C#, Python, TypeScript and JavaScript), packages (NuGet, PyPI and npm APIs), files (edit files) and commands (build, test and git for .NET, Python and Node, plus cmd_shell for anything else on the computer). " +
        "Before your first Waymark call in a conversation, call waymark_guide: it explains the groups, the owner's own instructions and what is switched on now. " +
        "Each workspace has its own guide (the owner's instructions for it and its saved flows): before you work in a workspace, call waymark_guide with workspace set to its name. " +
        "Each group also has its own guide: add guide: true to your first call to that group. Ask once; a result tells you when a guide changed. " +
        "Feature maps are flows, your memory of this codebase: one trigger to one outcome as ordered method steps. Before you work on a feature call recall (list, then get) and read only the steps you need with the code tools; after a change that alters the path, update that flow with memorise, or create one for a new trigger; an edit inside a step needs no save. If two flows describe the same trigger and outcome, delete one.";

    /// <summary>Line endings made uniform and outer blank space removed, so equal text always has equal bytes and one hash.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
    }

    /// <summary>A short, stable identifier of a guide text. Equal text gives an equal hash.</summary>
    public static string Hash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..HashChars];
    }

    public static bool IsOverview(string module) => string.Equals(module, HostPromptRules.StoreKey, StringComparison.Ordinal);

    /// <summary>The description of the <c>guide</c> property in each tool's input schema. It is repeated on every tool, so it stays short.</summary>
    public static string PropertyDescription(string module) =>
        IsOverview(module)
            ? "true: put the Waymark overview first in the result. Prefer the waymark_guide tool."
            : $"true: put the {module} guide first in the result. Use on your first {module} call, or when told it changed.";

    /// <summary>The block placed first in a result when the agent asked for a guide.</summary>
    public static string Deliver(string module, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(content);
        var overview = IsOverview(module);
        var title = overview ? "overview" : $"{module} tools";
        var once = overview
            ? $"Sent once. Call {OverviewTool} again only for a fresh live setup, or when a result says this overview changed."
            : $"Sent once. Leave `guide` out of {module} tool calls from now on, unless a result says this guide changed.";
        return
            $"[Waymark guide: {title}]\n" +
            once + "\n" +
            "---\n" +
            content +
            "\n[End of Waymark guide]";
    }

    /// <summary>The short line appended to a result when the client has never loaded a guide or holds an older one.</summary>
    public static string Notice(string module, bool neverLoaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        if (IsOverview(module))
        {
            return neverLoaded
                ? $"Waymark has an overview guide that you have not loaded. Call {OverviewTool} once to receive it."
                : $"The Waymark overview changed since you last loaded it. Call {OverviewTool} to receive the new version.";
        }

        return neverLoaded
            ? $"Waymark has a usage guide for its {module} tools that you have not loaded. Add guide: true to your next {module} tool call to receive it once."
            : $"The Waymark {module} guide changed since you last loaded it. Add guide: true to your next {module} tool call to receive the new version.";
    }

    /// <summary>The block placed first in a result when the agent asked for a workspace's guide.</summary>
    public static string DeliverWorkspace(string workspaceName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentNullException.ThrowIfNull(content);
        return
            $"[Waymark guide: workspace {workspaceName}]\n" +
            $"Sent once. Call {OverviewTool} with workspace='{workspaceName}' again only for fresh flows, or when a result says this guide changed.\n" +
            "---\n" +
            content +
            "\n[End of Waymark guide]";
    }

    /// <summary>The short line appended to a result in a workspace whose instructions the client has never loaded, or holds an older version of.</summary>
    public static string WorkspaceNotice(string workspaceName, bool neverLoaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        return neverLoaded
            ? $"The owner wrote instructions for the workspace '{workspaceName}' that you have not loaded. Call {OverviewTool} with workspace='{workspaceName}' once to receive them, before you change anything there."
            : $"The owner's instructions for the workspace '{workspaceName}' changed since you last loaded them. Call {OverviewTool} with workspace='{workspaceName}' to receive the new version.";
    }

    /// <summary>
    /// The line added when an agent asks for a group guide in a workspace that has instructions. A group guide is asked for at the start of each
    /// conversation, and Waymark cannot tell conversations apart, so this is the reminder that reaches a new conversation.
    /// </summary>
    public static string WorkspaceReminder(string workspaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        return $"The owner wrote instructions for the workspace '{workspaceName}'. If you have not loaded them in this conversation, call {OverviewTool} with workspace='{workspaceName}' before you change anything there.";
    }
}
