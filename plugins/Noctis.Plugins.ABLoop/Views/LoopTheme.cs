using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Noctis.Plugins.ABLoop.Views;

/// <summary>A-B Loop's colours: Noctis's page background and accent when the plugin can see them,
/// else a dark palette with the plugin's teal. Brushes are immutable and made once.</summary>
public sealed class LoopTheme
{
    public static readonly Color Teal = Color.FromRgb(0x2E, 0xC4, 0xB6);
    public static readonly LoopTheme Dark = new(true, Color.FromRgb(0x1C, 0x1C, 0x1F), Teal);
    public static readonly LoopTheme Light = new(false, Color.FromRgb(0xF2, 0xF2, 0xF5), Teal);

    public LoopTheme(bool dark, Color page, Color accent)
    {
        IsDark = dark;
        Accent = accent;
        Page = Solid(page);
        Text = Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Muted = Solid(dark ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Faint = Solid(dark ? Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0x00, 0x00, 0x00));
        Chip = Solid(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        Row = Solid(dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        ChipOn = Solid(Color.FromArgb(dark ? (byte)0x44 : (byte)0x30, accent.R, accent.G, accent.B));
        ChipOnText = Solid(dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.4));
        AccentBrush = Solid(accent);
        OnAccent = Solid((0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255 > 0.62 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White);
        Track = Solid(dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
        Played = Solid(dark ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x48, 0x00, 0x00, 0x00));
        Shade = Solid(Color.FromArgb(dark ? (byte)0x50 : (byte)0x40, accent.R, accent.G, accent.B));
        Playhead = Solid(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        MarkerPen = new ImmutablePen(Solid(dark ? Color.FromArgb(0x55, 0x00, 0x00, 0x00) : Color.FromArgb(0x30, 0x00, 0x00, 0x00)), 1);
    }

    public bool IsDark { get; }
    public Color Accent { get; }
    public IBrush Page { get; }
    public IBrush Text { get; }
    public IBrush Muted { get; }
    public IBrush Faint { get; }
    public IBrush Chip { get; }
    public IBrush Row { get; }
    public IBrush ChipOn { get; }
    public IBrush ChipOnText { get; }
    public IBrush AccentBrush { get; }
    public IBrush OnAccent { get; }
    public IBrush Track { get; }
    public IBrush Played { get; }
    public IBrush Shade { get; }
    public IBrush Playhead { get; }
    public IPen MarkerPen { get; }

    public static LoopTheme From(StyledElement element)
    {
        var variant = element is IThemeVariantHost host ? host.ActualThemeVariant : ThemeVariant.Dark;
        Color? page = element.TryFindResource("AppMainBackgroundColor", variant, out var p) && p is Color pc ? pc : null;
        var accent = element.TryFindResource("AccentColorBrush", variant, out var a) && a is ISolidColorBrush ab ? ab.Color : Teal;
        if (page is not { } bg) return variant == ThemeVariant.Light ? new LoopTheme(false, Color.FromRgb(0xF2, 0xF2, 0xF5), accent) : new LoopTheme(true, Color.FromRgb(0x1C, 0x1C, 0x1F), accent);
        return new LoopTheme((0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255 < 0.5, bg, accent);
    }

    private static Color Mix(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t),
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static ImmutableSolidColorBrush Solid(Color c) => new(c);
}
