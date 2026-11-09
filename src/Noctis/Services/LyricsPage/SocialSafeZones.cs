using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>Distance in canvas pixels from each edge that text must stay clear of.</summary>
public readonly record struct SafeInsets(float Left, float Top, float Right, float Bottom);

/// <summary>
/// Where social apps paint their own UI over a posted video, so the Lyrics Page keeps
/// its header and lyrics out of those areas.
/// </summary>
/// <remarks>
/// Story (1080×1920): Meta Ads Guide, Reels/Stories safe zones — keep the top 14 %
/// (270 px of 1920), the bottom 35 % (672 px) and 6 % per side (65 px) free of text.
/// The right edge is widened to 140 px for the TikTok In-Feed safe-zone template's
/// action rail (likes/comments column) and Instagram's Reels button rail: the "Lost Me"
/// share put text under the Likes button, which is why 65 px is not enough there.
/// Square (1080×1080): Instagram's profile grid crops 1:1 posts to the centre 3:4
/// (~810 px wide), as the card's comment in ShareCardRenderer.DrawPanelContent states,
/// so 135 px per side stays out of the crop.
/// Off: a plain 60 px margin all round, no zones.
/// </remarks>
public static class SocialSafeZones
{
    private const float PlainMargin = 60f;

    public static SafeInsets For(ShareCardFormat format, bool enabled)
    {
        if (!enabled)
            return new SafeInsets(PlainMargin, PlainMargin, PlainMargin, PlainMargin);

        return format == ShareCardFormat.Story
            ? new SafeInsets(65, 270, 140, 672)
            : new SafeInsets(135, 60, 135, 60);
    }

    /// <summary>The covered areas as canvas rects; empty when safe zones are off.</summary>
    public static IReadOnlyList<SKRect> Zones(ShareCardFormat format, bool enabled)
    {
        if (!enabled)
            return Array.Empty<SKRect>();

        return format == ShareCardFormat.Story
            ? new[]
            {
                new SKRect(0, 0, 1080, 270),
                new SKRect(0, 1248, 1080, 1920),
                new SKRect(0, 270, 65, 1248),
                new SKRect(940, 270, 1080, 1248),
            }
            : new[]
            {
                new SKRect(0, 0, 135, 1080),
                new SKRect(945, 0, 1080, 1080),
            };
    }
}
