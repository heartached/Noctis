using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Noctis.Services;

/// <summary>
/// GitHub #240: Settings › Appearance › "Tint window with accent colour". Shades the theme's
/// window and page background a little toward the accent. App.SetAccent adds the shaded
/// brushes to its accent overlay (merged last, so it wins over every theme, built-in, custom
/// or pack), which keeps everything that paints those brushes in step: the window, the area
/// around the page (PageSurround), every page, menus, dialogs and the mini player. Liquid
/// Glass reads the shaded colour back from the Application (MainWindow.ApplyLiquidGlass).
/// Album Page Colour and the lyrics page paint their own colours over these, so they win.
/// </summary>
internal static class AccentWindowTint
{
    /// <summary>How far toward the accent, on dark themes: enough to read as the accent's
    /// colour, little enough that the background stays a background.</summary>
    public const double DarkStrength = 0.08;

    /// <summary>Light themes show a shade more readily (a white background), so less.</summary>
    public const double LightStrength = 0.05;

    /// <summary>The most of the text's contrast against the page the shade may cost: a bright
    /// accent (Lemon, Arctic) on a dark theme, or a deep one on a light theme, gets a smaller
    /// step so the background never brightens (or darkens) toward the text.</summary>
    public const double MaxContrastLoss = 0.10;

    /// <summary>The theme surfaces that take the shade.</summary>
    internal static readonly string[] Keys = { "AppMainBackground", "AppMainBackgroundColor", "AppWindowBackgroundBrush" };

    /// <summary>Adds the shaded variants of <see cref="Keys"/>, read from <paramref name="source"/>
    /// for <paramref name="variant"/>, to <paramref name="target"/>. A key the theme lacks, or
    /// paints with a brush this can't shade, is left alone. One step for every key, sized on the
    /// page colour, so all surfaces keep the same hue.</summary>
    public static void AddTo(ResourceDictionary target, IResourceNode source, ThemeVariant? variant,
        Color accent, bool isLightTheme)
    {
        var page = source.TryGetResource("AppMainBackgroundColor", variant, out var pv) && pv is Color pc
            ? pc
            : isLightTheme ? Colors.White : Color.FromRgb(0x25, 0x25, 0x25);
        var t = StrengthFor(page, accent, isLightTheme);
        foreach (var key in Keys)
        {
            if (!source.TryGetResource(key, variant, out var value)) continue;
            if (Tint(value, accent, t) is { } tinted) target[key] = tinted;
        }
    }

    /// <summary>The step toward <paramref name="accent"/> for a page of colour <paramref name="page"/>:
    /// the theme's strength, less when that would cost more than <see cref="MaxContrastLoss"/>
    /// of the text's contrast (light text on a dark theme, dark text on a light one).</summary>
    internal static double StrengthFor(Color page, Color accent, bool isLightTheme)
    {
        var max = isLightTheme ? LightStrength : DarkStrength;
        var l0 = ThemeDerivation.RelativeLuminance(page) + 0.05;
        bool Ok(double t)
        {
            var l = ThemeDerivation.RelativeLuminance(ThemeDerivation.Mix(page, accent, t)) + 0.05;
            return isLightTheme ? l >= l0 * (1 - MaxContrastLoss) : l <= l0 / (1 - MaxContrastLoss);
        }
        if (Ok(max)) return max;
        double lo = 0, hi = max;
        for (var i = 0; i < 16; i++)
        {
            var mid = (lo + hi) / 2;
            if (Ok(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary><paramref name="c"/> moved <paramref name="t"/> of the way to the accent; keeps its alpha
    /// (Smoke's page surface is translucent over its gradient).</summary>
    internal static Color Tint(Color c, Color accent, double t)
    {
        var mixed = ThemeDerivation.Mix(c, accent, t);
        return Color.FromArgb(c.A, mixed.R, mixed.G, mixed.B);
    }

    internal static object? Tint(object? value, Color accent, double t)
    {
        switch (value)
        {
            case Color c:
                return Tint(c, accent, t);
            case ISolidColorBrush solid:
                return new SolidColorBrush(Tint(solid.Color, accent, t), solid.Opacity);
            case ILinearGradientBrush linear:
                var stops = new GradientStops();
                foreach (var stop in linear.GradientStops)
                    stops.Add(new GradientStop(Tint(stop.Color, accent, t), stop.Offset));
                return new LinearGradientBrush
                {
                    StartPoint = linear.StartPoint,
                    EndPoint = linear.EndPoint,
                    SpreadMethod = linear.SpreadMethod,
                    Opacity = linear.Opacity,
                    GradientStops = stops,
                };
            default:
                return null;
        }
    }
}
