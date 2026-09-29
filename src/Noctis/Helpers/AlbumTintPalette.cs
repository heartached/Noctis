using Avalonia.Media;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// Colour maths for the tinted album page (Settings › Artwork › Album Page Colour): how much
/// of the cover colour the page takes on (the Tint strength slider), and which colours keep
/// the page's pill buttons and header icons apart from that page colour. With "Accent follows
/// album art" the accent comes from the same cover, so the pills used to melt into the page
/// (Discord ask 09-24).
/// </summary>
public static class AlbumTintPalette
{
    /// <summary>WCAG 2.x non-text contrast (1.4.11): a filled pill or an icon against the page
    /// right around it.</summary>
    public const double MinFillContrast = 3.0;

    /// <summary>Label-on-fill floor for the app's own accent label (bold pill text; the app's
    /// white-on-accent pills sit around here). Below it the label becomes black or white,
    /// whichever reads better (always 4.5:1 or more).</summary>
    public const double MinLabelContrast = 3.0;

    /// <summary>The app's heart red (HeartIcon.OnBrush default).</summary>
    public static readonly Color HeartRed = Color.FromRgb(0xE7, 0x48, 0x56);

    /// <summary>The page colour for a cover edge colour at <paramref name="strengthPercent"/>
    /// (0–100): the cover colour laid over the theme's page colour at that opacity. 100 returns
    /// <paramref name="edge"/> untouched, the page as it looked before the slider existed.</summary>
    public static Color PageColor(Color edge, Color themePage, int strengthPercent)
    {
        var t = Math.Clamp(strengthPercent, 0, 100) / 100.0;
        if (t >= 1) return edge;
        return Color.FromRgb(Lerp(themePage.R, edge.R, t), Lerp(themePage.G, edge.G, t), Lerp(themePage.B, edge.B, t));
    }

    private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);

    /// <summary>The album page's dark text colour (light pages).</summary>
    public static readonly Color DarkText = Color.FromRgb(0x11, 0x11, 0x11);

    /// <summary>
    /// Whether the page text goes dark on <paramref name="page"/>. At full strength this is the
    /// page's long-standing luminance flip (<paramref name="lightThreshold"/>), so the default
    /// look is unchanged. A blended page also flips once white text would read under 3:1 and
    /// the dark text reads better: on the Light theme a dark cover blends into mid greys.
    /// </summary>
    public static bool UsesDarkText(Color page, int strengthPercent, double lightThreshold)
    {
        if (DominantColorExtractor.GetRelativeLuminance(page) > lightThreshold) return true;
        if (strengthPercent >= 100) return false;
        var white = Contrast(Colors.White, page);
        return white < MinFillContrast && Contrast(DarkText, page) > white;
    }

    /// <summary>WCAG contrast ratio of two opaque colours (1 = identical … 21 = black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = DominantColorExtractor.GetRelativeLuminance(a);
        var lb = DominantColorExtractor.GetRelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>A translucent colour as it shows over <paramref name="background"/>.</summary>
    public static Color Flatten(Color color, Color background)
    {
        if (color.A == 255) return color;
        var t = color.A / 255.0;
        return Color.FromRgb(Lerp(background.R, color.R, t), Lerp(background.G, color.G, t), Lerp(background.B, color.B, t));
    }

    /// <summary>
    /// <paramref name="color"/> itself when it already stands apart from
    /// <paramref name="background"/> by <paramref name="minRatio"/>. Otherwise the same hue
    /// moved lighter or darker in OKLab (whichever needs the smaller step), easing its chroma
    /// on the way so the far end is plain white or black; one of the two always clears 3:1.
    /// </summary>
    public static Color EnsureContrast(Color color, Color background, double minRatio = MinFillContrast)
    {
        color = Flatten(color, background);
        if (Contrast(color, background) >= minRatio) return color;

        var (l, a, b) = DominantColorExtractor.ToOkLab(color.R, color.G, color.B);
        var lighter = Search(l, a, b, background, minRatio, towardWhite: true);
        var darker = Search(l, a, b, background, minRatio, towardWhite: false);
        if (lighter is { } up && darker is { } down)
        {
            // Same distance: go away from the page (lighter on a dark page, darker on a light one).
            if (Math.Abs(up.Step - down.Step) < 1e-9)
                return DominantColorExtractor.GetRelativeLuminance(background) < 0.18 ? up.Color : down.Color;
            return up.Step < down.Step ? up.Color : down.Color;
        }
        if (lighter is { } onlyUp) return onlyUp.Color;
        if (darker is { } onlyDown) return onlyDown.Color;
        return Contrast(Colors.White, background) >= Contrast(Colors.Black, background) ? Colors.White : Colors.Black;
    }

    private static (Color Color, double Step)? Search(double l, double a, double b, Color background, double minRatio, bool towardWhite)
    {
        var end = towardWhite ? 1.0 : 0.0;
        var span = Math.Abs(end - l);
        if (span < 1e-6) return null;
        const int steps = 100;
        for (var i = 1; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var nl = l + (end - l) * progress;
            var keep = 1 - progress; // chroma eases out: the far end is exactly white / black
            var c = i == steps
                ? (towardWhite ? Colors.White : Colors.Black)
                : DominantColorExtractor.FromOkLab(nl, a * keep, b * keep);
            if (Contrast(c, background) >= minRatio) return (c, span * progress);
        }
        return null;
    }

    /// <summary>White or black, whichever reads better on <paramref name="fill"/> (the worse
    /// case, a mid grey, still gets about 4.6:1).</summary>
    public static Color LabelOn(Color fill)
        => Contrast(Colors.Black, fill) > Contrast(Colors.White, fill) ? Colors.Black : Colors.White;

    /// <summary>Colours for the album header's controls on a tinted page.</summary>
    /// <param name="Fill">Play / Shuffle / Add to Queue pill fill.</param>
    /// <param name="Label">Text and icons on that fill.</param>
    /// <param name="Border">Pill rim (the accent's own rim while the accent is kept).</param>
    /// <param name="Icon">The "…" glyph beside the heart.</param>
    /// <param name="Heart">The favourite heart while on.</param>
    public readonly record struct HeaderColours(Color Fill, Color Label, Color Border, Color Icon, Color Heart);

    /// <summary>
    /// Header colours for a tinted page. The accent fill is kept whenever it already clears
    /// 3:1 against the page (the page looks as it always did); otherwise the pills take the
    /// accent's hue moved lighter or darker until they do. The label stays the app's accent
    /// label while it reads on the fill, else black or white. The "…" glyph and the heart get
    /// the same 3:1 treatment as the pills.
    /// </summary>
    public static HeaderColours ForPage(Color page, Color accentFill, Color accentLabel, Color accentBorder, Color accentText)
    {
        var flatAccent = Flatten(accentFill, page);
        var fill = EnsureContrast(flatAccent, page);
        var kept = fill == flatAccent;
        var label = Contrast(Flatten(accentLabel, fill), fill) >= MinLabelContrast
            ? accentLabel
            : LabelOn(fill);
        return new HeaderColours(
            fill,
            label,
            kept ? accentBorder : Colors.Transparent,
            EnsureContrast(accentText, page),
            EnsureContrast(HeartRed, page));
    }
}
