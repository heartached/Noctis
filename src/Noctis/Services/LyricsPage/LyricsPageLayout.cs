using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// Static geometry of one Lyrics Page frame, in canvas pixels. <see cref="AnchorY"/> is
/// measured from the top of <see cref="Viewport"/>.
/// </summary>
public sealed record LyricsPageGeometry(int Width, int Height, SKRect Content, SKRect Header, float ArtSize,
    float TitleSize, float ArtistSize, SKRect Viewport, float AnchorY, float LyricSize,
    float BackgroundVocalSize, float RowHeight, float LineGap, float EdgeFade)
{
    /// <summary>Factor applied to the lyrics page's px values, which are set at a 46 px font.</summary>
    public float Scale => LyricSize / 46f;
}

public static class LyricsPageLayout
{
    /// <summary>Active line position as a share of the viewport height (LyricsView.PageAnchorRatio).</summary>
    public const double AnchorRatio = 0.32;

    public static LyricsPageGeometry Measure(ShareCardFormat format, bool safeZones)
    {
        bool story = format == ShareCardFormat.Story;
        int width = 1080;
        int height = story ? 1920 : 1080;

        float artSize = story ? 128f : 104f;
        float titleSize = story ? 44f : 38f;
        float artistSize = story ? 32f : 28f;
        float headerGap = story ? 56f : 40f;
        // About 1.6 lyric rows (RowHeight 86.4 / 69.6): one glyph height read as a slice, not a fade.
        float edgeFade = story ? 140f : 112f;
        float lyricSize = story ? 72f : 58f;

        var insets = SocialSafeZones.For(format, safeZones);
        var content = new SKRect(insets.Left, insets.Top, width - insets.Right, height - insets.Bottom);
        var header = new SKRect(content.Left, content.Top, content.Right, content.Top + artSize);
        var viewport = new SKRect(content.Left, header.Bottom + headerGap, content.Right, content.Bottom);

        float scale = lyricSize / 46f;
        return new LyricsPageGeometry(
            width, height, content, header, artSize, titleSize, artistSize, viewport,
            AnchorY: (float)(viewport.Height * AnchorRatio),
            LyricSize: lyricSize,
            BackgroundVocalSize: lyricSize * 0.6f,
            RowHeight: lyricSize * 1.2f,
            LineGap: 20f * scale,
            EdgeFade: edgeFade);
    }
}
