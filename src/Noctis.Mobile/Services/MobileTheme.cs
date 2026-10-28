using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Noctis.Helpers;

namespace Noctis.Mobile.Services;

/// <summary>
/// The phone's theme: an appearance (System / Dark / Light) picks between the base Light
/// dictionary — the desktop's "Light" theme — and one of the dark overlays, merged on top of
/// the base styles as the desktop App.SetThemeCore does; the accent overlay goes last.
/// </summary>
public sealed class MobileTheme
{
    public static IReadOnlyList<string> Appearances { get; } = new[] { "System", "Dark", "Light" };
    public static IReadOnlyList<string> DarkThemes { get; } = new[] { "Ink", "Smoke", "Dark", "Midnight" };
    public const string DefaultAccent = "#E74856";

    private ResourceInclude? _overlay;
    private ResourceDictionary? _lightText;
    private ResourceDictionary? _accent;
    private ResourceDictionary? _palette;

    /// <summary>
    /// The phone's own surface palette (iOS-style pages, grouped pages, cards, hairlines,
    /// floating chrome), keyed per variant so the always-dark scopes (Now Playing, Lyrics) pick
    /// the Dark set. Ink and Dark take Apple's pure black; Smoke and Midnight keep their own
    /// slate and blue-black, with surfaces stepped up from them the same way.
    /// The chrome brush is the Liquid Glass tint (translucent: what passes beneath shows
    /// through it), its outline the hairline rim every floating pill carries, and the selected
    /// brush the tab bar's inner capsule: a grey on light glass, darker than the pill on dark.
    /// Light's secondary text is Apple's secondaryLabel (#3C3C43 at 60 %), translucent, so it
    /// keeps its contrast on the grey fills it also sits on (the search field's magnifier read
    /// 2.97:1 as an opaque #8A8A8E); on a white page it is that same #8A8A8E. The explicit badge
    /// is the desktop's half-grey on dark, and Apple's secondary grey on Light, where the half-grey
    /// over white left its white "E" at 1.8:1.
    /// </summary>
    public static ResourceDictionary BuildPalette(string? darkTheme = null)
    {
        static SolidColorBrush B(string hex) => new(Color.Parse(hex));
        static ResourceDictionary Dark(string page, string grouped, string card, string separator, string fill,
            string chrome, string secondary, string chevron) => new()
        {
            ["PhoneBackgroundBrush"] = B(page),
            ["PhoneGroupedBackgroundBrush"] = B(grouped),
            ["PhoneSheetBackgroundBrush"] = B(grouped),
            ["PhoneCardBrush"] = B(card),
            ["PhoneSeparatorBrush"] = B(separator),
            ["PhoneFillBrush"] = B(fill),
            ["PhoneChromeBrush"] = B(chrome),
            ["PhoneChromeOutlineBrush"] = B("#30FFFFFF"),
            ["PhoneChromeSelectedBrush"] = B("#99000000"),
            ["PhoneSecondaryTextBrush"] = B(secondary),
            ["PhoneChevronBrush"] = B(chevron),
            ["PhoneExplicitBadgeBrush"] = B("#88888888"),
        };
        var palette = new ResourceDictionary();
        palette.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["PhoneBackgroundBrush"] = B("#FFFFFF"),
            ["PhoneGroupedBackgroundBrush"] = B("#F2F2F7"),
            ["PhoneSheetBackgroundBrush"] = B("#F2F2F7"),
            ["PhoneCardBrush"] = B("#FFFFFF"),
            ["PhoneSeparatorBrush"] = B("#D8D8DC"),
            ["PhoneFillBrush"] = B("#1F767680"),
            ["PhoneChromeBrush"] = B("#99FAFAFA"),
            ["PhoneChromeOutlineBrush"] = B("#21000000"),
            ["PhoneChromeSelectedBrush"] = B("#12000000"),
            ["PhoneSecondaryTextBrush"] = B("#993C3C43"),
            ["PhoneChevronBrush"] = B("#C4C4C7"),
            ["PhoneExplicitBadgeBrush"] = B("#8A8A8E"),
        };
        palette.ThemeDictionaries[ThemeVariant.Dark] = darkTheme switch
        {
            "Smoke" => Dark("#22242C", "#2A2D38", "#343844", "#3E4250", "#2A2D38", "#B3343844", "#9A9EAB", "#5E6270"),
            "Midnight" => Dark("#0E1322", "#161D31", "#1E2740", "#2A3552", "#1A2238", "#B31E2740", "#8A93AD", "#4A5372"),
            _ => Dark("#000000", "#1C1C1E", "#2C2C2E", "#38383A", "#1C1C1E", "#B32C2C2F", "#8D8D93", "#5A5A5E"),
        };
        return palette;
    }

    /// <summary>The theme last applied ("Light" or a dark theme), or null.</summary>
    public string? ThemeName { get; private set; }

    /// <summary>The theme to run: Light, or the chosen dark theme (Ink when the saved name is a
    /// desktop-only one such as "Gray").</summary>
    public static string Resolve(string? appearance, string? darkTheme, PlatformThemeVariant system)
    {
        var dark = darkTheme != null && DarkThemes.Contains(darkTheme) ? darkTheme : "Ink";
        return appearance switch
        {
            "Light" => "Light",
            "Dark" => dark,
            _ => system == PlatformThemeVariant.Light ? "Light" : dark,
        };
    }

    public void Apply(Application app, string themeName, string? accentHex)
    {
        Remove(app);
        var light = themeName == "Light";
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (!light && DarkThemes.Contains(themeName))
        {
            _overlay = new ResourceInclude(new Uri("avares://Noctis.UI/"))
            {
                Source = new Uri($"avares://Noctis.UI/Assets/Themes/{themeName}.axaml"),
            };
            app.Resources.MergedDictionaries.Add(_overlay);
        }
        if (light)
        {
            // The base Light dictionary is the desktop's legacy Light look, which kept its
            // dark-tuned white secondary text; on the phone's white pages it vanished. Keyed to
            // the Light variant only, so the always-dark overlays (Now Playing, Lyrics, in a
            // Dark ThemeVariantScope) keep their white text.
            _lightText = new ResourceDictionary();
            _lightText.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
            {
                ["SecondaryTextBrush"] = new SolidColorBrush(Color.Parse("#8A000000")),
                ["TertiaryTextBrush"] = new SolidColorBrush(Color.Parse("#61000000")),
            };
            app.Resources.MergedDictionaries.Add(_lightText);
        }

        _palette = BuildPalette(light ? null : themeName);
        app.Resources.MergedDictionaries.Add(_palette);

        var color = !string.IsNullOrWhiteSpace(accentHex) && Color.TryParse(accentHex, out var parsed)
            ? parsed
            : Color.Parse(DefaultAccent);
        // Last: every overlay defines its own AccentColorBrush et al., and the user's accent must win.
        _accent = AccentPalette.Build(color, light);
        GuardAccent(_accent, color, light ? null : themeName);
        app.Resources.MergedDictionaries.Add(_accent);
        ThemeName = themeName;
    }

    /// <summary>The floor every accent glyph, label and outline keeps against the surface it
    /// sits on, and every glyph on an accent fill against that fill: WCAG's 3:1 for large text
    /// and UI parts. The default Crimson clears it as is everywhere.</summary>
    public const double MinAccentContrast = 3.0;

    internal static readonly string[] SliderAccentKeys =
    {
        "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
        "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
    };

    /// <summary>
    /// The phone's contrast guards over the shared accent palette. On an accent fill (Play,
    /// chips, the selected tab's tile, switches) the glyph is white unless white falls under
    /// the floor (Tangerine, Emerald: white on them read 2.2-2.4:1), then black. The accent
    /// text brushes are keyed per variant and stepped toward black on Light or white on dark
    /// until they clear the floor on that variant's page, grouped page and card: Emerald text
    /// on a white card read 2.2:1, Indigo glyphs on Smoke's card 1.5:1. A pair that already
    /// passes is left exactly as the desktop builds it.
    /// </summary>
    private static void GuardAccent(ResourceDictionary accent, Color color, string? darkTheme)
    {
        var onAccent = ContrastRatio(Colors.White, color) >= MinAccentContrast ? Colors.White : Colors.Black;
        foreach (var key in new[]
                 {
                     "AccentForegroundBrush",
                     "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
                     "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
                     "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
                 })
            accent[key] = new SolidColorBrush(onAccent);

        var palette = BuildPalette(darkTheme);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var isLight = variant == ThemeVariant.Light;
            var built = AccentPalette.Build(color, isLight);
            var surfaces = (ResourceDictionary)palette.ThemeDictionaries[variant];
            var behind = new[] { "PhoneBackgroundBrush", "PhoneGroupedBackgroundBrush", "PhoneCardBrush" }
                .Select(k => ((ISolidColorBrush)surfaces[k]!).Color)
                .ToArray();
            var text = new ResourceDictionary();
            foreach (var key in new[] { "AccentTextBrush", "AccentTextExactBrush" })
            {
                accent.Remove(key);
                text[key] = new SolidColorBrush(Readable(((ISolidColorBrush)built[key]!).Color, behind, isLight ? Colors.Black : Colors.White));
            }
            // Fluent's slider fill and thumb (Now Playing's seek and volume bars, on the dark
            // scrim): the guarded accent too. The thumb's colour is a template value, which no
            // page style can override, so the resource is the one place to set it.
            foreach (var key in SliderAccentKeys)
                text[key] = text["AccentTextExactBrush"];
            accent.ThemeDictionaries[variant] = text;
        }
    }

    /// <summary>The colour itself when it clears the floor on every surface, else the first
    /// 5 % step toward <paramref name="toward"/> that does.</summary>
    internal static Color Readable(Color color, IReadOnlyList<Color> surfaces, Color toward)
    {
        for (var step = 0; step <= 20; step++)
        {
            var t = step / 20.0;
            var c = Color.FromRgb(
                (byte)Math.Round(color.R + (toward.R - color.R) * t),
                (byte)Math.Round(color.G + (toward.G - color.G) * t),
                (byte)Math.Round(color.B + (toward.B - color.B) * t));
            if (surfaces.All(s => ContrastRatio(c, s) >= MinAccentContrast)) return c;
        }
        return toward;
    }

    /// <summary>WCAG 2 contrast ratio of two opaque colours (1 = the same, 21 = black on white).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        static double Linear(byte channel)
        {
            var v = channel / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public void Remove(Application app)
    {
        if (_overlay != null) app.Resources.MergedDictionaries.Remove(_overlay);
        if (_lightText != null) app.Resources.MergedDictionaries.Remove(_lightText);
        if (_palette != null) app.Resources.MergedDictionaries.Remove(_palette);
        if (_accent != null) app.Resources.MergedDictionaries.Remove(_accent);
        _palette = null;
        _overlay = null;
        _lightText = null;
        _accent = null;
        ThemeName = null;
    }
}
