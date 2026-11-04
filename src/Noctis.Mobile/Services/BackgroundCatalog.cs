using Avalonia.Media;
using Noctis.Services;
using Noctis.Localization;

namespace Noctis.Mobile.Services;

/// <summary>One mesh background: its name and three colours, the highlight (top left), the mid
/// tone (lower right) and the floor everything sits on, as the dark theme shows them.</summary>
public sealed record BackgroundChoice(string Id, string EnglishName, string Group, Color Top, Color Mid, Color Floor)
{
    /// <summary>The name Settings shows, in the current language ("Plum Ink" → Mobile.BackgroundName.PlumInk).</summary>
    public string Name => Loc.T("Mobile.BackgroundName." + EnglishName.Replace(" ", string.Empty));
}

/// <summary>
/// The backgrounds Settings › Background offers (the approved mockup's Neutral and Colour sets)
/// and the maths that turns one into the colours a light or a dark page wears. Every wash is
/// kept inside a luminance band, so the text and the translucent cards over the mesh keep their
/// contrast whatever the choice or the cover (the background tests check the worst case).
/// </summary>
public static class BackgroundCatalog
{
    public const string Neutral = "Neutral";
    public const string Colour = "Colour";

    private static BackgroundChoice C(string id, string name, string group, string top, string mid, string floor) =>
        new(id, name, group, Color.Parse(top), Color.Parse(mid), Color.Parse(floor));

    public static IReadOnlyList<BackgroundChoice> Neutrals { get; } = new[]
    {
        C("graphite", "Graphite", Neutral, "#3A3D44", "#272A30", "#15171B"),
        C("ash", "Ash", Neutral, "#5D4B46", "#3A3036", "#1A1617"),
        C("plum-ink", "Plum Ink", Neutral, "#4E3A44", "#33232E", "#1A1316"),
        C("olive", "Olive", Neutral, "#454234", "#2C2A20", "#171611"),
        C("slate", "Slate", Neutral, "#3B4450", "#262D37", "#12161B"),
        C("pine", "Pine", Neutral, "#34483E", "#22302A", "#101713"),
        C("sand", "Sand", Neutral, "#574C3C", "#37302A", "#1C1814"),
        C("mist", "Mist", Neutral, "#444A5C", "#2D3140", "#15171D"),
    };

    public static IReadOnlyList<BackgroundChoice> Colours { get; } = new[]
    {
        C("ember", "Ember", Colour, "#8A3A2C", "#4A1D3D", "#1C0E17"),
        C("harbour", "Harbour", Colour, "#22566A", "#163340", "#0B1418"),
        C("amber", "Amber", Colour, "#7A5A1E", "#5A2A14", "#1A0E08"),
        C("dusk", "Dusk", Colour, "#3D3F86", "#2A1A4A", "#0F0C1C"),
        C("rose", "Rose", Colour, "#8A3A55", "#4A1A30", "#180A10"),
        C("lagoon", "Lagoon", Colour, "#1F6A62", "#12353A", "#091416"),
        C("orchid", "Orchid", Colour, "#7A3D8A", "#34194A", "#130A1A"),
        C("forest", "Forest", Colour, "#2D6A3A", "#16382A", "#09140D"),
        C("coral", "Coral", Colour, "#A14B3C", "#6A2A36", "#1C0E10"),
        C("iris", "Iris", Colour, "#4A45A0", "#2A2468", "#0E0C20"),
        C("cobalt", "Cobalt", Colour, "#2A58A8", "#17306A", "#0A1226"),
        C("mint", "Mint", Colour, "#2F7A5C", "#1C4A4A", "#0A1A1A"),
    };

    public static IEnumerable<BackgroundChoice> All => Neutrals.Concat(Colours);

    public static BackgroundChoice? Find(string? id) =>
        id == null ? null : All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The saved id when it names a background, else <see cref="PhoneBackground.Off"/>.</summary>
    public static string Normalize(string? id) =>
        string.Equals(id, PhoneBackground.Artwork, StringComparison.OrdinalIgnoreCase) ? PhoneBackground.Artwork
        : Find(id)?.Id ?? PhoneBackground.Off;

    /// <summary>What a wash may reach on a dark page (relative luminance): bright enough to show,
    /// dim enough that white text and the translucent cards on it stay readable.</summary>
    public const double DarkMaxLuminance = 0.07;

    /// <summary>The floor of a light page's wash, so black text keeps its contrast.</summary>
    public const double LightMinLuminance = 0.75;

    /// <summary>The mesh colours of a page: the floor, then the three washes (highlight, mid tone,
    /// and a blend of the two for the lower left).</summary>
    public readonly record struct Palette(Color Floor, Color A, Color B, Color C);

    public static Palette For(BackgroundChoice choice, bool light) => Build(choice.Top, choice.Mid, choice.Floor, light, null);

    /// <summary>The mesh a cover paints: its colour, and two neighbours round the hue wheel
    /// (a grey one when there is no cover yet).</summary>
    public static Palette ForCover(Color? cover, bool light)
    {
        if (cover is not { } c) return For(Neutrals[0], light);
        var deep = DominantColorExtractor.DeepPageColor(c);
        return Build(c, RotateHue(c, 32), Mix(Colors.Black, deep, 0.5), light, RotateHue(c, -32));
    }

    private static Palette Build(Color top, Color mid, Color floor, bool light, Color? third)
    {
        var blend = third ?? Mix(top, mid, 0.5);
        if (!light)
            return new Palette(floor, Cap(top, DarkMaxLuminance), Cap(mid, DarkMaxLuminance * 0.8), Cap(blend, DarkMaxLuminance * 0.7));
        // Light: a pastel of each colour over a barely tinted grey, Apple's grouped page.
        var page = Mix(Color.Parse("#F2F2F7"), top, 0.08);
        return new Palette(page, Pastel(top, 0.4), Pastel(mid, 0.34), Pastel(blend, 0.3));
    }

    private static Color Pastel(Color c, double amount) => Lift(Mix(Colors.White, Saturate(c), amount), LightMinLuminance);

    /// <summary>A colour scaled toward black until its luminance is at most <paramref name="max"/>.</summary>
    internal static Color Cap(Color c, double max)
    {
        for (var t = 1.0; t > 0.05; t -= 0.05)
        {
            var d = Mix(Colors.Black, c, t);
            if (DominantColorExtractor.GetRelativeLuminance(d) <= max) return d;
        }
        return Mix(Colors.Black, c, 0.05);
    }

    /// <summary>A colour mixed toward white until its luminance is at least <paramref name="min"/>.</summary>
    private static Color Lift(Color c, double min)
    {
        for (var t = 0.0; t <= 1; t += 0.05)
        {
            var l = Mix(c, Colors.White, t);
            if (DominantColorExtractor.GetRelativeLuminance(l) >= min) return l;
        }
        return Colors.White;
    }

    /// <summary>A muted colour pushed toward a vivid one of its hue, so a pastel of a grey-brown
    /// is still a tint and not grey.</summary>
    private static Color Saturate(Color c)
    {
        ToHsv(c, out var h, out var s, out var v);
        return FromHsv(h, Math.Min(1, s * 1.6 + 0.1), Math.Max(v, 0.7));
    }

    public static Color Mix(Color under, Color over, double amount)
    {
        byte Lerp(byte u, byte o) => (byte)Math.Round(u + (o - u) * amount);
        return Color.FromRgb(Lerp(under.R, over.R), Lerp(under.G, over.G), Lerp(under.B, over.B));
    }

    public static Color RotateHue(Color c, double degrees)
    {
        ToHsv(c, out var h, out var s, out var v);
        return FromHsv((h + degrees + 360) % 360, s, v);
    }

    internal static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var d = max - Math.Min(r, Math.Min(g, b));
        v = max;
        s = max == 0 ? 0 : d / max;
        h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
    }

    internal static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
