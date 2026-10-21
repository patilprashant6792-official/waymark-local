using System.Globalization;

namespace Waymark.Host.Terminal;

/// <summary>
/// How a run of characters looks. A colour is -1 (the theme's own), 0-255 (the xterm palette) or a true colour stored as 0x1000000 plus the rgb value.
/// The 16 basic colours are CSS classes so that light and dark themes can set them; the others become an inline colour.
/// </summary>
public sealed record TerminalStyle(int Foreground, int Background, bool Bold, bool Dim, bool Italic, bool Underline, bool Inverse)
{
    public const int TrueColorFlag = 0x1000000;

    public static readonly TerminalStyle Plain = new(-1, -1, false, false, false, false, false);

    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>CSS classes, or null when the style is the plain one.</summary>
    public string? CssClass
    {
        get
        {
            var (foreground, background) = Inverse ? (Background, Foreground) : (Foreground, Background);
            var classes = new List<string>(4);
            if (Inverse && foreground < 0 && background < 0)
            {
                classes.Add("inv");
            }

            if (foreground is >= 0 and < 16)
            {
                classes.Add("fg" + foreground.ToString(CultureInfo.InvariantCulture));
            }

            if (background is >= 0 and < 16)
            {
                classes.Add("bg" + background.ToString(CultureInfo.InvariantCulture));
            }

            if (Bold)
            {
                classes.Add("b");
            }

            if (Dim)
            {
                classes.Add("d");
            }

            if (Italic)
            {
                classes.Add("i");
            }

            if (Underline)
            {
                classes.Add("u");
            }

            return classes.Count == 0 ? null : string.Join(' ', classes);
        }
    }

    /// <summary>An inline style for the colours that are not one of the 16 basic ones, or null.</summary>
    public string? InlineStyle
    {
        get
        {
            var (foreground, background) = Inverse ? (Background, Foreground) : (Foreground, Background);
            var foregroundCss = foreground >= 16 ? "color:" + Hex(foreground) : null;
            var backgroundCss = background >= 16 ? "background-color:" + Hex(background) : null;
            return (foregroundCss, backgroundCss) switch
            {
                (null, null) => null,
                (not null, null) => foregroundCss,
                (null, not null) => backgroundCss,
                _ => foregroundCss + ";" + backgroundCss,
            };
        }
    }

    private static string Hex(int color)
    {
        int red;
        int green;
        int blue;
        if ((color & TrueColorFlag) != 0)
        {
            red = (color >> 16) & 0xFF;
            green = (color >> 8) & 0xFF;
            blue = color & 0xFF;
        }
        else if (color < 232)
        {
            var index = color - 16;
            red = CubeLevels[index / 36 % 6];
            green = CubeLevels[index / 6 % 6];
            blue = CubeLevels[index % 6];
        }
        else
        {
            red = green = blue = 8 + ((color - 232) * 10);
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{red:x2}{green:x2}{blue:x2}");
    }
}
