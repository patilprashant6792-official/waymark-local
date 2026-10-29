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
    public const int MaxChars = 20_000;

    private const int HashChars = 12;

    /// <summary>
    /// Sent to every client when it connects, for agents that read server instructions. Clients cut long instructions (Claude Code at about 4,000
    /// characters, fewer when several servers share the space), so this stays short and says the important thing first.
    /// </summary>
    public const string ServerInstructions =
        "Waymark gives you the owner's computer through four tool groups: code (read and search C#, Python, TypeScript and JavaScript), packages (NuGet, PyPI and npm APIs), files (edit files) and commands (build, test, git, shell for .NET, Python and Node). " +
        "Before your first Waymark call in a conversation, call waymark_guide: it explains the groups, the owner's own instructions and what is switched on now. " +
        "Each group also has its own guide: add guide: true to your first call to that group. Ask once; a result tells you when a guide changed.";

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
}
