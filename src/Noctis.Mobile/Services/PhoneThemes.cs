using Avalonia.Media;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>
/// One phone theme as Settings › Theme lists it: the surfaces every page, card, row and glass is
/// painted with (<see cref="MobileTheme.BuildPalette"/> turns them into the palette), and for a
/// colour theme its own accent and the background it pairs with.
/// </summary>
public sealed record PhoneThemeChoice(string Id, string Group, Color Page, Color Grouped, Color Card, Color Separator,
    Color Fill, Color Chrome, Color Secondary, Color Chevron, Color? Accent, BackgroundChoice? Pair)
{
    public bool IsLight => Id == PhoneThemes.Light;

    /// <summary>The name Settings shows: a colour theme takes its background's ("Forest"), the
    /// others their own ("Ink").</summary>
    public string Name => Pair?.Name ?? Loc.T("Mobile.Theme." + Id);
}

/// <summary>
/// The phone's themes (beta tester feedback 11-04: the cards stayed the same grey whatever the
/// background, so the look broke): Light, the four dark styles the desktop also has, and a dark
/// theme for each Colour background (<see cref="BackgroundCatalog.Colours"/>, same id), whose
/// page, cards, grouped rows, hairlines, chrome glass, secondary text and accent are all drawn
/// from that background's colours. Subtle on purpose: a theme's card is the neutral card with a
/// third of the colour mixed in, its page the background's own deep floor.
/// <para>Rules. Secondary text is a translucent light grey (Apple's secondaryLabel), as opaque as
/// it must be to keep WCAG AA (4.5:1) on the page, the grouped rows and the cards; primary text
/// is white on every dark theme and black on Light. Accent: a colour theme defines one (its
/// hue, vivid), used while Settings › Accent › Match Theme is on; Light and the dark styles
/// define none, so the chosen accent always shows there.</para>
/// </summary>
public static class PhoneThemes
{
    public const string Light = "Light";
    public const string Standard = "Standard";
    public const string Colour = "Colour";

    /// <summary>The floor every secondary label keeps against the page, the grouped rows and the cards.</summary>
    public const double MinSecondaryContrast = 4.5;

    private static Color P(string hex) => Color.Parse(hex);

    private static PhoneThemeChoice Neutral(string id, string page, string grouped, string card, string separator, string fill,
        string chrome, string secondaryBase, string chevron)
    {
        var (pg, gr, cd) = (P(page), P(grouped), P(card));
        return new PhoneThemeChoice(id, Standard, pg, gr, cd, P(separator), P(fill), P(chrome),
            Secondary(P(secondaryBase), 0x99, pg, gr, cd), P(chevron), null, null);
    }

    /// <summary>A dark theme from a Colour background: its floor is the page, its highlight mixed
    /// into the neutral (Ink) surfaces, a vivid shade of its hue the accent.</summary>
    internal static PhoneThemeChoice FromBackground(BackgroundChoice c)
    {
        var tint = c.Top;
        Color T(string neutral, double amount) => BackgroundCatalog.Mix(P(neutral), tint, amount);
        var page = c.Floor;
        var grouped = T("#1C1C1E", 0.25);
        var card = T("#2C2C2E", 0.32);
        var chrome = T("#2C2C2F", 0.40);
        BackgroundCatalog.ToHsv(tint, out var h, out var s, out _);
        var light = BackgroundCatalog.Mix(P("#EBEBF5"), BackgroundCatalog.FromHsv(h, Math.Min(1, s), 1), 0.18);
        var accent = BackgroundCatalog.FromHsv(h, 0.58, 0.92);
        // Its secondary text also reads on its cards and rows as glass over its own background's
        // brightest washes (PhoneBackground's layer), not only on the flat page.
        var mesh = BackgroundCatalog.For(c, light: false);
        var surfaces = new List<Color> { page, grouped, card };
        foreach (var wash in new[] { mesh.Floor, mesh.A, mesh.B, mesh.C })
        {
            surfaces.Add(Over(Color.FromArgb(PhoneBackground.CardGlassAlpha, card.R, card.G, card.B), wash));
            surfaces.Add(Over(Color.FromArgb(PhoneBackground.GroupedGlassAlpha, grouped.R, grouped.G, grouped.B), wash));
        }
        return new PhoneThemeChoice(c.Id, Colour, page, grouped, card, T("#38383A", 0.35), grouped,
            Color.FromArgb(0xB3, chrome.R, chrome.G, chrome.B), Secondary(light, 0x99, surfaces.ToArray()),
            T("#5A5A5E", 0.30), accent, c);
    }

    /// <summary>Light, then the four dark styles (the palettes the phone always had).</summary>
    public static IReadOnlyList<PhoneThemeChoice> Standards { get; } = new[]
    {
        new PhoneThemeChoice(Light, Standard, P("#FFFFFF"), P("#F2F2F7"), P("#FFFFFF"), P("#D8D8DC"), P("#1F767680"),
            P("#99FAFAFA"), Secondary(P("#3C3C43"), 0x99, P("#FFFFFF"), P("#F2F2F7"), P("#FFFFFF")), P("#C4C4C7"), null, null),
        Neutral("Ink", "#000000", "#1C1C1E", "#2C2C2E", "#38383A", "#1C1C1E", "#B32C2C2F", "#EBEBF5", "#5A5A5E"),
        Neutral("Smoke", "#22242C", "#2A2D38", "#343844", "#3E4250", "#2A2D38", "#B3343844", "#E6E8F0", "#5E6270"),
        Neutral("Dark", "#000000", "#1C1C1E", "#2C2C2E", "#38383A", "#1C1C1E", "#B32C2C2F", "#EBEBF5", "#5A5A5E"),
        Neutral("Midnight", "#0E1322", "#161D31", "#1E2740", "#2A3552", "#1A2238", "#B31E2740", "#DCE3F5", "#4A5372"),
    };

    /// <summary>One per Colour background, in hue order round the wheel.</summary>
    public static IReadOnlyList<PhoneThemeChoice> Colours { get; } = new[]
        { "rose", "coral", "ember", "amber", "forest", "mint", "lagoon", "harbour", "cobalt", "iris", "dusk", "orchid" }
        .Select(id => FromBackground(BackgroundCatalog.Find(id)!))
        .ToArray();

    public static IEnumerable<PhoneThemeChoice> All => Standards.Concat(Colours);

    /// <summary>Every theme that can be the dark side (all but Light).</summary>
    public static IEnumerable<PhoneThemeChoice> Darks => All.Where(t => !t.IsLight);

    public static PhoneThemeChoice? Find(string? id) =>
        id == null ? null : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The dark theme <paramref name="id"/> names, else Ink.</summary>
    public static PhoneThemeChoice DarkOrInk(string? id) => Find(id) is { IsLight: false } t ? t : Find("Ink")!;

    /// <summary>
    /// The phone's dark theme from settings.json. Saved since themes came in: MobileThemeName.
    /// Before (the migration): the desktop's <see cref="AppSettings.Theme"/> when it is one of the
    /// four dark styles (else Ink, as the phone always read it), unless a Colour background was on,
    /// whose own theme it becomes, so the cards on it are no longer the neutral grey. A Neutral
    /// background, Off or Now Playing keeps the dark style. Light and System stay in
    /// MobileAppearance as they were.
    /// </summary>
    public static string DarkThemeFor(AppSettings settings)
    {
        if (Find(settings.MobileThemeName) is { IsLight: false } saved) return saved.Id;
        if (Colours.FirstOrDefault(t => string.Equals(t.Id, settings.MobileBackground, StringComparison.OrdinalIgnoreCase)) is { } paired)
            return paired.Id;
        return MobileTheme.DarkThemes.Contains(settings.Theme) ? settings.Theme : "Ink";
    }

    /// <summary>
    /// Writes the migrated theme once (<see cref="DarkThemeFor"/>), so a later Background change
    /// cannot move it. Match Theme for the accent stays on only when the accent was never changed
    /// from the default: a chosen accent is kept. True when it wrote.
    /// </summary>
    public static async Task<bool> MigrateAsync(IPersistenceService persistence, AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.MobileThemeName)) return false;
        var dark = DarkThemeFor(settings);
        var follows = string.IsNullOrWhiteSpace(settings.AccentColorHex)
                      || string.Equals(settings.AccentColorHex, MobileTheme.DefaultAccent, StringComparison.OrdinalIgnoreCase);
        settings.MobileThemeName = dark;
        settings.MobileAccentFollowsTheme = follows;
        try
        {
            await persistence.UpdateSettingsAsync(s =>
            {
                if (!string.IsNullOrEmpty(s.MobileThemeName)) return;   // written in between: keep it
                s.MobileThemeName = dark;
                s.MobileAccentFollowsTheme = follows;
            });
        }
        catch (Exception ex)
        {
            // The look is applied all the same; the next start migrates again.
            DebugLog.Write("Theme", $"Theme migration save failed: {ex.Message}");
            return false;
        }
        return true;
    }

    /// <summary>The accent the app wears: the theme's own while Match Theme is on and the theme
    /// has one, else the chosen one.</summary>
    public static string EffectiveAccent(string resolvedTheme, string accentHex, bool followsTheme) =>
        followsTheme && Find(resolvedTheme)?.Accent is { } own ? $"#{own.R:X2}{own.G:X2}{own.B:X2}" : accentHex;

    /// <summary>The same with Settings › Accent › Match Artwork: while on, the playing cover's
    /// colour (<paramref name="cover"/>, its vibrant colour: <see cref="Noctis.Services.ArtworkVibrantColor.GetAccentSourceHex"/>) stands
    /// in for the chosen accent, which comes back with no cover or a grey one. Match Theme still
    /// wins on a colour theme.</summary>
    public static string EffectiveAccent(string resolvedTheme, string accentHex, bool followsTheme, bool followsArtwork, Color? cover) =>
        EffectiveAccent(resolvedTheme, followsArtwork && ArtworkAccentHex(cover) is { } artwork ? artwork : accentHex, followsTheme);

    /// <summary>A cover colour as an accent: the desktop's "Accent follows album art" taming
    /// (<see cref="Noctis.Helpers.ArtworkAccent.TameForAccent"/>: real chroma at mid lightness),
    /// null for no cover or a grey one. The phone's contrast guards (MobileTheme) then fit it to
    /// the theme like any other accent.</summary>
    public static string? ArtworkAccentHex(Color? cover) =>
        cover is { } c ? Noctis.Helpers.ArtworkAccent.TameForAccent($"#{c.R:X2}{c.G:X2}{c.B:X2}") : null;

    /// <summary><paramref name="light"/> as translucent text: the first alpha from
    /// <paramref name="startAlpha"/> up that keeps <see cref="MinSecondaryContrast"/> on every surface.</summary>
    internal static Color Secondary(Color light, byte startAlpha, params Color[] surfaces)
    {
        for (int a = startAlpha; a <= 255; a += 4)
        {
            var c = Color.FromArgb((byte)a, light.R, light.G, light.B);
            if (surfaces.All(s => MobileTheme.ContrastRatio(Over(c, s), s) >= MinSecondaryContrast)) return c;
        }
        return Color.FromRgb(light.R, light.G, light.B);
    }

    /// <summary><paramref name="top"/> composited over an opaque <paramref name="under"/>.</summary>
    public static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte M(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(M(top.R, under.R), M(top.G, under.G), M(top.B, under.B));
    }
}
