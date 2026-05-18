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
}
