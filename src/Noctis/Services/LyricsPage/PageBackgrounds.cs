using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// What sits behind the lyrics of one Lyrics Page video, drawn in pixel space at the
/// size it was created for. <paramref name="clipSeconds"/> is the time since the clip start.
/// </summary>
internal interface IPageBackground : IDisposable
{
    void Draw(SKCanvas canvas, double clipSeconds);
}

/// <summary>UniformToFill as a source crop: the centred part of the source with the destination's aspect.</summary>
public static class BackgroundFit
{
    public static SKRect CoverSource(int srcW, int srcH, int dstW, int dstH)
    {
        if ((long)srcW * dstH > (long)srcH * dstW)
        {
            // Source is wider: keep the full height, crop the sides.
            double cropW = (double)srcH * dstW / dstH;
            double left = (srcW - cropW) / 2.0;
            return new SKRect((float)left, 0, (float)(left + cropW), srcH);
        }

        double cropH = (double)srcW * dstH / dstW;
        double top = (srcH - cropH) / 2.0;
        return new SKRect(0, (float)top, srcW, (float)(top + cropH));
    }
}

/// <summary>
/// The readability layer over a background: at the default strength it matches the lyrics
/// page's #99000000 scrim; still images and clips get a dark tint of the cover colour instead
/// of plain black.
/// </summary>
public static class PageBackgroundTint
{
    public const int DefaultStrength = 70;

    public static byte OverlayAlpha(int strength) =>
        (byte)Math.Round(255 * 0.85 * Math.Clamp(strength, 0, 100) / 100.0);

    public static SKColor Tint(SKColor vibrant)
    {
        vibrant.ToHsl(out var hue, out var saturation, out _);
        return SKColor.FromHsl(hue, Math.Min(saturation, 60f), 12f);
    }

    public static bool IsStillImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A background rendered once at construction and blitted every frame.</summary>
internal abstract class PrerenderedBackground : IPageBackground
{
    private readonly SKBitmap _frame;

    protected PrerenderedBackground(int w, int h)
    {
        _frame = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    protected SKCanvas OpenCanvas() => new(_frame);

    protected static void Scrim(SKCanvas canvas, SKColor color, int w, int h)
    {
        if (color.Alpha == 0) return;
        using var paint = new SKPaint { Color = color, BlendMode = SKBlendMode.SrcOver };
        canvas.DrawRect(0, 0, w, h, paint);
    }

    public void Draw(SKCanvas canvas, double clipSeconds) => canvas.DrawBitmap(_frame, 0, 0);

    public void Dispose() => _frame.Dispose();
}

/// <summary>
/// The lyrics page backdrop: the cover blurred and scaled ×1.18 (<see cref="ShareCardRenderer.DrawBlurredCover"/>)
/// under a black scrim at <see cref="PageBackgroundTint.OverlayAlpha"/>. Without artwork the
/// vibrant-colour fallback #1A1A2E stands in for the cover.
/// </summary>
internal sealed class WashBackground : PrerenderedBackground
{
    internal WashBackground(SKBitmap? art, int w, int h, int strength) : base(w, h)
    {
        using var canvas = OpenCanvas();
        canvas.Clear(SKColors.Black);
        if (art != null)
        {
            // Drawn in the 1080-wide card space so the blur reads the same in the half-size
            // preview as in the export.
            float scale = w / 1080f;
            canvas.Save();
            canvas.Scale(scale);
            ShareCardRenderer.DrawBlurredCover(canvas, art, 1080, (int)Math.Round(h / scale));
            canvas.Restore();
        }
        else
        {
            canvas.Clear(new SKColor(0x1A, 0x1A, 0x2E));
        }
        Scrim(canvas, SKColors.Black.WithAlpha(PageBackgroundTint.OverlayAlpha(strength)), w, h);
        canvas.Flush();
    }
}

/// <summary>A still picture cropped to fill (<see cref="BackgroundFit.CoverSource"/>) under the cover-colour tint.</summary>
internal sealed class StillBackground : PrerenderedBackground
{
    internal StillBackground(SKBitmap image, int w, int h, SKColor tint, int strength) : base(w, h)
    {
        using var canvas = OpenCanvas();
        canvas.Clear(SKColors.Black);
        using (var source = SKImage.FromBitmap(image))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(source, BackgroundFit.CoverSource(image.Width, image.Height, w, h),
                new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        Scrim(canvas, tint.WithAlpha(PageBackgroundTint.OverlayAlpha(strength)), w, h);
        canvas.Flush();
    }
}

internal static class PageBackgrounds
{
    /// <summary>
    /// The background for <paramref name="spec"/>. Anything that cannot be used — no media
    /// file, an undecodable picture, no ffmpeg for a clip — falls back to the artwork wash.
    /// <paramref name="art"/> is the decoded cover (not owned).
    /// </summary>
    internal static IPageBackground Create(LyricsPageSpec spec, int pixelWidth, int pixelHeight, int fps, bool blocking, SKBitmap? art)
    {
        int strength = spec.BackgroundStrength;
        if (spec.BackgroundKind == LyricsPageBackgroundKind.ArtworkWash
            || string.IsNullOrEmpty(spec.BackgroundMediaPath)
            || !File.Exists(spec.BackgroundMediaPath))
            return new WashBackground(art, pixelWidth, pixelHeight, strength);

        var path = spec.BackgroundMediaPath;
        var tint = PageBackgroundTint.Tint(SKColor.Parse(ShareCardRenderer.GetVibrantColorHex(spec.ArtworkPath)));

        if (PageBackgroundTint.IsStillImage(path))
        {
            SKBitmap? image = null;
            try { image = SKBitmap.Decode(path); } catch { }
            if (image == null)
                return new WashBackground(art, pixelWidth, pixelHeight, strength);
            using (image)
                return new StillBackground(image, pixelWidth, pixelHeight, tint, strength);
        }

        var wash = new WashBackground(art, pixelWidth, pixelHeight, strength);
        if (string.IsNullOrEmpty(spec.FfmpegPath))
            return wash;
        return new ClipBackground(spec.FfmpegPath, path, pixelWidth, pixelHeight, fps,
            tint.WithAlpha(PageBackgroundTint.OverlayAlpha(strength)), blocking, wash);
    }
}
