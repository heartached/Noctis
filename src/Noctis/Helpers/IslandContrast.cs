using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Keeps the player island's text and icons readable on whatever surface it ends up with.
/// The island's fill is translucent (Settings › Glass Opacity, 40% by default, and the same
/// tint over the frost with Liquid Glass on), so over a light album page colour the pill
/// itself turns light grey while the theme's island colours stay white (live, 11-06: the
/// "Thriller" title at 233,240,240 on ~#C8C6C2). Like the album page's own text (#180,
/// <see cref="Noctis.ViewModels.AlbumDetailViewModel.PageForegroundBrush"/>), the island
/// then flips to a dark family on light surfaces and back to light on dark ones. A surface
/// the theme's colours already read on gets no overrides at all.
/// </summary>
public static class IslandContrast
{
    /// <summary>WCAG AA for the now-playing title and the other island text.</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>The island's dark text on light surfaces: the album page's dark text.</summary>
    public static readonly Color DarkForeground = AlbumTintPalette.DarkText;

    /// <summary>The resource keys an override sets (besides a slider fill that melts in).</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "IslandForeground", "IslandForegroundSecondary", "IslandForegroundTertiary", "IslandIconFill",
        "IslandSliderUnfilled", "IslandTrackBoxSliderUnfilled", "IslandAlbumArtPlaceholder",
    ];

    /// <summary>Dark island colours for a light surface (the Dark theme's values, inverted).</summary>
    private static readonly Color[] DarkFamily =
    [
        DarkForeground, Color.FromRgb(0x3A, 0x3A, 0x3A), Color.FromRgb(0x5A, 0x5A, 0x5A), DarkForeground,
        Color.FromArgb(0x40, 0x00, 0x00, 0x00), Color.FromArgb(0x2E, 0x00, 0x00, 0x00), Color.FromArgb(0x26, 0x00, 0x00, 0x00),
    ];

    /// <summary>Light island colours for a dark surface (the Dark theme's own values).</summary>
    private static readonly Color[] LightFamily =
    [
        Colors.White, Color.FromRgb(0xC8, 0xC8, 0xC8), Color.FromRgb(0x90, 0x90, 0x90), Colors.White,
        Color.FromRgb(0x3A, 0x3A, 0x3A), Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
    ];

    /// <summary>A fill of <paramref name="fill"/> at <paramref name="opacity"/> over <paramref name="backdrop"/>.</summary>
    public static Color Surface(Color backdrop, Color fill, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return AlbumTintPalette.Flatten(Color.FromArgb(alpha, fill.R, fill.G, fill.B), Color.FromRgb(backdrop.R, backdrop.G, backdrop.B));
    }

    /// <summary>
    /// The colour <paramref name="panel"/> shows over a plain <paramref name="backdrop"/>: its
    /// tint over the frost with Liquid Glass on (a blur of a plain colour is that colour), its
    /// Background as drawn with it off.
    /// </summary>
    public static Color SurfaceOf(GlassPanel panel, Color backdrop)
    {
        if (panel.EffectiveGlassActive)
        {
            var (tint, opacity) = panel.ResolveTint();
            return Surface(backdrop, tint, opacity);
        }
        if (panel.Background is ISolidColorBrush solid)
            return Surface(backdrop, solid.Color, solid.Opacity * solid.Color.A / 255.0);
        return Color.FromRgb(backdrop.R, backdrop.G, backdrop.B);
    }

    /// <summary>
    /// The island resources to override on <paramref name="surface"/>, or null when the
    /// theme's own colours (looked up through <paramref name="theme"/>) already read there.
    /// The family follows whichever of dark or white text reads better on the surface; the
    /// slider fill (the accent, AccentPalette) is kept unless it drops under 3:1.
    /// </summary>
    public static Dictionary<string, IBrush>? OverridesFor(Color surface, Func<string, IBrush?> theme)
    {
        var themeFg = theme("IslandForeground") is ISolidColorBrush fg ? fg.Color : Colors.White;
        var themeContrast = AlbumTintPalette.Contrast(AlbumTintPalette.Flatten(themeFg, surface), surface);
        if (themeContrast >= MinTextContrast) return null;

        var dark = UsesDarkForeground(surface);
        var family = dark ? DarkFamily : LightFamily;
        if (AlbumTintPalette.Contrast(family[0], surface) <= themeContrast) return null;

        var overrides = new Dictionary<string, IBrush>(Keys.Count + 1);
        for (var i = 0; i < Keys.Count; i++)
            overrides[Keys[i]] = new SolidColorBrush(family[i]);
        if (theme("IslandSliderFilled") is ISolidColorBrush slider)
        {
            var shown = AlbumTintPalette.Flatten(slider.Color, surface);
            var fixedFill = AlbumTintPalette.EnsureContrast(slider.Color, surface);
            if (fixedFill != shown) overrides["IslandSliderFilled"] = new SolidColorBrush(fixedFill);
        }
        return overrides;
    }

    /// <summary>Whether dark text reads better than white on <paramref name="surface"/>.</summary>
    public static bool UsesDarkForeground(Color surface) =>
        AlbumTintPalette.Contrast(DarkForeground, surface) > AlbumTintPalette.Contrast(Colors.White, surface);

    /// <summary>Writes <paramref name="overrides"/> into <paramref name="resources"/>, or takes
    /// every island key back out of it (null), leaving the theme's values to show through.</summary>
    public static void Apply(Avalonia.Controls.IResourceDictionary resources, IReadOnlyDictionary<string, IBrush>? overrides)
    {
        foreach (var key in Keys)
            Set(resources, key, overrides != null && overrides.TryGetValue(key, out var b) ? b : null);
        Set(resources, "IslandSliderFilled",
            overrides != null && overrides.TryGetValue("IslandSliderFilled", out var s) ? s : null);
    }

    private static void Set(Avalonia.Controls.IResourceDictionary resources, string key, IBrush? brush)
    {
        if (brush == null)
        {
            resources.Remove(key);
            return;
        }
        if (resources.TryGetValue(key, out var existing) && existing is ISolidColorBrush old
            && brush is ISolidColorBrush next && old.Color == next.Color && old.Opacity == next.Opacity)
            return;
        resources[key] = brush;
    }
}
