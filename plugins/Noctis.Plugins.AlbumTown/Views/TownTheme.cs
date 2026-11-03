using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// Album Town's colours: a flat, quiet dark palette (the default, in step with Noctis) and a
/// light one. <see cref="From"/> follows the app: it reads Noctis's own page background and
/// accent when the plugin can see them, and falls back to the dark palette when it cannot.
/// Every brush and pen is immutable and made once, so drawing a frame allocates nothing.
/// </summary>
public sealed class TownTheme
{
    public static readonly Color DefaultAccent = Color.FromRgb(0xFF, 0x5C, 0x8A);
    public static readonly TownTheme Dark = new(dark: true, Color.FromRgb(0x1C, 0x1C, 0x1F), DefaultAccent);
    public static readonly TownTheme Light = new(dark: false, Color.FromRgb(0xF2, 0xF2, 0xF5), DefaultAccent);

    public TownTheme(bool dark, Color page, Color accent)
    {
        IsDark = dark;
        Page = page;
        Accent = accent;
        Floor = dark ? Mix(page, Colors.White, 0.025) : Mix(page, Colors.Black, 0.02);
        FloorLine = dark ? Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        FloorGlow = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF);
        CoverEdge = dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        TileShadow = dark ? Color.FromArgb(0x8C, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0x1A, 0x1A, 0x24);

        FloorBrush = Solid(Floor);
        GroundShadow = Solid(dark ? Color.FromArgb(0x50, 0x00, 0x00, 0x00) : Color.FromArgb(0x22, 0x10, 0x10, 0x20));
        Limb = Solid(Color.FromRgb(0xF5, 0xF5, 0xF7));
        LimbPen = new ImmutablePen((IImmutableBrush)Solid(dark ? Color.FromArgb(0x66, 0x00, 0x00, 0x00) : Color.FromArgb(0x38, 0x00, 0x00, 0x00)), 1);
        EyeWhite = Solid(Colors.White);
        EyePen = new ImmutablePen((IImmutableBrush)Solid(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), 1);
        Pupil = Solid(Color.FromRgb(0x1C, 0x1C, 0x1E));
        BlinkPen = new ImmutablePen((IImmutableBrush)Pupil, 1.6, lineCap: PenLineCap.Round);
        Bubble = Solid(dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
        BubblePen = new ImmutablePen((IImmutableBrush)Solid(dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00)), 1);
        BubbleText = Solid(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Tag = Solid(dark ? Color.FromArgb(0xCC, 0x0E, 0x0E, 0x10) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        TagText = Solid(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E));
        AccentBrush = Solid(accent);
        OnAccent = Solid(Luminance(accent) > 0.6 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White);
        AccentRing = new ImmutablePen((IImmutableBrush)AccentBrush, 3, lineJoin: PenLineJoin.Round);
        Placeholder = Solid(dark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xD1, 0xD1, 0xD6));
        PlaceholderText = Solid(dark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0x00, 0x00, 0x00));

        Chrome = Solid(dark ? Mix(page, Colors.Black, 0.25) : Mix(page, Colors.White, 0.6));
        Card = Solid(dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
        Text = Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Muted = Solid(dark ? Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Faint = Solid(dark ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0x00, 0x00, 0x00));
        Chip = Solid(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        TabOn = Solid(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
        ShelfEdge = new ImmutablePen((IImmutableBrush)Solid(CoverEdge), 1);
        HandleFill = Solid(dark ? Color.FromArgb(0xE6, 0xF2, 0xF2, 0xF4) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        HandlePen = new ImmutablePen((IImmutableBrush)Solid(dark ? Color.FromArgb(0x60, 0x00, 0x00, 0x00) : Color.FromArgb(0x50, 0x00, 0x00, 0x00)), 1);
        GhostFill = Solid(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        GhostPen = new ImmutablePen((IImmutableBrush)AccentBrush, 2, new ImmutableDashStyle(new double[] { 3, 2 }, 0));
        var blocked = Color.FromRgb(0xE7, 0x48, 0x56);
        GhostBlocked = Solid(Color.FromArgb(0x30, blocked.R, blocked.G, blocked.B));
        GhostBlockedPen = new ImmutablePen((IImmutableBrush)Solid(blocked), 2, new ImmutableDashStyle(new double[] { 3, 2 }, 0));
    }

    public bool IsDark { get; }
    public Color Page { get; }
    public Color Accent { get; }
    public Color Floor { get; }
    public Color FloorLine { get; }
    public Color FloorGlow { get; }
    public Color CoverEdge { get; }
    public Color TileShadow { get; }

    public IBrush FloorBrush { get; }
    public IBrush GroundShadow { get; }
    public IBrush Limb { get; }
    public IPen LimbPen { get; }
    public IBrush EyeWhite { get; }
    public IPen EyePen { get; }
    public IBrush Pupil { get; }
    public IPen BlinkPen { get; }
    public IBrush Bubble { get; }
    public IPen BubblePen { get; }
    public IBrush BubbleText { get; }
    public IBrush Tag { get; }
    public IBrush TagText { get; }
    public IBrush AccentBrush { get; }
    public IBrush OnAccent { get; }
    public IPen AccentRing { get; }
    public IBrush Placeholder { get; }
    public IBrush PlaceholderText { get; }

    public IBrush Chrome { get; }
    public IBrush Card { get; }
    public IBrush Text { get; }
    public IBrush Muted { get; }
    public IBrush Faint { get; }
    public IBrush Chip { get; }
    public IBrush TabOn { get; }
    public IPen ShelfEdge { get; }
    /// <summary>A room's resize handles (the hot one is the accent).</summary>
    public IBrush HandleFill { get; }
    public IPen HandlePen { get; }
    /// <summary>Where a dragged room would land (fits / blocked).</summary>
    public IBrush GhostFill { get; }
    public IPen GhostPen { get; }
    public IBrush GhostBlocked { get; }
    public IPen GhostBlockedPen { get; }

    /// <summary>
    /// The palette for the app around <paramref name="element"/>: Noctis's page background
    /// (AppMainBackgroundColor) decides dark or light, its AccentColorBrush tints the dancer.
    /// Without them (another host, a test) the dark palette.
    /// </summary>
    public static TownTheme From(StyledElement element)
    {
        var variant = element is IThemeVariantHost host ? host.ActualThemeVariant : ThemeVariant.Dark;
        Color? page = element.TryFindResource("AppMainBackgroundColor", variant, out var p) && p is Color pc ? pc : null;
        var accent = element.TryFindResource("AccentColorBrush", variant, out var a) && a is ISolidColorBrush ab ? ab.Color : DefaultAccent;
        if (page is not { } bg) return Dark;
        var dark = Luminance(bg) < 0.5;
        return new TownTheme(dark, bg, accent);
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static Color Mix(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static ImmutableSolidColorBrush Solid(Color c) => new(c);
}
