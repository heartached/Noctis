using System;
using Avalonia;
using Avalonia.Media;
using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// Turns the current artwork's vibrant colour into the visualizer's fill: a vertical
/// gradient with the artwork colour at the base and a lighter tint toward the tips (for
/// Mirror, the tint runs outward from the centre line). Pure and allocation-light — one
/// brush per artwork/style change, never per frame.
///
/// Guards the covers where "the album's colour" would vanish on the dark scrim: near-grey
/// covers have no hue worth painting (caller falls back to the theme accent), and dark
/// covers are lifted to a lightness floor so the bars stay visible.
/// </summary>
public static class VisualizerPalette
{
    /// <summary>Below this saturation the colour reads as grey: no artwork tint.</summary>
    public const double MinSaturation = 0.12;

    /// <summary>Lightness floor for the base colour so dark covers still show.</summary>
    public const double BaseLightnessFloor = 0.52;

    /// <summary>Lightness ceiling for the base so near-white covers keep some colour.</summary>
    public const double BaseLightnessCeiling = 0.68;

    /// <summary>How much lighter the tips are than the base.</summary>
    public const double TipLift = 0.22;

    /// <summary>
    /// The gradient brush for <paramref name="artwork"/> in <paramref name="style"/>, or null
    /// when there is no usable colour (no artwork, or too grey) and the caller's fallback
    /// fill should be used.
    /// </summary>
    public static LinearGradientBrush? Build(Color? artwork, VisualizerStyle style)
    {
        if (artwork is not { } c) return null;
        var (h, s, l) = RgbToHsl(c);
        if (s < MinSaturation) return null;

        var (baseColor, tipColor) = Colors(h, s, l);
        return style == VisualizerStyle.Mirror
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(tipColor, 0.0),
                    new GradientStop(baseColor, 0.5),
                    new GradientStop(tipColor, 1.0),
                },
            }
            : new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(baseColor, 0.0),
                    new GradientStop(tipColor, 1.0),
                },
            };
    }

    /// <summary>Base and tip colours for an HSL artwork colour (exposed for tests).</summary>
    public static (Color Base, Color Tip) Colors(double h, double s, double l)
    {
        var sat = Math.Clamp(s * 1.1, MinSaturation, 1.0); // a touch more vivid than the cover
        var baseL = Math.Clamp(l, BaseLightnessFloor, BaseLightnessCeiling);
        var tipL = Math.Min(0.92, baseL + TipLift);
        return (HslToColor(h, sat, baseL), HslToColor(h, sat * 0.85, tipL));
    }

    public static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color HslToColor(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb(To8((r + m)), To8((g + m)), To8((b + m)));
    }

    private static byte To8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
