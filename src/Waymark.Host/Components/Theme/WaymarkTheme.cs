using MudBlazor;

namespace Waymark.Host.Components.Theme;

/// <summary>
/// Builds the MudBlazor theme from <see cref="DesignSystem"/>. Radius, spacing, motion and the pill shape that MudBlazor
/// has no slot for live in wwwroot/css/theme.css.
/// </summary>
public static class WaymarkTheme
{
    public static MudTheme Create() => new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = DesignSystem.Dark.Primary,
            PrimaryContrastText = DesignSystem.Dark.PrimaryText,
            Secondary = DesignSystem.Dark.TextMuted,
            SecondaryContrastText = DesignSystem.Dark.PrimaryText,
            Success = DesignSystem.Dark.Ok,
            SuccessContrastText = DesignSystem.Dark.Background,
            Warning = DesignSystem.Dark.Warn,
            WarningContrastText = DesignSystem.Dark.Background,
            Error = DesignSystem.Dark.Error,
            ErrorContrastText = DesignSystem.Dark.Background,
            Info = DesignSystem.Dark.Info,
            InfoContrastText = DesignSystem.Dark.Background,
            TextPrimary = DesignSystem.Dark.Text,
            TextSecondary = DesignSystem.Dark.TextMuted,
            TextDisabled = DesignSystem.Dark.TextHint,
            Background = DesignSystem.Dark.Background,
            BackgroundGray = DesignSystem.Dark.SurfaceRaised,
            Surface = DesignSystem.Dark.Surface,
            AppbarBackground = DesignSystem.Dark.Background,
            AppbarText = DesignSystem.Dark.Text,
            DrawerBackground = DesignSystem.Dark.Background,
            DrawerText = DesignSystem.Dark.Text,
            LinesDefault = DesignSystem.Dark.Line,
            LinesInputs = DesignSystem.Dark.LineInput,
            Divider = DesignSystem.Dark.Line,
            DividerLight = DesignSystem.Dark.Line,
            TableLines = DesignSystem.Dark.Line
        },
        PaletteLight = new PaletteLight
        {
            Primary = DesignSystem.Light.Primary,
            PrimaryContrastText = DesignSystem.Light.PrimaryText,
            Secondary = DesignSystem.Light.TextMuted,
            SecondaryContrastText = DesignSystem.Light.PrimaryText,
            Success = DesignSystem.Light.Ok,
            SuccessContrastText = "#FFFFFF",
            Warning = DesignSystem.Light.Warn,
            WarningContrastText = "#FFFFFF",
            Error = DesignSystem.Light.Error,
            ErrorContrastText = "#FFFFFF",
            Info = DesignSystem.Light.Info,
            InfoContrastText = "#FFFFFF",
            TextPrimary = DesignSystem.Light.Text,
            TextSecondary = DesignSystem.Light.TextMuted,
            TextDisabled = DesignSystem.Light.TextHint,
            Background = DesignSystem.Light.Background,
            BackgroundGray = DesignSystem.Light.SurfaceRaised,
            Surface = DesignSystem.Light.Surface,
            AppbarBackground = DesignSystem.Light.Surface,
            AppbarText = DesignSystem.Light.Text,
            DrawerBackground = DesignSystem.Light.Surface,
            DrawerText = DesignSystem.Light.Text,
            LinesDefault = DesignSystem.Light.Line,
            LinesInputs = DesignSystem.Light.LineInput,
            Divider = DesignSystem.Light.Line,
            DividerLight = DesignSystem.Light.Line,
            TableLines = DesignSystem.Light.Line
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            AppbarHeight = "56px"
        },
        Typography = new Typography
        {
            Default = Style(new DefaultTypography(), "0.875rem", "400", "1.5"),
            Body1 = Style(new Body1Typography(), "0.875rem", "400", "1.5"),
            Body2 = Style(new Body2Typography(), "0.8125rem", "400", "1.5"),
            Subtitle1 = Style(new Subtitle1Typography(), "1rem", "600", "1.4"),
            Subtitle2 = Style(new Subtitle2Typography(), "0.875rem", "600", "1.4"),
            H1 = Style(new H1Typography(), "1.75rem", "600", "1.2", "-0.01em"),
            H2 = Style(new H2Typography(), "1.5rem", "600", "1.25", "-0.01em"),
            H3 = Style(new H3Typography(), "1.25rem", "600", "1.3"),
            H4 = Style(new H4Typography(), "1.125rem", "600", "1.35"),
            H5 = Style(new H5Typography(), "1rem", "600", "1.4"),
            H6 = Style(new H6Typography(), "0.875rem", "600", "1.4"),
            Button = Style(new ButtonTypography(), "0.875rem", "500", "1.25"),
            Caption = Style(new CaptionTypography(), "0.75rem", "400", "1.4"),
            Overline = Style(new OverlineTypography(), "0.6875rem", "600", "1.4", "0.06em", "uppercase")
        }
    };

    private static T Style<T>(T type, string size, string weight, string lineHeight, string letterSpacing = "normal", string textTransform = "none")
        where T : BaseTypography
    {
        type.FontFamily = DesignSystem.SansFamily;
        type.FontSize = size;
        type.FontWeight = weight;
        type.LineHeight = lineHeight;
        type.LetterSpacing = letterSpacing;
        type.TextTransform = textTransform;
        return type;
    }
}
