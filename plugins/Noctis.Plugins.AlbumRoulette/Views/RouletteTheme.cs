using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Noctis.Plugins.AlbumRoulette.Views;

/// <summary>
/// Album Roulette's colours. <see cref="From"/> follows the app (Noctis's page background decides
/// dark or light, its accent tints the glow and the Spin button); without them, a dark palette
/// with the plugin's coral. Every brush and pen is immutable and made once, so a reel frame
/// allocates nothing.
/// </summary>
public sealed class RouletteTheme
{
    public static readonly Color Coral = Color.FromRgb(0xFF, 0x6B, 0x5A);

    // Before Dark and Light: static fields initialize in order, and their constructors read it.
    private static readonly Color[] PlaceholderHues =
    {
        Color.FromRgb(0xF2, 0x8B, 0x82), Color.FromRgb(0xF6, 0xC1, 0x77), Color.FromRgb(0x9A, 0xD1, 0xA4), Color.FromRgb(0x7F, 0xC8, 0xD9),
        Color.FromRgb(0x9C, 0xA8, 0xF0), Color.FromRgb(0xC9, 0x9B, 0xE6), Color.FromRgb(0xE8, 0x9B, 0xC4), Color.FromRgb(0xB8, 0xC4, 0x8A),
    };

    public static readonly RouletteTheme Dark = new(dark: true, Color.FromRgb(0x1C, 0x1C, 0x1F), Coral);
    public static readonly RouletteTheme Light = new(dark: false, Color.FromRgb(0xF2, 0xF2, 0xF5), Coral);

    public RouletteTheme(bool dark, Color page, Color accent)
    {
        IsDark = dark;
        PageColor = page;
        Accent = accent;
        Page = Solid(page);
        Text = Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Muted = Solid(dark ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Faint = Solid(dark ? Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0x00, 0x00, 0x00));
        Chip = Solid(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        ChipOn = Solid(Color.FromArgb(dark ? (byte)0x40 : (byte)0x2C, accent.R, accent.G, accent.B));
        ChipOnText = Solid(dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.35));
        AccentBrush = Solid(accent);
        OnAccent = Solid(Luminance(accent) > 0.62 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White);
        CoverEdge = new ImmutablePen(Solid(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00)), 1);
        var glowA = dark ? (byte)0x8C : (byte)0xA0;
        // The glow at 17 strengths and the veil at 33, made once: a frame picks one instead of
        // pushing an opacity (which costs the drawing context an allocation each time).
        _glows = new IImmutableBrush[GlowSteps + 1];
        for (var k = 0; k <= GlowSteps; k++)
            _glows[k] = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                Opacity = k / (double)GlowSteps,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(glowA, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb((byte)(glowA * 0.45), accent.R, accent.G, accent.B), 0.45),
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
                },
            }.ToImmutable();
        _veils = new IImmutableBrush[VeilSteps + 1];
        for (var k = 0; k <= VeilSteps; k++)
            _veils[k] = Solid(Color.FromArgb((byte)Math.Round(255.0 * k / VeilSteps), page.R, page.G, page.B));
        Placeholders = PlaceholderHues.Select(c => (IImmutableBrush)Solid(dark ? Mix(c, Color.FromRgb(0x20, 0x20, 0x24), 0.35) : c)).ToArray();
        PlaceholderGlyph = Solid(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        Shadow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 28, Color = Color.FromArgb(dark ? (byte)0x90 : (byte)0x40, 0, 0, 0) });
    }

    public bool IsDark { get; }
    public Color PageColor { get; }
    public Color Accent { get; }
    public IBrush Page { get; }
    public IBrush Text { get; }
    public IBrush Muted { get; }
    public IBrush Faint { get; }
    public IBrush Chip { get; }
    public IBrush ChipOn { get; }
    public IBrush ChipOnText { get; }
    public IBrush AccentBrush { get; }
    public IBrush OnAccent { get; }
    public IPen CoverEdge { get; }
    private const int GlowSteps = 16;
    private const int VeilSteps = 32;
    private readonly IImmutableBrush[] _glows;
    private readonly IImmutableBrush[] _veils;

    /// <summary>The soft accent glow behind the cover at <paramref name="strength"/> (0..1).</summary>
    public IImmutableBrush GlowAt(double strength) => _glows[(int)Math.Round(Math.Clamp(strength, 0, 1) * GlowSteps)];

    /// <summary>The page colour at <paramref name="amount"/> (0..1): drawn over a cover, it fades it into the page.</summary>
    public IImmutableBrush Veil(double amount) => _veils[(int)Math.Round(Math.Clamp(amount, 0, 1) * VeilSteps)];
    /// <summary>Tiles standing in for a cover not decoded yet, or an album without one.</summary>
    public IReadOnlyList<IImmutableBrush> Placeholders { get; }
    public IBrush PlaceholderGlyph { get; }
    public BoxShadows Shadow { get; }

    /// <summary>A placeholder colour picked from <paramref name="seed"/> (stable across runs).</summary>
    public IImmutableBrush PlaceholderFor(string seed)
    {
        uint h = 2166136261;
        foreach (var ch in seed) h = (h ^ ch) * 16777619;
        h ^= h >> 13;
        return Placeholders[(int)(h % (uint)Placeholders.Count)];
    }

    /// <summary>The palette of the app around <paramref name="element"/>.</summary>
    public static RouletteTheme From(StyledElement element)
    {
        var variant = element is IThemeVariantHost host ? host.ActualThemeVariant : ThemeVariant.Dark;
        Color? page = element.TryFindResource("AppMainBackgroundColor", variant, out var p) && p is Color pc ? pc : null;
        var accent = element.TryFindResource("AccentColorBrush", variant, out var a) && a is ISolidColorBrush ab ? ab.Color : Coral;
        if (page is not { } bg) return variant == ThemeVariant.Light ? new RouletteTheme(false, Light.PageColor, accent) : new RouletteTheme(true, Dark.PageColor, accent);
        return new RouletteTheme(Luminance(bg) < 0.5, bg, accent);
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
