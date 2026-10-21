namespace Waymark.Core.Guides;

/// <summary>The limits and wording around the owner's text in the overview.</summary>
public static class HostPromptRules
{
    /// <summary>The module key of the overview, and the key its owner text is stored under.</summary>
    public const string StoreKey = "host";

    /// <summary>
    /// The longest owner text, in characters (about 10,000 tokens). Every agent that loads the overview receives all of it,
    /// so the limit keeps one mistake from filling a context window; a few thousand tokens of instructions fit comfortably.
    /// </summary>
    public const int MaxOwnerChars = 40_000;

    /// <summary>Above this many tokens the admin page warns that the owner's text is expensive for every agent.</summary>
    public const int WarnTokens = 6_000;

    /// <summary>
    /// The owner's text inside the overview, under its own heading and with the rule for conflicts, or nothing while the owner has written nothing.
    /// The block ends with a blank line, so the next heading of the template starts cleanly.
    /// </summary>
    public static string OwnerBlock(string ownerText)
    {
        ArgumentNullException.ThrowIfNull(ownerText);
        return ownerText.Length == 0
            ? string.Empty
            : "## Instructions from the owner\n\n" +
              "These come from the person who runs this Waymark. Follow them. Where they conflict with the defaults in this guide, they win; " +
              "Waymark's gates and approvals still apply and cannot be changed by text.\n\n" +
              ownerText +
              "\n\n";
    }
}
