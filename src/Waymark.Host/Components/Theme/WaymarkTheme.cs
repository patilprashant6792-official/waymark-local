using MudBlazor;

namespace Waymark.Host.Components.Theme;

/// <summary>
/// The design-system tokens from the UX doc. Input borders use LinesInputs, which is deliberately stronger than the
/// decorative divider colour so form fields meet the 3:1 contrast needed for UI boundaries.
/// </summary>
public static class WaymarkTheme
{
    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2563EB",
            PrimaryContrastText = "#FFFFFF",
            Success = "#15803D",
            Warning = "#B45309",
            Error = "#B91C1C",
            Info = "#0369A1",
            TextPrimary = "#14171C",
            TextSecondary = "#4F5866",
            Background = "#F6F7F9",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#14171C",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#14171C",
            LinesDefault = "#C9CED6",
            LinesInputs = "#6B7280"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7AA7FF",
            PrimaryContrastText = "#0B1220",
            Success = "#4ADE80",
            Warning = "#FBBF24",
            Error = "#F87171",
            Info = "#38BDF8",
            TextPrimary = "#E8EAEE",
            TextSecondary = "#9AA3B2",
            Background = "#14171C",
            Surface = "#1B1F27",
            AppbarBackground = "#1B1F27",
            AppbarText = "#E8EAEE",
            DrawerBackground = "#1B1F27",
            DrawerText = "#E8EAEE",
            LinesDefault = "#4A5361",
            LinesInputs = "#8A94A6"
        }
    };
}
