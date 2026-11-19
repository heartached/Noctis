using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// "Accent follows album art" (Settings → Appearance): turns the cover's vibrant colour
/// into one that can carry buttons, links and the now-playing row. The share-card vibrant
/// colour is clamped for a dark background; an accent needs mid lightness and real chroma.
/// </summary>
public static class ArtworkAccent
{
    private const double MinSaturation = 0.5;
    private const double MinLightness = 0.42;
    private const double MaxLightness = 0.6;
    /// <summary>Below this the cover is effectively grey: a grey accent reads as broken, so callers keep the user's own accent.</summary>
    private const double GreyThreshold = 0.12;

    /// <summary>Returns a legible accent hex derived from the artwork colour, or null when the cover is too grey to use.</summary>
    public static string? TameForAccent(string? artworkHex)
    {
        if (string.IsNullOrWhiteSpace(artworkHex) || !Color.TryParse(artworkHex, out var color))
            return null;

        var hsl = color.ToHsl();
        if (hsl.S < GreyThreshold) return null;

        var s = Math.Max(hsl.S, MinSaturation);
        var l = Math.Clamp(hsl.L, MinLightness, MaxLightness);
        var rgb = new HslColor(1, hsl.H, s, l).ToRgb();
        return $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
    }

    /// <summary>
    /// The accent of a black-and-white cover (GitHub #255): near white on a dark theme, near
    /// black on a light one, so it reads on the page either way. Such a cover used to fall back
    /// to the user's own accent, which looked like "Accent follows album art" had not applied.
    /// </summary>
    public static string NeutralAccent(bool lightTheme) => lightTheme ? "#2B2B2B" : "#F0F0F0";

    /// <summary>
    /// The accent for a cover whose colour is <paramref name="artworkHex"/> (the vibrant colour,
    /// or the plain average of a grey cover): its tamed colour, the neutral accent when it is
    /// grey, null without a readable cover (the caller keeps the user's own accent).
    /// </summary>
    public static string? ForCover(string? artworkHex, bool lightTheme)
    {
        if (string.IsNullOrWhiteSpace(artworkHex) || !Color.TryParse(artworkHex, out _))
            return null;
        return TameForAccent(artworkHex) ?? NeutralAccent(lightTheme);
    }
}
