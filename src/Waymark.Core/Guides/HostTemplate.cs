namespace Waymark.Core.Guides;

/// <summary>
/// The overview template: text with two markers, <c>{{owner}}</c> where the owner's instructions go and <c>{{setup}}</c> where the live setup goes.
/// It is cut into three parts once, so a marker inside the owner's text is never treated as a marker.
/// </summary>
public sealed class HostTemplate
{
    public const string OwnerMarker = "{{owner}}";
    public const string SetupMarker = "{{setup}}";

    private HostTemplate(string beforeOwner, string betweenOwnerAndSetup, string afterSetup)
    {
        BeforeOwner = beforeOwner;
        BetweenOwnerAndSetup = betweenOwnerAndSetup;
        AfterSetup = afterSetup;
    }

    public string BeforeOwner { get; }

    public string BetweenOwnerAndSetup { get; }

    public string AfterSetup { get; }

    /// <summary>Throws <see cref="InvalidOperationException"/> unless each marker appears exactly once and the owner marker comes first.</summary>
    public static HostTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var owner = Single(text, OwnerMarker);
        var setup = Single(text, SetupMarker);
        if (owner > setup)
        {
            throw new InvalidOperationException($"In the overview template {OwnerMarker} must come before {SetupMarker}.");
        }

        // The marker sits on a line of its own; the line break after it is not part of the text that follows the owner's block.
        return new HostTemplate(
            text[..owner],
            text[(owner + OwnerMarker.Length)..setup].TrimStart('\r', '\n'),
            text[(setup + SetupMarker.Length)..]);
    }

    /// <summary>The text that identifies the overview: the template with the owner's block and without the live setup.</summary>
    public string Stable(string ownerBlock) => BeforeOwner + ownerBlock + BetweenOwnerAndSetup + AfterSetup;
    /// <summary>The text an agent receives.</summary>
    public string Render(string ownerBlock, string setup) => BeforeOwner + ownerBlock + BetweenOwnerAndSetup + setup + AfterSetup;

    private static int Single(string text, string marker)
    {
        var first = text.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || text.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"The overview template must contain {marker} exactly once.");
        }

        return first;
    }
}
