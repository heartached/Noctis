using Avalonia.Media;

namespace Noctis.Services;

public static class ThemeDerivation
{
    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    internal static Color ParseHex(string hex, Color fallback)
    {
        try { return Color.Parse(hex.Trim()); }
        catch { return fallback; }
    }

    /// <summary>
    /// Returns a foreground color that meets the WCAG contrast target against <paramref name="bg"/>.
    /// Picks the white-or-black pole that already has more contrast, then darkens/lightens until
    /// the target is met. Always converges (black-on-white = 21:1 covers any target ≤ 21).
    /// </summary>
    public static Color PickReadableText(Color bg, double minRatio)
    {
        var startWhite = ContrastRatio(bg, Colors.White) >= ContrastRatio(bg, Colors.Black);
        var fg = startWhite ? Colors.White : Colors.Black;

        if (ContrastRatio(bg, fg) >= minRatio) return fg;

        // Move 16 steps toward the opposite pole; one of them must satisfy.
        for (var i = 1; i <= 16; i++)
        {
            var t = i / 16.0;
            var candidate = startWhite
                ? Mix(Colors.White, Colors.Black, t)
                : Mix(Colors.Black, Colors.White, t);
            if (ContrastRatio(bg, candidate) >= minRatio) return candidate;
        }
        return startWhite ? Colors.White : Colors.Black;
    }

    internal static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte r = (byte)(a.R + (b.R - a.R) * t);
        byte g = (byte)(a.G + (b.G - a.G) * t);
        byte bl = (byte)(a.B + (b.B - a.B) * t);
        return Color.FromRgb(r, g, bl);
    }

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>
    /// Expands a CustomThemeDefinition into the full ResourceDictionary the app expects.
    /// Caller merges this onto Application.Resources; App.SetAccent runs after to overlay
    /// the active accent palette (Dark1/2/3, Light1/2/3, etc.).
    /// </summary>
    public static IDictionary<string, object> Derive(Noctis.Models.CustomThemeDefinition def)
    {
        var isDark = !string.Equals(def.BaseMode, "Light", StringComparison.OrdinalIgnoreCase);
        var main = ParseHex(def.MainBackgroundHex, isDark ? Color.Parse("#121212") : Color.Parse("#F5F5F5"));
        var sidebar = ParseHex(def.SidebarBackgroundHex, isDark ? Color.Parse("#1C1C1C") : Color.Parse("#EAEAEA"));
        var accent = ParseHex(def.AccentHex, Color.Parse("#E74856"));

        // Foregrounds clamped against the main background.
        var primary    = PickReadableText(main, 4.5);
        var secondary  = PickReadableText(main, 3.0);
        var tertiary   = PickReadableText(main, 2.0);

        // Opaque island base = main bg; we encode the 0xF0 alpha later.
        var islandBase = main;
        var islandFg          = PickReadableText(islandBase, 4.5);
        var islandFgSecondary = PickReadableText(islandBase, 3.0);
        var islandFgTertiary  = PickReadableText(islandBase, 2.0);

        // Surface ramp — alpha overlays of the foreground polarity, matching Dark.axaml steps.
        var overlayColor = isDark ? Colors.White : Colors.Black;
        var stripe        = WithAlpha(overlayColor, 0x08);
        var hover         = WithAlpha(overlayColor, 0x14);
        var homePill      = WithAlpha(overlayColor, 0x14);
        var homePillHover = WithAlpha(overlayColor, 0x26);
        var sidebarHover  = WithAlpha(overlayColor, 0x14);

        // Sidebar selected = lift the sidebar background a notch toward foreground polarity.
        var sidebarSelected     = Mix(sidebar, overlayColor, 0.08);
        var sidebarSelectedHov  = Mix(sidebar, overlayColor, 0.14);

        var dict = new Dictionary<string, object>
        {
            // ── Sentinel read by App.SetTheme ──
            ["__BaseVariant"] = isDark ? "Dark" : "Light",

            // ── Accent (a stub; App.SetAccent overlays the real palette right after) ──
            ["SystemAccentColor"] = accent,
            ["AccentColorBrush"]  = new SolidColorBrush(accent),
            ["AccentForegroundBrush"] = new SolidColorBrush(Colors.White),
            ["SystemControlHighlightAccentBrush"]  = new SolidColorBrush(accent),
            ["SystemControlHighlightAccentBrush2"] = new SolidColorBrush(accent),
            ["ToggleSwitchFillOn"]            = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnPressed"]     = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnDragging"]    = new SolidColorBrush(accent),

            // ── App backgrounds ──
            ["AppSidebarBackground"]   = new SolidColorBrush(sidebar),
            ["AppMainBackground"]      = new SolidColorBrush(main),
            ["AppMainBackgroundColor"] = main,
            ["TrackListStripeBrush"]   = new SolidColorBrush(stripe),
            ["TrackListHoverBrush"]    = new SolidColorBrush(hover),

            // ── Primary/secondary/tertiary text (extra keys used by views; safe additions) ──
            ["PrimaryTextBrush"]   = new SolidColorBrush(primary),
            ["SecondaryTextBrush"] = new SolidColorBrush(secondary),
            ["TertiaryTextBrush"]  = new SolidColorBrush(tertiary),

            // ── Home cards ──
            ["HomeCardBackground"]      = new SolidColorBrush(homePill),
            ["HomeCardHoverBackground"] = new SolidColorBrush(homePillHover),

            // ── Sidebar highlight ──
            ["SidebarSelectedBrush"]      = new SolidColorBrush(sidebarSelected),
            ["SidebarSelectedHoverBrush"] = new SolidColorBrush(sidebarSelectedHov),
            ["SidebarHoverBrush"]         = new SolidColorBrush(sidebarHover),

            // ── Playback Island ──
            ["IslandBackground"]           = new SolidColorBrush(WithAlpha(islandBase, 0xF0)),
            ["IslandForeground"]           = new SolidColorBrush(islandFg),
            ["IslandForegroundSecondary"]  = new SolidColorBrush(islandFgSecondary),
            ["IslandForegroundTertiary"]   = new SolidColorBrush(islandFgTertiary),
            ["IslandIconFill"]             = new SolidColorBrush(islandFg),
            ["IslandSliderFilled"]         = new SolidColorBrush(islandFg),
            ["IslandSliderUnfilled"]       = new SolidColorBrush(Mix(islandBase, overlayColor, 0.22)),
            ["IslandAlbumArtPlaceholder"]  = new SolidColorBrush(WithAlpha(overlayColor, 0x33)),
            ["IslandExplicitBadge"]        = new SolidColorBrush(WithAlpha(overlayColor, 0x88)),
            ["IslandIconAccent"]           = new SolidColorBrush(accent),
        };

        // System chrome keys all resolve to the main background (matches the built-in pattern).
        string[] chromeColorKeys =
        {
            "SystemChromeMediumColor", "SystemChromeMediumLowColor", "SystemChromeLowColor",
            "SystemChromeHighColor", "SystemAltHighColor", "SystemAltMediumColor",
            "SystemAltMediumHighColor", "SystemAltMediumLowColor", "SystemAltLowColor",
            "SystemRegionColor", "RegionColor",
        };
        foreach (var k in chromeColorKeys) dict[k] = main;

        string[] chromeBrushKeys =
        {
            "SystemControlBackgroundAltHighBrush", "SystemControlBackgroundAltMediumBrush",
            "SystemControlBackgroundAltMediumHighBrush", "SystemControlBackgroundAltMediumLowBrush",
            "SystemControlBackgroundBaseLowBrush", "SystemControlBackgroundBaseMediumBrush",
            "SystemControlBackgroundChromeMediumBrush", "SystemControlBackgroundChromeMediumLowBrush",
            "SystemControlBackgroundChromeWhiteBrush",
            "SystemControlPageBackgroundAltHighBrush", "SystemControlPageBackgroundBaseLowBrush",
            "SystemControlPageBackgroundBaseMediumBrush", "SystemControlPageBackgroundChromeLowBrush",
            "SystemControlPageBackgroundChromeMediumLowBrush",
            "SystemChromeMediumBrush", "SystemChromeMediumLowBrush", "SystemChromeLowBrush",
            "SystemChromeHighBrush", "SystemAltHighBrush", "SystemAltMediumBrush",
            "SystemAltMediumHighBrush", "SystemAltMediumLowBrush", "SystemAltLowBrush",
        };
        // BaseLow gets the sidebar tint (matches Midnight pattern for popups/menus that
        // need to differ slightly from the main page).
        foreach (var k in chromeBrushKeys)
            dict[k] = new SolidColorBrush(k == "SystemControlBackgroundBaseLowBrush" ? sidebar : main);

        return dict;
    }
}
