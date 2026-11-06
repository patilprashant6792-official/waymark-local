namespace Waymark.Host.Components.Ui;

/// <summary>What a tag or status means. Colour is only ever used for one of these, never for decoration.</summary>
public enum Tone
{
    Neutral,
    Ok,
    Warn,
    Error,
    Info
}

/// <summary>The four button looks. A view has at most one Primary.</summary>
public enum ButtonKind
{
    /// <summary>The one white button of a view: the thing most people come here to do.</summary>
    Primary,
    /// <summary>A hairline pill for every other action.</summary>
    Secondary,
    /// <summary>Outlined red. Never filled, so the page does not shout.</summary>
    Danger,
    /// <summary>Text only, for low-key actions inside rows.</summary>
    Ghost
}

/// <summary>Input widths come from a scale instead of per-page pixel values.</summary>
public enum FieldWidth
{
    Small,
    Medium,
    Large,
    Full
}

/// <summary>One choice of a <see cref="WmSegmented{TValue}"/>.</summary>
public sealed record WmOption<TValue>(TValue Value, string Label, string? CssClass = null, bool Disabled = false, string? Title = null);

/// <summary>One tab of <see cref="WmTabs"/>; a Badge above zero draws a count.</summary>
public sealed record WmTab(string Id, string Name, int Badge = 0, string? BadgeLabel = null);
