using Waymark.Host.Components.Shared;

namespace Waymark.Host.Components.Patterns;

/// <summary>How the line leading into a hop is drawn: solid for healthy, dashed amber for held, dashed red for cut, grey for idle.</summary>
public enum HopKind
{
    Ok,
    Hold,
    Bad,
    Off
}

/// <summary>One stop of the trust chain: what it is, the state it is in, and one line of detail.</summary>
public sealed record ChainHop(string Name, HopKind Kind, string State, string? Detail = null)
{
    /// <summary>The dot and word of the hop use the same states as every other status in the app.</summary>
    public StatusState Status => Kind switch
    {
        HopKind.Ok => StatusState.Healthy,
        HopKind.Hold => StatusState.Attention,
        HopKind.Bad => StatusState.Failed,
        _ => StatusState.Off
    };

    public string CssClass => Kind switch
    {
        HopKind.Hold => "hold",
        HopKind.Bad => "bad",
        HopKind.Off => "off",
        _ => string.Empty
    };
}
