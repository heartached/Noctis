using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Noctis.Plugins.Chords.Views;

/// <summary>Chords' colours: Noctis's page background and accent when the plugin can see them, else
/// a dark palette with the plugin's violet. Brushes are immutable and made once per theme.</summary>
public sealed class ChordsTheme
{
    public static readonly Color Violet = Color.FromRgb(0x8B, 0x6C, 0xF6);
    public static readonly ChordsTheme Dark = new(true, Color.FromRgb(0x1C, 0x1C, 0x1F), Violet);

    public ChordsTheme(bool dark, Color page, Color accent)
    {
        IsDark = dark;
        Accent = accent;
        Page = Solid(page);
        Text = Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Muted = Solid(dark ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Faint = Solid(dark ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0x00, 0x00, 0x00));
        Chip = Solid(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        Block = Solid(dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        BlockNow = Solid(Color.FromArgb(dark ? (byte)0x55 : (byte)0x3A, accent.R, accent.G, accent.B));
        AccentBrush = Solid(accent);
        AccentText = Solid(dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.4));
        OnAccent = Solid((0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255 > 0.62 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White);
        Playhead = new ImmutablePen(Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E)), 2);
    }

    public bool IsDark { get; }
    public Color Accent { get; }
    public IBrush Page { get; }
    public IBrush Text { get; }
    public IBrush Muted { get; }
    public IBrush Faint { get; }
    public IBrush Chip { get; }
    public IBrush Block { get; }
    public IBrush BlockNow { get; }
    public IBrush AccentBrush { get; }
    public IBrush AccentText { get; }
    public IBrush OnAccent { get; }
    public IPen Playhead { get; }

    public static ChordsTheme From(StyledElement element)
    {
        var variant = element is IThemeVariantHost host ? host.ActualThemeVariant : ThemeVariant.Dark;
        Color? page = element.TryFindResource("AppMainBackgroundColor", variant, out var p) && p is Color pc ? pc : null;
        var accent = element.TryFindResource("AccentColorBrush", variant, out var a) && a is ISolidColorBrush ab ? ab.Color : Violet;
        if (page is not { } bg) return variant == ThemeVariant.Light ? new ChordsTheme(false, Color.FromRgb(0xF2, 0xF2, 0xF5), accent) : new ChordsTheme(true, Color.FromRgb(0x1C, 0x1C, 0x1F), accent);
        return new ChordsTheme((0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255 < 0.5, bg, accent);
    }

    private static Color Mix(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static ImmutableSolidColorBrush Solid(Color c) => new(c);
}
