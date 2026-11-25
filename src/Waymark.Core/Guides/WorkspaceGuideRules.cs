using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Waymark.Core.Guides;

/// <summary>
/// The limits and wording of a workspace's guide: the owner's summary (listed in the overview) and instructions (only in the workspace's guide).
/// A workspace guide travels through the same delivery tracking as the module guides, under the module key <c>ws:{id}</c>.
/// The id, not the name, is the key, so the key stays the same for the life of the workspace.
/// </summary>
public static class WorkspaceGuideRules
{
    public const string ModulePrefix = "ws:";

    /// <summary>The longest summary. It is listed in the overview for every agent, so it stays one short line.</summary>
    public const int MaxSummaryChars = 200;

    /// <summary>
    /// The longest instructions, in characters (about 5,000 tokens). Only agents that work in the workspace receive them, but they receive all of them,
    /// so the limit keeps one mistake from filling a context window.
    /// </summary>
    public const int MaxInstructionsChars = 20_000;

    /// <summary>Above this many tokens the admin page warns that the instructions are expensive for every agent that works in the workspace.</summary>
    public const int WarnTokens = 3_000;

    public static string ModuleKey(long workspaceId) => ModulePrefix + workspaceId.ToString(CultureInfo.InvariantCulture);

    public static bool IsWorkspaceModule([NotNullWhen(true)] string? module) =>
        module is not null && module.StartsWith(ModulePrefix, StringComparison.Ordinal);

    public static bool TryParseModuleKey(string? module, out long workspaceId)
    {
        workspaceId = 0;
        return IsWorkspaceModule(module)
            && long.TryParse(module.AsSpan(ModulePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out workspaceId)
            && workspaceId > 0;
    }

    /// <summary>The summary as one line: line breaks become spaces, runs of white space collapse, outer space is removed.</summary>
    public static string NormalizeSummary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The owner's instructions inside the workspace guide, under their own heading and with the rule for conflicts, or nothing while there are none.
    /// The block ends with a blank line, so the next section starts cleanly.
    /// </summary>
    public static string InstructionsBlock(string workspaceName, string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentNullException.ThrowIfNull(instructions);
        return instructions.Length == 0
            ? string.Empty
            : "## Instructions from the owner for this workspace\n\n" +
              $"These come from the person who runs this Waymark and apply to work in the workspace '{workspaceName}'. Follow them. " +
              "Where they conflict with the owner's instructions in the overview, these win for this workspace; " +
              "Waymark's gates and approvals still apply and cannot be changed by text.\n\n" +
              instructions +
              "\n\n";
    }

    /// <summary>
    /// The part of a workspace guide that identifies it: name, summary and instructions, without the live parts (flows, gates), which change
    /// whenever an agent saves a flow and must not make every client look outdated.
    /// </summary>
    public static string Stable(string workspaceName, WorkspaceGuideText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return workspaceName + "\n" + text.Summary + "\n" + InstructionsBlock(workspaceName, text.Instructions);
    }

    public static string Hash(string workspaceName, WorkspaceGuideText text) => GuideRules.Hash(Stable(workspaceName, text));

    /// <summary>
    /// The hash with the owner's pinned notes in it (each 'id:content hash:title'), so a client hears when a pin changes. Without pins it equals <see cref="Hash(string, WorkspaceGuideText)"/>.
    /// </summary>
    public static string Hash(string workspaceName, WorkspaceGuideText text, IReadOnlyList<string> pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        return pinned.Count == 0
            ? Hash(workspaceName, text)
            : GuideRules.Hash(Stable(workspaceName, text) + "\npinned:" + string.Join(",", pinned));
    }

    /// <summary>
    /// The hash with the pinned notes and whether search by meaning is on. Search by meaning changes what the guide asks of an agent (the flows are no
    /// longer listed; memory_recall finds them), so switching it makes a client look outdated. With it off the hash equals
    /// <see cref="Hash(string, WorkspaceGuideText, IReadOnlyList{string})"/>, so a workspace with search by meaning off keeps the hash it had.
    /// </summary>
    public static string Hash(string workspaceName, WorkspaceGuideText text, IReadOnlyList<string> pinned, bool meaningOn)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (!meaningOn)
        {
            return Hash(workspaceName, text, pinned);
        }

        var pins = pinned.Count == 0 ? string.Empty : "\npinned:" + string.Join(",", pinned);
        return GuideRules.Hash(Stable(workspaceName, text) + pins + "\nmeaning:on");
    }
}
