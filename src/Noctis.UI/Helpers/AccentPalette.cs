using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// The accent overlay: SystemAccentColor and its Dark1-3 / Light1-3 shades, and every brush
/// that fans out from it (accent fills and text, the now-playing row, toggle and check-glyph
/// foregrounds, menu hovers), with the contrast guards for pale accents and light themes.
/// Lifted verbatim from the desktop App.SetAccent so the desktop and the phone merge the
/// same dictionary; the caller merges it last so it beats a theme overlay's own accent.
/// </summary>
public static class AccentPalette
{
    public static ResourceDictionary Build(Color color, bool isLightTheme)
    {
        var dark1  = Mix(color, Colors.Black, 0.15);
        var dark2  = Mix(color, Colors.Black, 0.30);
        var dark3  = Mix(color, Colors.Black, 0.45);
        var light1 = Mix(color, Colors.White, 0.15);
        var light2 = Mix(color, Colors.White, 0.30);
        var light3 = Mix(color, Colors.White, 0.45);
        var accentForeground = GetReadableForeground(color);
        // Row text / EQ bars / icons on the now-playing track box: white on dark themes,
        // black on light ones. Deliberately theme-driven rather than derived from the
        // accent's own luminance — flipping per-accent made the row read as mismatched
        // against the rest of the list, and a solid accent band with constant text is what
        // the design targets.
        //
        // The one thing that outranks that consistency is being able to read the row at all.
        // The constant was previously unconditional, which put white text on a near-white
        // band whenever the accent was pale (the Dark theme's silver, a white or pastel
        // custom accent) and left the row rendering as a blank bar. So the constant holds
        // only while it clears a 3:1 floor against the band; below that the row takes
        // whichever of black/white actually contrasts. Every accent that reads either way
        // keeps the theme colour, so this changes nothing for the common ones.
        var themeRowForeground = isLightTheme ? Colors.Black : Colors.White;
        // The now-playing row is the accent on every theme (Ink's pinned blue row read as
        // "the theme changes my accent", 09-17).
        var rowColor = color;
        var nowPlayingRowForeground = ContrastRatio(themeRowForeground, rowColor) >= 3.0
            ? themeRowForeground
            : HighestContrastForeground(rowColor);
        IBrush accentButtonBackground = new SolidColorBrush(color);
        // Outline around accent-filled pills. Only meaningful when the accent fill
        // would be indistinguishable from the page background — in practice that's
        // a white / very-light accent on the Light theme. In every other case the
        // outline is visual noise, so make it fully transparent.
        var accentBorder = (isLightTheme && IsLight(color))
            ? Mix(color, Colors.Black, 0.25)
            : Color.FromArgb(0, 0, 0, 0);
        // Page-bg-aware accent for *text* / icon foregrounds drawn on the main surface.
        // Falls back to a darkened/lightened accent when the accent itself would blend
        // into the current page background.
        var accentText = isLightTheme
            ? (IsLight(color) ? Mix(color, Colors.Black, 0.55) : color)
            : (IsLight(color) ? color : Mix(color, Colors.White, 0.55));
        // Exact accent for text, adjusted only when the raw accent lacks contrast
        // against the current page background. Thresholds approximate a 3:1
        // contrast ratio vs the dark (#252525) and light page backgrounds.
        var lum = Luminance(color);
        var accentTextExact = isLightTheme
            ? (lum >= 0.28 ? Mix(color, Colors.Black, 0.45) : color)
            : (lum <= 0.15 ? Mix(color, Colors.White, 0.45) : color);

        var rd = new ResourceDictionary
        {
            ["SystemAccentColor"] = color,
            ["SystemAccentColorDark1"] = dark1,
            ["SystemAccentColorDark2"] = dark2,
            ["SystemAccentColorDark3"] = dark3,
            ["SystemAccentColorLight1"] = light1,
            ["SystemAccentColorLight2"] = light2,
            ["SystemAccentColorLight3"] = light3,
            ["SystemControlHighlightAccentBrush"]  = new SolidColorBrush(color),
            ["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(light1),
            ["AccentColorBrush"]                   = new SolidColorBrush(color),
            // Fill for accent-filled action buttons. Identical to AccentColorBrush here;
            // it exists as its own key so MainWindow's Liquid Glass overlay can frost the
            // buttons without making every accent surface (sliders, now-playing row,
            // sidebar selection, drag preview) translucent too.
            ["AccentButtonBackground"]             = accentButtonBackground,
            ["AccentForegroundBrush"]              = new SolidColorBrush(accentForeground),
            ["AccentBorderBrush"]                  = new SolidColorBrush(accentBorder),
            ["AccentTextBrush"]                    = new SolidColorBrush(accentText),
            ["AccentTextExactBrush"]               = new SolidColorBrush(accentTextExact),
            ["AccentColorBrushLight1"]             = new SolidColorBrush(light1),
            ["AccentColorBrushDark1"]              = new SolidColorBrush(dark1),

            // Now-playing track row box. Previously retinted at runtime from the current
            // artwork's vibrant colour, which ignored the user's accent; it now follows the
            // accent like every other accent-filled surface.
            ["NowPlayingRowBrush"]           = new SolidColorBrush(rowColor),
            ["NowPlayingRowForegroundBrush"] = new SolidColorBrush(nowPlayingRowForeground),
            ["ToggleSwitchFillOn"]                 = new SolidColorBrush(color),
            ["ToggleSwitchFillOnPointerOver"]      = new SolidColorBrush(light1),
            ["ToggleSwitchFillOnPressed"]          = new SolidColorBrush(dark1),
            ["ToggleSwitchFillOnDragging"]         = new SolidColorBrush(light1),
            ["IslandIconAccent"]                   = new SolidColorBrush(color),

            // Fluent paints its accent-filled control states (checked ToggleButton,
            // CheckBox tick, RadioButton dot) with a hardcoded white foreground. The
            // fill above follows the accent, the foreground did not — so a white/very
            // light accent rendered white-on-white (invisible "Raw" pill label, tick,
            // radio dot). Re-point them at the same readable foreground the app's own
            // accent pills use.
            ["ToggleButtonForegroundChecked"]            = new SolidColorBrush(accentForeground),
            ["ToggleButtonForegroundCheckedPointerOver"] = new SolidColorBrush(accentForeground),
            ["ToggleButtonForegroundCheckedPressed"]     = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundChecked"]            = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = new SolidColorBrush(accentForeground),
            ["CheckBoxCheckGlyphForegroundCheckedPressed"]     = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFill"]            = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFillPointerOver"] = new SolidColorBrush(accentForeground),
            ["RadioButtonCheckGlyphFillPressed"]     = new SolidColorBrush(accentForeground),
            // Slider fill/thumb tracks the accent so every slider (seek, volume,
            // volume-adjust, pre-amp, island) is uniformly accent-coloured.
            ["IslandSliderFilled"]                 = new SolidColorBrush(color),

            // Accent-tinted hover/press for menu items so dropdowns match the active accent
            ["MenuFlyoutItemBackgroundPointerOver"]    = new SolidColorBrush(color, 0.22),
            ["MenuFlyoutItemBackgroundPressed"]       = new SolidColorBrush(color, 0.35),
            ["MenuFlyoutSubItemBackgroundPointerOver"] = new SolidColorBrush(color, 0.22),
            ["MenuFlyoutSubItemBackgroundPressed"]    = new SolidColorBrush(color, 0.35),
            ["MenuFlyoutSubItemBackgroundSubMenuOpened"] = new SolidColorBrush(color, 0.22),
            ["MenuBarItemBackgroundPointerOver"]      = new SolidColorBrush(color, 0.22),
            ["MenuBarItemBackgroundPressed"]          = new SolidColorBrush(color, 0.35),
            ["MenuBarItemBackgroundSelected"]         = new SolidColorBrush(color, 0.22),
        };

        return rd;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bl);
    }

    private static double Luminance(Color c)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) +
               0.7152 * Linear(c.G) +
               0.0722 * Linear(c.B);
    }

    private static Color GetReadableForeground(Color background)
    {
        // Bias toward white: only switch to black when the accent is light enough
        // that white-on-accent would be unreadable (e.g. white, pale yellow, mint).
        return Luminance(background) >= 0.6 ? Colors.Black : Colors.White;
    }

    private static bool IsLight(Color c) => GetReadableForeground(c) == Colors.Black;

    /// <summary>WCAG contrast ratio between two opaque colours (1.0 = identical, 21.0 = black on white).</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Black or white, whichever is more legible on <paramref name="background"/>. Unlike
    /// GetReadableForeground — which is tuned for small glyphs and biases toward white — this
    /// makes no aesthetic choice; it is the last-resort pick for a large filled band.
    /// </summary>
    private static Color HighestContrastForeground(Color background) =>
        ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;
}
