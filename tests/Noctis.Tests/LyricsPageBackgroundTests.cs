using Noctis.Services.LyricsPage;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public sealed class LyricsPageBackgroundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-page-bg-" + Guid.NewGuid().ToString("N"));

    public LyricsPageBackgroundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static LyricsPageSpec Custom(string path, string? ffmpeg = null) =>
        LyricsPageTestData.Spec() with
        {
            BackgroundKind = LyricsPageBackgroundKind.Custom,
            BackgroundMediaPath = path,
            FfmpegPath = ffmpeg,
        };

    private static SKBitmap Render(IPageBackground background, int w, int h, double seconds = 0)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        background.Draw(canvas, seconds);
        canvas.Flush();
        return bitmap;
    }

    private string WritePng(string name, int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Cover_fit_crops_the_centre_to_fill()
    {
        Assert.Equal(new SKRect(656.25f, 0, 1263.75f, 1080), BackgroundFit.CoverSource(1920, 1080, 1080, 1920));
        Assert.Equal(new SKRect(0, 0, 1000, 1000), BackgroundFit.CoverSource(1000, 1000, 1080, 1080));
        Assert.Equal(new SKRect(0, 1500, 1080, 2340), BackgroundFit.CoverSource(1080, 3840, 1080, 840));
    }

    [Fact]
    public void Readability_strength_maps_to_the_page_scrim()
    {
        Assert.Equal(0, PageBackgroundTint.OverlayAlpha(0));
        Assert.Equal(152, PageBackgroundTint.OverlayAlpha(70));   // ≈ the page's #99 scrim
        Assert.Equal(217, PageBackgroundTint.OverlayAlpha(100));
        Assert.Equal(PageBackgroundTint.OverlayAlpha(100), PageBackgroundTint.OverlayAlpha(250));
        Assert.Equal(0, PageBackgroundTint.OverlayAlpha(-5));
        PageBackgroundTint.Tint(new SKColor(200, 40, 40)).ToHsl(out _, out var s, out var l);
        Assert.InRange(l, 11.5f, 12.5f); Assert.True(s <= 60.5f);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.Png", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.gif", false)]
    [InlineData("a", false)]
    public void Still_images_are_told_apart_from_clips_by_extension(string path, bool still) =>
        Assert.Equal(still, PageBackgroundTint.IsStillImage(path));

    [Fact]
    public void Missing_or_unreadable_custom_file_falls_back_to_the_wash()
    {
        using (var missing = PageBackgrounds.Create(Custom(Path.Combine(_dir, "nope.mp4"), "ffmpeg"), 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(missing);

        var textPng = Path.Combine(_dir, "text.png");
        File.WriteAllText(textPng, "this is not a picture");
        using (var unreadable = PageBackgrounds.Create(Custom(textPng), 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(unreadable);

        using (var noMedia = PageBackgrounds.Create(LyricsPageTestData.Spec() with
               { BackgroundKind = LyricsPageBackgroundKind.AnimatedCover }, 108, 192, 60, true, null))
            Assert.IsType<WashBackground>(noMedia);
    }

    [Fact]
    public void Still_image_is_scaled_to_fill_and_tinted()
    {
        var png = WritePng("red.png", 200, 100, new SKColor(255, 0, 0));
        using var background = PageBackgrounds.Create(Custom(png), 108, 192, 60, true, null);
        Assert.IsType<StillBackground>(background);

        using var frame = Render(background, 108, 192);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                var p = frame.GetPixel(x, y);
                Assert.True(p.Red > p.Green + 40 && p.Red > p.Blue + 40, $"({x},{y}) = {p}");
            }

        // The tint sits over the picture at the readability overlay: 70 % → alpha 152 → red ≈ 255 × (1 − 152/255).
        var centre = frame.GetPixel(54, 96);
        var expected = 255 * (1 - PageBackgroundTint.OverlayAlpha(70) / 255.0);
        Assert.InRange(centre.Red, expected - 2, expected + 30);
    }

    [Fact]
    public void Artwork_wash_is_the_blurred_cover_under_the_scrim()
    {
        using var art = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        art.Erase(new SKColor(0, 200, 0));
        using var wash = new WashBackground(art, 108, 192, 70);
        using var frame = Render(wash, 108, 192);
        var c = frame.GetPixel(54, 96);
        var expected = 200 * (1 - PageBackgroundTint.OverlayAlpha(70) / 255.0);
        Assert.InRange(c.Green, expected - 3, expected + 3);
        Assert.True(c.Red < 5 && c.Blue < 5, c.ToString());

        // No artwork: the vibrant fallback #1A1A2E under the same scrim.
        using var plain = new WashBackground(null, 108, 192, 0);
        using var plainFrame = Render(plain, 108, 192);
        Assert.Equal(new SKColor(0x1A, 0x1A, 0x2E), plainFrame.GetPixel(54, 96));
    }

    [Fact]
    public void Artwork_wash_kind_ignores_the_media_path()
    {
        var png = WritePng("red2.png", 20, 20, SKColors.Red);
        using var background = PageBackgrounds.Create(LyricsPageTestData.Spec() with
            { BackgroundKind = LyricsPageBackgroundKind.ArtworkWash, BackgroundMediaPath = png }, 108, 192, 60, true, null);
        Assert.IsType<WashBackground>(background);
    }
}
