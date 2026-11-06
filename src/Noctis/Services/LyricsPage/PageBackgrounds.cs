using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// What sits behind the lyrics of one Lyrics Page video, drawn in pixel space at the
/// size it was created for. <paramref name="clipSeconds"/> is the time since the clip start.
/// </summary>
internal interface IPageBackground : IDisposable
{
    void Draw(SKCanvas canvas, double clipSeconds);

    /// <summary>The readability strength (0-100) of the layer over the picture, from the next
    /// draw on. Safe to set from any thread.</summary>
    int Strength { set; }
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

/// <summary>
/// A picture rendered once at construction and blitted every frame, with the readability
/// layer (<see cref="PageBackgroundTint.OverlayAlpha"/> of <c>scrim</c>) drawn over it per frame
/// so a strength change needs no re-render.
/// </summary>
internal abstract class PrerenderedBackground : IPageBackground
{
    private readonly SKBitmap _frame;
    private readonly SKPaint _scrimPaint = new() { BlendMode = SKBlendMode.SrcOver };
    private readonly SKColor _scrim;
    private volatile int _strength;

    protected PrerenderedBackground(int w, int h, SKColor scrim, int strength)
    {
        _frame = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul));
        _scrim = scrim;
        _strength = strength;
    }

    protected SKCanvas OpenCanvas() => new(_frame);

    public int Strength { set => _strength = value; }

    public void Draw(SKCanvas canvas, double clipSeconds)
    {
        canvas.DrawBitmap(_frame, 0, 0);
        var alpha = PageBackgroundTint.OverlayAlpha(_strength);
        if (alpha == 0) return;
        _scrimPaint.Color = _scrim.WithAlpha(alpha);
        canvas.DrawRect(0, 0, _frame.Width, _frame.Height, _scrimPaint);
    }

    public void Dispose()
    {
        _frame.Dispose();
        _scrimPaint.Dispose();
    }
}

/// <summary>
/// The lyrics page backdrop: the cover blurred and scaled ×1.18 (<see cref="ShareCardRenderer.DrawBlurredCover"/>)
/// under a black scrim at <see cref="PageBackgroundTint.OverlayAlpha"/>. Without artwork the
/// vibrant-colour fallback #1A1A2E stands in for the cover.
/// </summary>
internal sealed class WashBackground : PrerenderedBackground
{
    internal WashBackground(SKBitmap? art, int w, int h, int strength) : base(w, h, SKColors.Black, strength)
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
        canvas.Flush();
    }
}

/// <summary>A still picture cropped to fill (<see cref="BackgroundFit.CoverSource"/>) under the cover-colour tint.</summary>
internal sealed class StillBackground : PrerenderedBackground
{
    internal StillBackground(SKBitmap image, int w, int h, SKColor tint, int strength) : base(w, h, tint, strength)
    {
        using var canvas = OpenCanvas();
        canvas.Clear(SKColors.Black);
        using (var source = SKImage.FromBitmap(image))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(source, BackgroundFit.CoverSource(image.Width, image.Height, w, h),
                new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        }
        canvas.Flush();
    }
}

/// <summary>
/// An animated picture (an animated WebP: the bundled ffmpeg cannot decode those) played by
/// Skia's codec, cropped to fill and looped by its frame durations, under the cover-colour tint.
/// Frames are decoded forward on demand into one buffer; going back restarts from the frame the
/// asked one depends on. Draw from one thread at a time.
/// </summary>
internal sealed class AnimatedImageBackground : IPageBackground
{
    /// <summary>A frame with no duration shows this long, as browsers do.</summary>
    private const int DefaultFrameMs = 100;

    private readonly SKCodec _codec;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly int[] _startsMs;
    private readonly int _loopMs;
    private readonly SKBitmap _buffer;
    private readonly SKImageInfo _info;
    private readonly SKRect _source;
    private readonly SKRect _dest;
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKPaint _scrimPaint = new() { BlendMode = SKBlendMode.SrcOver };
    private readonly SKColor _tint;
    private volatile int _strength;
    private int _current = -1;

    private AnimatedImageBackground(SKCodec codec, int w, int h, SKColor tint, int strength)
    {
        _codec = codec;
        _frames = codec.FrameInfo;
        _startsMs = new int[_frames.Length];
        int at = 0;
        for (int i = 0; i < _frames.Length; i++)
        {
            _startsMs[i] = at;
            at += _frames[i].Duration > 0 ? _frames[i].Duration : DefaultFrameMs;
        }
        _loopMs = Math.Max(1, at);
        _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _buffer = new SKBitmap(_info);
        _source = BackgroundFit.CoverSource(_info.Width, _info.Height, Math.Max(1, w), Math.Max(1, h));
        _dest = new SKRect(0, 0, Math.Max(1, w), Math.Max(1, h));
        _tint = tint;
        _strength = strength;
    }

    /// <summary>The background for the animated picture at <paramref name="path"/>, or null when
    /// it is not one (a still or an unreadable file).</summary>
    internal static AnimatedImageBackground? TryCreate(string path, int w, int h, SKColor tint, int strength)
    {
        SKCodec? codec = null;
        try { codec = SKCodec.Create(path); } catch { }
        if (codec == null) return null;
        if (codec.FrameCount < 2)
        {
            codec.Dispose();
            return null;
        }
        var background = new AnimatedImageBackground(codec, w, h, tint, strength);
        if (background.Decode(0)) return background;
        background.Dispose();
        return null;
    }

    public int Strength { set => _strength = value; }

    /// <summary>The frame shown <paramref name="clipSeconds"/> into the loop.</summary>
    internal int FrameIndexAt(double clipSeconds)
    {
        double ms = double.IsFinite(clipSeconds) && clipSeconds > 0 ? clipSeconds * 1000 % _loopMs : 0;
        int index = Array.BinarySearch(_startsMs, (int)ms);
        return index >= 0 ? index : ~index - 1;
    }

    public void Draw(SKCanvas canvas, double clipSeconds)
    {
        Decode(FrameIndexAt(clipSeconds));
        using (var image = SKImage.FromBitmap(_buffer))
            canvas.DrawImage(image, _source, _dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), _paint);
        var alpha = PageBackgroundTint.OverlayAlpha(_strength);
        if (alpha == 0) return;
        _scrimPaint.Color = _tint.WithAlpha(alpha);
        canvas.DrawRect(_dest, _scrimPaint);
    }

    /// <summary>Brings the buffer to frame <paramref name="index"/>; false when that frame cannot
    /// be decoded.</summary>
    private bool Decode(int index)
    {
        if (index == _current) return true;
        int required = _frames[index].RequiredFrame;
        int prior;
        if (required < 0)
        {
            _buffer.Erase(SKColors.Transparent);
            prior = -1;
        }
        else if (_current >= required && _current < index
                 && _frames[_current].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
        {
            prior = _current;   // the usual step: one frame on from the one shown
        }
        else
        {
            if (!Decode(required)) return false;
            prior = required;
        }

        var result = _codec.GetPixels(_info, _buffer.GetPixels(), new SKCodecOptions(index, prior));
        _buffer.NotifyPixelsChanged();
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            _current = -1;
            return false;
        }
        _current = index;
        return true;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _codec.Dispose();
        _paint.Dispose();
        _scrimPaint.Dispose();
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
            // An animated WebP carries the same name as a still one: play its frames.
            if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                && AnimatedImageBackground.TryCreate(path, pixelWidth, pixelHeight, tint, strength) is { } animated)
                return animated;

            SKBitmap? image = null;
            try { image = DecodeStill(path, pixelWidth, pixelHeight); } catch { }
            if (image == null)
                return new WashBackground(art, pixelWidth, pixelHeight, strength);
            using (image)
                return new StillBackground(image, pixelWidth, pixelHeight, tint, strength);
        }

        var wash = new WashBackground(art, pixelWidth, pixelHeight, strength);
        if (string.IsNullOrEmpty(spec.FfmpegPath))
            return wash;
        return new ClipBackground(spec.FfmpegPath, path, pixelWidth, pixelHeight, fps, tint, strength, blocking, wash);
    }

    /// <summary>
    /// The picture at <paramref name="path"/> upright (its EXIF orientation applied, as photo
    /// apps show it), decoded at the smallest of the codec's scales that still fills
    /// <paramref name="width"/> × <paramref name="height"/>: a phone photo is far larger than
    /// the canvas. Null when it cannot be decoded.
    /// </summary>
    internal static SKBitmap? DecodeStill(string path, int width, int height)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        var origin = codec.EncodedOrigin;
        bool turns = Turns(origin);
        var full = codec.Info.Size;

        bool Fills(SKSizeI size) =>
            (turns ? size.Height : size.Width) >= width && (turns ? size.Width : size.Height) >= height;

        // The codec rounds a scale to one it supports (JPEG: eighths), possibly down: take the
        // first eighth whose size still fills.
        var decodeSize = full;
        for (int eighths = 1; eighths < 8; eighths++)
        {
            var scaled = codec.GetScaledDimensions(eighths / 8f);
            if (scaled.Width <= 0 || scaled.Height <= 0 || !Fills(scaled)) continue;
            if ((long)scaled.Width * scaled.Height < (long)full.Width * full.Height) decodeSize = scaled;
            break;
        }

        var decoded = DecodeAt(codec, decodeSize);
        if (decoded == null && decodeSize != full) decoded = DecodeAt(codec, full);
        if (decoded == null || origin == SKEncodedOrigin.TopLeft) return decoded;

        using (decoded)
            return Orient(decoded, origin);
    }

    private static bool Turns(SKEncodedOrigin origin) => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    private static SKBitmap? DecodeAt(SKCodec codec, SKSizeI size)
    {
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            bitmap.NotifyPixelsChanged();
            return bitmap;
        }
        bitmap.Dispose();
        return null;
    }

    /// <summary><paramref name="source"/> turned and flipped as its EXIF <paramref name="origin"/> says.</summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        // newX = ScaleX·x + SkewX·y + TransX, newY = SkewY·x + ScaleY·y + TransY.
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        bool turns = Turns(origin);
        var upright = new SKBitmap(new SKImageInfo(turns ? source.Height : source.Width,
            turns ? source.Width : source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(upright);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return upright;
    }
}
