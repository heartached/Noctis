using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Noctis.Converters;

/// <summary>
/// Produces a small, heavily pre-blurred copy of an artwork bitmap for use as a
/// page backdrop, replacing a live fullscreen <c>BlurEffect</c>.
///
/// Why: an Effect is re-executed every rendered frame, and a radius-72 gaussian
/// over a fullscreen surface is the single most expensive draw on the lyrics
/// page — during the line-change scroll glide it ran every frame and dropped the
/// whole page to ~25fps under software rendering / weak GPUs (issue #11; two
/// stacked blurred layers made it worse). Blurring ONCE per artwork here makes
/// the backdrop a plain bitmap draw.
///
/// How: the art is downscaled so its long side is <see cref="WorkingSize"/> px
/// (discarding detail exactly like a heavy blur does), then convolved with a
/// separable gaussian whose sigma is chosen so the upscaled result matches the
/// old radius-72 fullscreen look. Results are cached per source bitmap, so a
/// track change costs one sub-millisecond 128px convolution.
///
/// As a multi-binding — (art, Drift on, blur %, saturation %) — it bakes the Drift
/// Blur / Saturation knobs (GitHub #111) into the same small bitmap, so they cost
/// nothing per frame either. Knobs at 100% (or Drift off) return the plain cached
/// result above, untouched; otherwise one tuned copy per source is kept and rebuilt
/// only when a knob moves, from cached 128px pixels (no re-read of the cover).
/// </summary>
public class PreBlurredArtworkConverter : IValueConverter, IMultiValueConverter
{
    private const int WorkingSize = 128;

    // radius 72 at a ~2232px-wide fullscreen ≈ radius 4.1 at 128px; BlurEffect
    // radius ≈ 2σ, and a touch extra keeps the backdrop at least as soft as the
    // live effect it replaces (the scrim sits on top either way).
    private const double Sigma = 2.4;

    private static readonly ConditionalWeakTable<Bitmap, Bitmap> Cache = new();

    /// <summary>Per source: its downscaled pixels and the last tuned bitmap made from them.</summary>
    private sealed class TunedArt
    {
        public required byte[] Small;
        public required int Width;
        public required int Height;
        public int Blur = -1;
        public int Saturation = -1;
        public Bitmap? Result;
    }

    private static readonly ConditionalWeakTable<Bitmap, TunedArt> TunedCache = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var art = values.Count > 0 ? values[0] : null;
        if (values.Count < 4 || values[1] is not true || values[2] is not int blur || values[3] is not int saturation
            || (blur == 100 && saturation == 100))
            return Convert(art, targetType, parameter, culture);
        if (art is not Bitmap src) return null;
        try
        {
            var tuned = TunedCache.GetValue(src, static s =>
            {
                var small = ReadDownscaled(s, out var w, out var h);
                return new TunedArt { Small = small, Width = w, Height = h };
            });
            if (tuned.Result is null || tuned.Blur != blur || tuned.Saturation != saturation)
            {
                tuned.Result = ToBitmap(Tune(tuned.Small, tuned.Width, tuned.Height, blur, saturation), tuned.Width, tuned.Height);
                tuned.Blur = blur;
                tuned.Saturation = saturation;
            }
            return tuned.Result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PreBlurredArtwork] {ex.Message}");
            return null;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Bitmap src) return null;
        try
        {
            return Cache.GetValue(src, CreateBlurred);
        }
        catch (Exception ex)
        {
            // A throwing IValueConverter surfaces as a binding failure rather than a
            // graceful fallback, and CopyPixels rejects some bitmap implementations
            // outright (see the note in ReadDownscaled). No backdrop beats a broken bind.
            System.Diagnostics.Debug.WriteLine($"[PreBlurredArtwork] {ex.Message}");
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Bitmap CreateBlurred(Bitmap source)
    {
        var pixels = ReadDownscaled(source, out var w, out var h);

        // Separable gaussian, clamp-to-edge. Channel-order agnostic: every byte
        // channel is blurred independently, so BGRA vs RGBA needs no branching.
        Blur(pixels, w, h, Sigma);
        return ToBitmap(pixels, w, h);
    }

    /// <summary>
    /// The Drift knobs applied to downscaled pixels (a new array; <paramref name="small"/> is
    /// kept): blur at <paramref name="blurPercent"/> of the stock sigma (0 = none), then
    /// saturation at <paramref name="saturationPercent"/> (0 = grey, 100 = unchanged). 100/100
    /// gives exactly what <see cref="CreateBlurred"/> draws.
    /// </summary>
    internal static byte[] Tune(byte[] small, int w, int h, int blurPercent, int saturationPercent)
    {
        var pixels = (byte[])small.Clone();
        if (blurPercent > 0) Blur(pixels, w, h, Sigma * (blurPercent / 100.0));
        if (saturationPercent != 100) Saturate(pixels, saturationPercent / 100.0);
        return pixels;
    }

    private static void Blur(byte[] pixels, int w, int h, double sigma)
    {
        var kernel = BuildKernel(sigma);
        var tmp = new byte[pixels.Length];
        ConvolveHorizontal(pixels, tmp, w, h, kernel);
        ConvolveVertical(tmp, pixels, w, h, kernel);
    }

    // Each pixel moves toward (s < 1) or away from (s > 1) its own luma — the same mix and
    // weights as Kawarp's shader (KawarpShader). Premultiplied BGRA: a channel is clamped
    // to its alpha so the result stays a valid premultiplied colour.
    private static void Saturate(byte[] pixels, double s)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var a = pixels[i + 3];
            var luma = 0.299 * r + 0.587 * g + 0.114 * b;
            pixels[i] = (byte)Math.Clamp(luma + (b - luma) * s + 0.5, 0, a);
            pixels[i + 1] = (byte)Math.Clamp(luma + (g - luma) * s + 0.5, 0, a);
            pixels[i + 2] = (byte)Math.Clamp(luma + (r - luma) * s + 0.5, 0, a);
        }
    }

    private static Bitmap ToBitmap(byte[] pixels, int w, int h)
    {
        var stride = w * 4;
        var result = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = result.Lock())
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        return result;
    }

    /// <summary>The source's pixels, area-averaged down to <see cref="WorkingSize"/> on the long side.</summary>
    private static byte[] ReadDownscaled(Bitmap source, out int w, out int h)
    {
        // Read the source pixels at native size: CreateScaledBitmap only accepts
        // some bitmap implementations ("Invalid source bitmap type" for e.g.
        // WriteableBitmap), so the downscale is done here instead.
        var sw = source.PixelSize.Width;
        var sh = source.PixelSize.Height;
        var srcStride = sw * 4;
        var srcPixels = new byte[srcStride * sh];
        var handle = GCHandle.Alloc(srcPixels, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(0, 0, sw, sh), handle.AddrOfPinnedObject(), srcPixels.Length, srcStride);
        }
        finally
        {
            handle.Free();
        }

        var scale = Math.Min(1.0, WorkingSize / (double)Math.Max(sw, sh));
        w = Math.Max(1, (int)Math.Round(sw * scale));
        h = Math.Max(1, (int)Math.Round(sh * scale));
        return Downscale(srcPixels, sw, sh, w, h);
    }

    // Area-average downscale: each destination pixel averages its whole source
    // cell, so no source detail is skipped (point sampling would alias and the
    // gaussian below is sized for the small image, not for hiding aliasing).
    private static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var dy = 0; dy < dh; dy++)
        {
            var y0 = dy * sh / dh;
            var y1 = Math.Max(y0 + 1, (dy + 1) * sh / dh);
            for (var dx = 0; dx < dw; dx++)
            {
                var x0 = dx * sw / dw;
                var x1 = Math.Max(x0 + 1, (dx + 1) * sw / dw);
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * sw * 4;
                    for (var x = x0; x < x1; x++)
                    {
                        var i = row + x * 4;
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        a += src[i + 3];
                    }
                }
                var n = (y1 - y0) * (x1 - x0);
                var di = (dy * dw + dx) * 4;
                dst[di] = (byte)(b / n);
                dst[di + 1] = (byte)(g / n);
                dst[di + 2] = (byte)(r / n);
                dst[di + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    private static double[] BuildKernel(double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1];
        double sum = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = v;
            sum += v;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        return kernel;
    }

    private static void ConvolveHorizontal(byte[] src, byte[] dst, int w, int h, double[] kernel)
    {
        var radius = kernel.Length / 2;
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, w - 1);
                    var idx = row + sx * 4;
                    var kv = kernel[k + radius];
                    b += src[idx] * kv;
                    g += src[idx + 1] * kv;
                    r += src[idx + 2] * kv;
                    a += src[idx + 3] * kv;
                }
                var di = row + x * 4;
                dst[di] = (byte)(b + 0.5);
                dst[di + 1] = (byte)(g + 0.5);
                dst[di + 2] = (byte)(r + 0.5);
                dst[di + 3] = (byte)(a + 0.5);
            }
        }
    }

    private static void ConvolveVertical(byte[] src, byte[] dst, int w, int h, double[] kernel)
    {
        var radius = kernel.Length / 2;
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(y + k, 0, h - 1);
                    var idx = (sy * w + x) * 4;
                    var kv = kernel[k + radius];
                    b += src[idx] * kv;
                    g += src[idx + 1] * kv;
                    r += src[idx + 2] * kv;
                    a += src[idx + 3] * kv;
                }
                var di = (y * w + x) * 4;
                dst[di] = (byte)(b + 0.5);
                dst[di + 1] = (byte)(g + 0.5);
                dst[di + 2] = (byte)(r + 0.5);
                dst[di + 3] = (byte)(a + 0.5);
            }
        }
    }
}
