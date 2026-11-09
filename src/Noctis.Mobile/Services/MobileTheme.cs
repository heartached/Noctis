using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
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
    /// the Dark set; the colours are the theme's (<see cref="PhoneThemes"/>). Ink and Dark take
    /// Apple's pure black; Smoke and Midnight keep their own slate and blue-black, and a colour
    /// theme its background's floor, with surfaces stepped up from them the same way.
    /// The chrome brush is the Liquid Glass tint (translucent: what passes beneath shows
    /// through it), its outline the hairline rim every floating pill carries, and the selected
    /// brush the tab bar's inner capsule: a grey on light glass, darker than the pill on dark.
    /// Secondary text is translucent, Apple's secondaryLabel, as opaque as WCAG AA needs on the
    /// page, the grouped rows and the cards (PhoneThemes.Secondary). The explicit badge is the
    /// desktop's half-grey on dark, and Apple's secondary grey on Light, where the half-grey
    /// over white left its white "E" at 1.8:1. A colour theme also tints the desktop keys the
    /// phone shows on its pages (HomeCardBackground's tiles, SecondaryTextBrush).
    /// </summary>
    public static ResourceDictionary BuildPalette(string? darkTheme = null)
    {
        static SolidColorBrush B(Color c) => new(c);
        var light = PhoneThemes.Find(PhoneThemes.Light)!;
        var dark = PhoneThemes.DarkOrInk(darkTheme);
        var palette = new ResourceDictionary();
        palette.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["PhoneBackgroundBrush"] = B(light.Page),
            ["PhoneGroupedBackgroundBrush"] = B(light.Grouped),
            ["PhoneSheetBackgroundBrush"] = B(light.Grouped),
            ["PhoneCardBrush"] = B(light.Card),
            ["PhoneSeparatorBrush"] = B(light.Separator),
            ["PhoneFillBrush"] = B(light.Fill),
            ["PhoneChromeBrush"] = B(light.Chrome),
            ["PhoneChromeOutlineBrush"] = B(Color.Parse("#21000000")),
            ["PhoneChromeSelectedBrush"] = B(Color.Parse("#12000000")),
            ["PhoneSecondaryTextBrush"] = B(light.Secondary),
            ["PhoneChevronBrush"] = B(light.Chevron),
            ["PhoneExplicitBadgeBrush"] = B(Color.Parse("#8A8A8E")),
        };
        var darkSet = new ResourceDictionary
        {
            ["PhoneBackgroundBrush"] = B(dark.Page),
            ["PhoneGroupedBackgroundBrush"] = B(dark.Grouped),
            ["PhoneSheetBackgroundBrush"] = B(dark.Grouped),
            ["PhoneCardBrush"] = B(dark.Card),
            ["PhoneSeparatorBrush"] = B(dark.Separator),
            ["PhoneFillBrush"] = B(dark.Fill),
            ["PhoneChromeBrush"] = B(dark.Chrome),
            ["PhoneChromeOutlineBrush"] = B(Color.Parse("#30FFFFFF")),
            ["PhoneChromeSelectedBrush"] = B(Color.Parse("#99000000")),
            ["PhoneSecondaryTextBrush"] = B(dark.Secondary),
            ["PhoneChevronBrush"] = B(dark.Chevron),
            ["PhoneExplicitBadgeBrush"] = B(Color.Parse("#88888888")),
        };
        if (dark.Group == PhoneThemes.Colour)
        {
            darkSet["HomeCardBackground"] = B(dark.Grouped);
            darkSet["SecondaryTextBrush"] = B(dark.Secondary);
        }
        palette.ThemeDictionaries[ThemeVariant.Dark] = darkSet;
        return palette;
    }

    /// <summary>The theme last applied ("Light" or a dark theme), or null.</summary>
    public string? ThemeName { get; private set; }

    /// <summary>The theme to run: Light, or the chosen dark theme, a dark style or a colour theme
    /// (Ink when the saved name is a desktop-only one such as "Gray").</summary>
    public static string Resolve(string? appearance, string? darkTheme, PlatformThemeVariant system)
    {
        var dark = PhoneThemes.DarkOrInk(darkTheme).Id;
        return appearance switch
        {
            "Light" => "Light",
            "Dark" => dark,
            _ => system == PlatformThemeVariant.Light ? "Light" : dark,
        };
    }

    public void Apply(Application app, string themeName, string? accentHex)
    {
        StopBlend();
        Remove(app);
        var light = themeName == "Light";
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (!light)
        {
            // A colour theme stands on Ink's desktop keys; the palette tints what the phone shows.
            var overlay = DarkThemes.Contains(themeName) ? themeName : "Ink";
            _overlay = new ResourceInclude(new Uri("avares://Noctis.UI/"))
            {
                Source = new Uri($"avares://Noctis.UI/Assets/Themes/{overlay}.axaml"),
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

        // Last: every overlay defines its own AccentColorBrush et al., and the user's accent must win.
        _accent = BuildAccent(accentHex, themeName);
        app.Resources.MergedDictionaries.Add(_accent);
        ThemeName = themeName;
    }

    /// <summary>The accent overlay for <paramref name="accentHex"/> (Crimson when unreadable) on
    /// <paramref name="themeName"/>, with the phone's contrast guards.</summary>
    private static ResourceDictionary BuildAccent(string? accentHex, string themeName)
    {
        var color = !string.IsNullOrWhiteSpace(accentHex) && Color.TryParse(accentHex, out var parsed)
            ? parsed
            : Color.Parse(DefaultAccent);
        var light = themeName == "Light";
        var accent = AccentPalette.Build(color, light);
        GuardAccent(accent, color, light ? null : themeName);
        return accent;
    }

    /// <summary>How long Match Artwork's accent takes to blend into the next cover's.</summary>
    public static readonly TimeSpan AccentBlend = TimeSpan.FromMilliseconds(450);

    private DispatcherTimer? _blendTimer;
    private Action<double>? _blendStep;
    private Action? _blendDone;
    private DateTime _blendStart;
    private TimeSpan _blendLength;

    /// <summary>The accent changes on the theme already applied, nothing else re-merged: Match
    /// Artwork on a track change (a full <see cref="Apply"/> re-resolves every resource in the app
    /// and rebuilds the palette). Over <paramref name="blend"/> the colours on screen glide from
    /// the old accent to the new one: each brush of the old overlay is recoloured in place, frame by
    /// frame, so only what is drawn with the accent repaints; at the end the new overlay (its
    /// brushes the same colours by then) takes the old one's place. A change mid-blend glides on
    /// from where the colours are. No-op before the first <see cref="Apply"/>.</summary>
    public void SetAccent(Application app, string? accentHex, TimeSpan blend = default)
    {
        if (_accent == null || ThemeName == null) return;
        StopBlend();
        var next = BuildAccent(accentHex, ThemeName);
        var pairs = BlendPairs(_accent, next);
        void Swap()
        {
            var list = app.Resources.MergedDictionaries;
            var index = _accent == null ? -1 : list.IndexOf(_accent);
            if (index >= 0) list[index] = next;
            else list.Add(next);
            _accent = next;
        }
        if (blend <= TimeSpan.Zero || pairs.Count == 0)
        {
            Swap();
            return;
        }
        _blendStep = t =>
        {
            var e = 1 - Math.Pow(1 - t, 3);   // ease out: quick to leave, soft to land
            foreach (var (brush, from, to) in pairs) brush.Color = Lerp(from, to, e);
        };
        _blendDone = Swap;
        _blendStart = DateTime.UtcNow;
        _blendLength = blend;
        _blendTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = (DateTime.UtcNow - _blendStart) / _blendLength;
            if (t >= 1) FinishBlend();
            else _blendStep?.Invoke(t);
        });
        _blendTimer.Start();
    }

    /// <summary>A blend is under way (<see cref="SetAccent"/>).</summary>
    public bool IsBlending => _blendDone != null;

    /// <summary>Paints the blend at <paramref name="t"/> (0-1) of its course. Internal for tests:
    /// the headless platform never fires a DispatcherTimer.</summary>
    internal void AdvanceBlend(double t) => _blendStep?.Invoke(Math.Clamp(t, 0, 1));

    /// <summary>Lands a blend under way at once: the new overlay in place.</summary>
    public void FinishBlend()
    {
        var done = _blendDone;
        _blendStep?.Invoke(1);
        StopBlend();
        done?.Invoke();
    }

    private void StopBlend()
    {
        _blendTimer?.Stop();
        _blendTimer = null;
        _blendStep = null;
        _blendDone = null;
    }

    /// <summary>Every brush of the overlay on screen, with its colour now and the one the same key
    /// takes in <paramref name="next"/> (the per-variant text brushes too). A brush two keys share
    /// (the slider keys are AccentTextExactBrush itself) is listed once.</summary>
    private static List<(SolidColorBrush Brush, Color From, Color To)> BlendPairs(ResourceDictionary current, ResourceDictionary next)
    {
        var pairs = new List<(SolidColorBrush, Color, Color)>();
        var seen = new HashSet<SolidColorBrush>(ReferenceEqualityComparer.Instance);
        void Add(IResourceDictionary from, IResourceDictionary to)
        {
            foreach (var key in to.Keys)
                if (from.TryGetValue(key, out var a) && a is SolidColorBrush brush && to[key] is ISolidColorBrush b && seen.Add(brush))
                    pairs.Add((brush, brush.Color, b.Color));
        }
        Add(current, next);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            if (current.ThemeDictionaries.TryGetValue(variant, out var from) && next.ThemeDictionaries.TryGetValue(variant, out var to)
                && from is IResourceDictionary f && to is IResourceDictionary n)
                Add(f, n);
        return pairs;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

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
        StopBlend();
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
