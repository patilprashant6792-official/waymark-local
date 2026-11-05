namespace Waymark.Host.Components.Theme;

/// <summary>
/// The single source of colour and type for the whole product. WaymarkTheme feeds these to MudBlazor (which turns them
/// into --mud-palette-* variables), theme.css gives them --wm-* names, and the pairing pages build their inline CSS from
/// them. Change a value here and every surface follows.
/// </summary>
public static class DesignSystem
{
    /// <summary>The sans stack. Inter is used when installed; Segoe UI Variable is the native Windows fallback.</summary>
    public static readonly string[] SansFamily = ["Inter", "Segoe UI Variable Text", "Segoe UI", "system-ui", "-apple-system", "sans-serif"];

    public const string MonoFamily = "\"Cascadia Mono\", ui-monospace, Consolas, monospace";

    /// <summary>The dark palette is the product default.</summary>
    public static class Dark
    {
        public const string Background = "#0D0D0D";
        public const string Surface = "#141414";
        public const string SurfaceRaised = "#1A1A1A";
        public const string Line = "#262626";
        // 3:1 against the background, so form fields keep a visible boundary.
        public const string LineInput = "#666666";
        public const string Text = "#ECECEC";
        public const string TextMuted = "#A3A3A3";
        // 4.5:1 on the background: hints stay readable.
        public const string TextHint = "#8A8A8A";
        public const string Primary = "#ECECEC";
        public const string PrimaryText = "#0D0D0D";
        public const string Ok = "#4ADE80";
        public const string Warn = "#FBBF24";
        public const string Error = "#F87171";
        public const string Info = "#7AA7FF";
    }

    public static class Light
    {
        public const string Background = "#F7F7F8";
        public const string Surface = "#FFFFFF";
        public const string SurfaceRaised = "#F0F0F1";
        public const string Line = "#E4E4E7";
        public const string LineInput = "#80808A";
        public const string Text = "#0D0D0D";
        public const string TextMuted = "#52525B";
        public const string TextHint = "#6B6B75";
        public const string Primary = "#0D0D0D";
        public const string PrimaryText = "#FFFFFF";
        public const string Ok = "#15803D";
        public const string Warn = "#B45309";
        public const string Error = "#B91C1C";
        public const string Info = "#1D4ED8";
    }
}
