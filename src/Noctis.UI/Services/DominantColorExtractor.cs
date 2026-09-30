using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Noctis.Services;

/// <summary>
/// Generates gradient brushes from base colors for the lyrics view background presets.
/// </summary>
public static class DominantColorExtractor
{
    /// <summary>
    /// Creates a dark, atmospheric diagonal gradient brush from a base color.
    /// </summary>
    public static LinearGradientBrush CreateGradientFromColor(Color color)
    {
        var (hue, sat, _) = RgbToHsl(color.R, color.G, color.B);
        if (sat < 0.2)
            sat = 0.2;

        var darkest = HslToColor(hue, sat * 0.5, 0.05);
        var dark = HslToColor(hue, sat, 0.13);
        var mid = HslToColor(hue, sat * 0.9, 0.23);
        var light = HslToColor(hue, sat * 0.85, 0.35);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(darkest, 0.0),
                new GradientStop(dark, 0.35),
                new GradientStop(mid, 0.7),
                new GradientStop(light, 1.0),
            }
        };
    }

    private static readonly Color FallbackColor = Color.FromRgb(0x1A, 0x1A, 0x2E);

    private const int MaxCacheSize = 500;
    private static readonly ConcurrentDictionary<string, Color> ColorCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (Color, Color)> PaletteCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Color> EdgeBackgroundCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Color> AverageColorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a cached average color for the given artwork path, or extracts and caches it.
    /// </summary>
    public static Color GetOrExtractAverageColor(string artworkPath, Bitmap bitmap)
    {
        if (AverageColorCache.TryGetValue(artworkPath, out var cached))
            return cached;

        var color = ExtractAverageColor(bitmap);

        if (AverageColorCache.Count >= MaxCacheSize)
            AverageColorCache.Clear();

        AverageColorCache.TryAdd(artworkPath, color);
        return color;
    }

    /// <summary>
    /// Returns a cached dominant color for the given artwork path, or extracts and caches it.
    /// </summary>
    public static Color GetOrExtractDominantColor(string artworkPath, Bitmap bitmap)
    {
        if (ColorCache.TryGetValue(artworkPath, out var cached))
            return cached;

        var color = ExtractDominantColor(bitmap);

        if (ColorCache.Count >= MaxCacheSize)
            ColorCache.Clear();

        ColorCache.TryAdd(artworkPath, color);
        return color;
    }

    /// <summary>
    /// Center-weighted average of the pixels that are neither near-black nor near-white.
    /// <paramref name="pixels"/> is BGRA, <paramref name="rowBytes"/> per row. Pure, so it
    /// runs on any thread.
    /// </summary>
    private static Color DominantFromPixels(byte[] pixels, int width, int height, int rowBytes)
    {
        const int brightnessMin = 15;
        const int brightnessMax = 240;

        double totalR = 0, totalG = 0, totalB = 0;
        double totalWeight = 0;

        double cx = width / 2.0;
        double cy = height / 2.0;
        double maxDist = Math.Sqrt(cx * cx + cy * cy);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                // Bgra8888 format: B, G, R, A
                int offset = rowStart + x * 4;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];

                // Perceived brightness (fast approximation)
                int brightness = (r * 299 + g * 587 + b * 114) / 1000;

                if (brightness < brightnessMin || brightness > brightnessMax)
                    continue;

                // Center-weighted: pixels closer to center count more
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double weight = 1.0 + (1.0 - dist / maxDist); // 1.0 to 2.0

                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                totalWeight += weight;
            }
        }

        if (totalWeight < 1.0)
            return FallbackColor;

        return Color.FromRgb(
            (byte)(totalR / totalWeight),
            (byte)(totalG / totalWeight),
            (byte)(totalB / totalWeight));
    }

    /// <summary>
    /// Two-means clustering of the usable pixels into a darker dominant and a secondary
    /// colour. <paramref name="pixels"/> is BGRA. Pure, so it runs on any thread.
    /// </summary>
    private static (Color Dominant, Color Secondary) PaletteFromPixels(byte[] pixels, int width, int height, int rowBytes)
    {
        const int brightnessMin = 15;
        const int brightnessMax = 240;

        // Collect valid pixels
        var validPixels = new List<(byte R, byte G, byte B, double Weight)>();
        double cx = width / 2.0, cy = height / 2.0;
        double maxDist = Math.Sqrt(cx * cx + cy * cy);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int offset = rowStart + x * 4;
                byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                int brightness = (r * 299 + g * 587 + b * 114) / 1000;
                if (brightness < brightnessMin || brightness > brightnessMax) continue;

                double dx = x - cx, dy = y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double weight = 1.0 + (1.0 - dist / maxDist);
                validPixels.Add((r, g, b, weight));
            }
        }

        if (validPixels.Count < 2)
            return (FallbackColor, Color.FromRgb(0x3A, 0x1C, 0x71));

        // Simple 2-means clustering (3 iterations)
        var rng = new Random(42);
        var idx1 = rng.Next(validPixels.Count);
        var idx2 = rng.Next(validPixels.Count);
        double c1R = validPixels[idx1].R, c1G = validPixels[idx1].G, c1B = validPixels[idx1].B;
        double c2R = validPixels[idx2].R, c2G = validPixels[idx2].G, c2B = validPixels[idx2].B;

        for (int iter = 0; iter < 3; iter++)
        {
            double s1R = 0, s1G = 0, s1B = 0, w1 = 0;
            double s2R = 0, s2G = 0, s2B = 0, w2 = 0;

            foreach (var (r, g, b, w) in validPixels)
            {
                double d1 = (r - c1R) * (r - c1R) + (g - c1G) * (g - c1G) + (b - c1B) * (b - c1B);
                double d2 = (r - c2R) * (r - c2R) + (g - c2G) * (g - c2G) + (b - c2B) * (b - c2B);
                if (d1 <= d2) { s1R += r * w; s1G += g * w; s1B += b * w; w1 += w; }
                else          { s2R += r * w; s2G += g * w; s2B += b * w; w2 += w; }
            }

            if (w1 > 0) { c1R = s1R / w1; c1G = s1G / w1; c1B = s1B / w1; }
            if (w2 > 0) { c2R = s2R / w2; c2G = s2G / w2; c2B = s2B / w2; }
        }

        var dominant = Color.FromRgb((byte)c1R, (byte)c1G, (byte)c1B);
        var secondary = Color.FromRgb((byte)c2R, (byte)c2G, (byte)c2B);

        // Ensure dominant is the darker one (better for backgrounds)
        double lum1 = 0.2126 * c1R + 0.7152 * c1G + 0.0722 * c1B;
        double lum2 = 0.2126 * c2R + 0.7152 * c2G + 0.0722 * c2B;
        if (lum2 < lum1)
            (dominant, secondary) = (secondary, dominant);

        return (dominant, secondary);
    }

    /// <summary>Longest side the file warm-up decodes at before the 50px downscale.</summary>
    private const int WarmDecodeDimension = 512;

    /// <summary>
    /// Whether the dominant, palette and average colours for <paramref name="artworkPath"/>
    /// are all cached, so the Get* calls on the UI thread do no rendering.
    /// </summary>
    public static bool HasCachedColors(string artworkPath)
        => ColorCache.ContainsKey(artworkPath)
           && PaletteCache.ContainsKey(artworkPath)
           && AverageColorCache.ContainsKey(artworkPath);

    /// <summary>
    /// Fills the dominant, palette and average caches for <paramref name="artworkPath"/> by
    /// decoding the FILE with SkiaSharp, so it is safe (and meant) to run on a worker
    /// thread. The Bitmap-based extractors render through a RenderTargetBitmap on the UI
    /// thread and stalled track-change animations; they also keyed the colours of
    /// whatever bitmap was on screen, which at a track change can still be the previous
    /// cover. Same algorithms over the same 50×50 sample. Returns false when the file
    /// can't be read (the caches are left alone).
    /// </summary>
    public static bool WarmFromFile(string artworkPath)
    {
        const int sampleSize = 50;
        try
        {
            using var raw = Helpers.SkiaArtworkDecoder.DecodeSubsampled(artworkPath, WarmDecodeDimension);
            if (raw == null) return false;
            using var small = raw.Resize(new SkiaSharp.SKImageInfo(sampleSize, sampleSize,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul), SkiaSharp.SKFilterQuality.High);
            if (small == null) return false;

            var pixels = small.Bytes;
            int rowBytes = small.RowBytes;

            // The UI path downscales to a single pixel; the mean of the 50×50 sample is
            // the same average without a GPU round-trip.
            long sumR = 0, sumG = 0, sumB = 0;
            for (int y = 0; y < sampleSize; y++)
            {
                int row = y * rowBytes;
                for (int x = 0; x < sampleSize; x++)
                {
                    int o = row + x * 4;
                    sumB += pixels[o];
                    sumG += pixels[o + 1];
                    sumR += pixels[o + 2];
                }
            }
            const int count = sampleSize * sampleSize;
            var average = Color.FromRgb((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));

            AddCapped(ColorCache, artworkPath, DominantFromPixels(pixels, sampleSize, sampleSize, rowBytes));
            AddCapped(PaletteCache, artworkPath, PaletteFromPixels(pixels, sampleSize, sampleSize, rowBytes));
            AddCapped(AverageColorCache, artworkPath, average);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddCapped<T>(ConcurrentDictionary<string, T> cache, string key, T value)
    {
        if (cache.Count >= MaxCacheSize) cache.Clear();
        cache[key] = value;
    }

    /// <summary>
    /// Extracts the dominant color from a bitmap using center-weighted pixel sampling.
    /// Downscales to ~50x50 for performance and skips near-black/near-white pixels.
    /// </summary>
    public static Color ExtractDominantColor(Bitmap? bitmap)
    {
        if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
            return FallbackColor;

        const int sampleSize = 50;

        try
        {
            var pixelSize = new PixelSize(sampleSize, sampleSize);

            // Render the source bitmap scaled down into a WriteableBitmap for pixel access.
            // Steps: render source -> RenderTargetBitmap -> save to stream -> decode into
            // a managed pixel buffer. This avoids unsafe code and works with all Avalonia backends.
            using var rtb = new RenderTargetBitmap(pixelSize);
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap,
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                    new Rect(0, 0, sampleSize, sampleSize));
            }

            // Save RTB to a memory stream, then reload as a WriteableBitmap to access pixels
            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            using var decoded = WriteableBitmap.Decode(ms);
            using var fb = decoded.Lock();

            // Copy pixel data to managed array for safe access
            int bufferSize = fb.RowBytes * fb.Size.Height;
            var pixels = new byte[bufferSize];
            Marshal.Copy(fb.Address, pixels, 0, bufferSize);
            return DominantFromPixels(pixels, fb.Size.Width, fb.Size.Height, fb.RowBytes);
        }
        catch
        {
            return FallbackColor;
        }
    }

    /// <summary>
    /// Extracts dominant and secondary colors from a bitmap using simplified k-means (2 clusters).
    /// Returns two visually distinct colors for richer gradient generation.
    /// </summary>
    public static (Color Dominant, Color Secondary) ExtractColorPalette(Bitmap? bitmap)
    {
        if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
            return (FallbackColor, Color.FromRgb(0x3A, 0x1C, 0x71));

        const int sampleSize = 50;

        try
        {
            var pixelSize = new PixelSize(sampleSize, sampleSize);
            using var rtb = new RenderTargetBitmap(pixelSize);
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap,
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                    new Rect(0, 0, sampleSize, sampleSize));
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            using var decoded = WriteableBitmap.Decode(ms);
            using var fb = decoded.Lock();

            int bufferSize = fb.RowBytes * fb.Size.Height;
            var pixels = new byte[bufferSize];
            Marshal.Copy(fb.Address, pixels, 0, bufferSize);
            return PaletteFromPixels(pixels, fb.Size.Width, fb.Size.Height, fb.RowBytes);
        }
        catch
        {
            return (FallbackColor, Color.FromRgb(0x3A, 0x1C, 0x71));
        }
    }

    /// <summary>
    /// Returns a cached edge-background color for the given artwork path, or extracts and caches it.
    /// </summary>
    public static Color GetOrExtractEdgeBackgroundColor(string artworkPath, Bitmap bitmap)
    {
        if (EdgeBackgroundCache.TryGetValue(artworkPath, out var cached))
            return cached;

        var color = ExtractEdgeBackgroundColor(bitmap);

        if (EdgeBackgroundCache.Count >= MaxCacheSize)
            EdgeBackgroundCache.Clear();

        EdgeBackgroundCache.TryAdd(artworkPath, color);
        return color;
    }

    /// <summary>
    /// Returns cached palette or extracts and caches it.
    /// </summary>
    public static (Color Dominant, Color Secondary) GetOrExtractPalette(string artworkPath, Bitmap bitmap)
    {
        if (PaletteCache.TryGetValue(artworkPath, out var cached))
            return cached;

        var palette = ExtractColorPalette(bitmap);

        if (PaletteCache.Count >= MaxCacheSize)
            PaletteCache.Clear();

        PaletteCache.TryAdd(artworkPath, palette);
        return palette;
    }

    /// <summary>
    /// Generates a pair of adaptive gradient brushes from a dominant color.
    /// Left: atmospheric gradient (via CreateGradientFromColor).
    /// Right: subdued/darker variant for lyrics readability.
    /// </summary>
    public static (LinearGradientBrush Left, LinearGradientBrush Right) GenerateAdaptiveBrushes(Color color)
    {
        var left = CreateGradientFromColor(color);
        var right = CreateSubduedGradient(color);
        return (left, right);
    }

    /// <summary>
    /// Generates adaptive brushes using both dominant and secondary colors for richer gradients.
    /// </summary>
    public static (LinearGradientBrush Left, LinearGradientBrush Right) GenerateAdaptiveBrushes(Color dominant, Color secondary)
    {
        var left = CreateGradientFromColor(dominant);
        var right = CreateDualColorSubduedGradient(dominant, secondary);
        return (left, right);
    }

    /// <summary>
    /// Generates a unified brush using two extracted colors for more accurate art representation.
    /// </summary>
    public static LinearGradientBrush GenerateUnifiedBrush(Color dominant, Color secondary)
    {
        var (h1, s1, _) = RgbToHsl(dominant.R, dominant.G, dominant.B);
        var (h2, s2, _) = RgbToHsl(secondary.R, secondary.G, secondary.B);
        s1 = Math.Max(s1, 0.30);
        s2 = Math.Max(s2, 0.30);

        var stop0 = HslToColor(h1,   s1 * 0.55, 0.06);
        var stop1 = HslToColor(h1,   s1 * 0.80, 0.14);
        var stop2 = HslToColor(h1,   s1 * 0.90, 0.22);
        var stop3 = HslToColor(h2,   s2 * 0.85, 0.20);
        var stop4 = HslToColor(h2,   s2 * 0.75, 0.28);
        var stop5 = HslToColor(h1,   s1 * 0.50, 0.10);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.85, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.15, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(stop0, 0.00),
                new GradientStop(stop1, 0.20),
                new GradientStop(stop2, 0.40),
                new GradientStop(stop3, 0.60),
                new GradientStop(stop4, 0.80),
                new GradientStop(stop5, 1.00),
            }
        };
    }

    /// <summary>
    /// Creates a subdued gradient using both dominant and secondary colors.
    /// </summary>
    private static LinearGradientBrush CreateDualColorSubduedGradient(Color dominant, Color secondary)
    {
        var (h1, s1, _) = RgbToHsl(dominant.R, dominant.G, dominant.B);
        var (h2, s2, _) = RgbToHsl(secondary.R, secondary.G, secondary.B);
        s1 = Math.Max(s1, 0.20);
        s2 = Math.Max(s2, 0.20);

        var darkest = HslToColor(h1, s1 * 0.45, 0.06);
        var dark    = HslToColor(h1, s1 * 0.55, 0.12);
        var mid     = HslToColor(h2, s2 * 0.50, 0.18);
        var accent  = HslToColor(h2, s2 * 0.45, 0.24);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(accent,  0.0),
                new GradientStop(mid,     0.30),
                new GradientStop(dark,    0.65),
                new GradientStop(darkest, 1.0),
            }
        };
    }

    /// <summary>
    /// Generates a single unified gradient spanning the full lyrics view.
    /// Uses 5 evenly-spaced stops so color transitions are imperceptible.
    /// Slight diagonal angle prevents a perfectly horizontal color band.
    /// Left side is a rich deep shade, right side brighter — no black zones.
    /// </summary>
    public static LinearGradientBrush GenerateUnifiedBrush(Color color)
    {
        var (hue, sat, _) = RgbToHsl(color.R, color.G, color.B);
        sat = Math.Max(sat, 0.35);

        // Shift hue for accent color variety (prevents flat monochrome backgrounds)
        var accentHue = (hue + 0.11) % 1.0;      // ~40° clockwise
        var warmHue = (hue - 0.06 + 1.0) % 1.0;  // ~22° counter-clockwise

        var stop0 = HslToColor(warmHue,   sat * 0.55, 0.06);  // deep warm corner
        var stop1 = HslToColor(hue,       sat * 0.80, 0.14);  // dominant dark
        var stop2 = HslToColor(hue,       sat * 0.90, 0.22);  // dominant rich
        var stop3 = HslToColor(accentHue, sat * 0.85, 0.20);  // accent blend
        var stop4 = HslToColor(accentHue, sat * 0.75, 0.28);  // accent highlight
        var stop5 = HslToColor(warmHue,   sat * 0.65, 0.12);  // warm dark edge

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.85, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.15, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(stop0, 0.00),
                new GradientStop(stop1, 0.20),
                new GradientStop(stop2, 0.40),
                new GradientStop(stop3, 0.60),
                new GradientStop(stop4, 0.80),
                new GradientStop(stop5, 1.00),
            }
        };
    }

    /// <summary>
    /// Creates a subdued but still colorful gradient for the lyrics panel.
    /// Balanced for text readability while keeping album colors visible.
    /// </summary>
    private static LinearGradientBrush CreateSubduedGradient(Color color)
    {
        var (hue, sat, _) = RgbToHsl(color.R, color.G, color.B);
        if (sat < 0.20)
            sat = 0.20;

        var darkest = HslToColor(hue, sat * 0.45, 0.06);
        var dark    = HslToColor(hue, sat * 0.55, 0.12);
        var mid     = HslToColor(hue, sat * 0.50, 0.18);
        var accent  = HslToColor(hue, sat * 0.45, 0.24);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(accent,  0.0),
                new GradientStop(mid,     0.30),
                new GradientStop(dark,    0.65),
                new GradientStop(darkest, 1.0),
            }
        };
    }

    /// <summary>
    /// Generates a diagonal gradient brush from two colors for gradient presets.
    /// </summary>
    public static LinearGradientBrush GenerateGradientBrush(Color color1, Color color2)
    {
        var (h1, s1, _) = RgbToHsl(color1.R, color1.G, color1.B);
        var (h2, s2, _) = RgbToHsl(color2.R, color2.G, color2.B);
        s1 = Math.Max(s1, 0.30);
        s2 = Math.Max(s2, 0.30);

        var stop0 = HslToColor(h1, s1 * 0.60, 0.08);
        var stop1 = HslToColor(h1, s1 * 0.85, 0.18);
        var stop2 = HslToColor((h1 + h2) / 2.0, (s1 + s2) / 2.0 * 0.80, 0.20);
        var stop3 = HslToColor(h2, s2 * 0.85, 0.18);
        var stop4 = HslToColor(h2, s2 * 0.60, 0.08);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.85, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.15, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(stop0, 0.00),
                new GradientStop(stop1, 0.25),
                new GradientStop(stop2, 0.50),
                new GradientStop(stop3, 0.75),
                new GradientStop(stop4, 1.00),
            }
        };
    }

    /// <summary>
    /// Apple-Music-style background extraction from a downscaled cover's edge band; see
    /// <see cref="PickEdgeBackgroundColor"/> for how the colour is chosen.
    /// </summary>
    public static Color ExtractEdgeBackgroundColor(Bitmap? bitmap)
    {
        if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
            return FallbackColor;

        const int sampleSize = EdgeSampleSize;

        try
        {
            var pixelSize = new PixelSize(sampleSize, sampleSize);
            using var rtb = new RenderTargetBitmap(pixelSize);
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap,
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                    new Rect(0, 0, sampleSize, sampleSize));
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            using var decoded = WriteableBitmap.Decode(ms);
            using var fb = decoded.Lock();

            int width = fb.Size.Width;
            int height = fb.Size.Height;
            int rowBytes = fb.RowBytes;
            int bufferSize = rowBytes * height;
            var pixels = new byte[bufferSize];
            Marshal.Copy(fb.Address, pixels, 0, bufferSize);

            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int src = y * rowBytes + x * 4, dst = (y * width + x) * 3;
                rgb[dst] = pixels[src + 2];
                rgb[dst + 1] = pixels[src + 1];
                rgb[dst + 2] = pixels[src];
            }
            return PickEdgeBackgroundColor(rgb, width, height) ?? FallbackColor;
        }
        catch
        {
            return FallbackColor;
        }
    }

    /// <summary>Edge extraction downscale (both paths).</summary>
    private const int EdgeSampleSize = 64;

    /// <summary>OKLab distance under which two colour bins count as the same background.
    /// ~0.06 merges a gradient's neighbouring shades and JPEG noise, but keeps e.g. a pink
    /// and a red border apart.</summary>
    private const double EdgeClusterMergeDistance = 0.06;

    /// <summary>Chroma cap (OKLCH) for the page fill: neon covers are toned down to a
    /// deeper version of the same hue instead of a full-page highlighter.</summary>
    internal const double EdgeMaxChroma = 0.15;

    private sealed class EdgeCluster
    {
        public double W, L, A, B;
        public double MeanL => L / W;
        public double MeanA => A / W;
        public double MeanB => B / W;

        /// <summary>Near-black or near-white with (almost) no colour.</summary>
        public bool IsNeutralExtreme
        {
            get
            {
                var chroma = Math.Sqrt(MeanA * MeanA + MeanB * MeanB);
                return chroma < 0.035 && (MeanL < 0.18 || MeanL > 0.95);
            }
        }
    }

    /// <summary>
    /// The album-page background colour from a small downscaled cover (row-major RGB, 3
    /// bytes per pixel). Replaces the old "1-px outer ring, 4-bit RGB histogram" pick,
    /// which was often wrong (user report 09-22):
    /// <list type="bullet">
    /// <item>a band (outer ~10%) is sampled instead of one pixel ring, weighted toward the
    /// edge, so thin frame lines and resize fringes no longer decide the colour;</item>
    /// <item>pixels are grouped in OKLab (perceptual) and neighbouring bins are merged, so a
    /// gradient or noisy edge votes as ONE colour instead of splitting across dozens of
    /// RGB buckets and losing to a small flat patch;</item>
    /// <item>near-black / near-white only lose to a real colour when they hold less than
    /// ~45% of the band — the old code skipped them always, so a black-bordered cover was
    /// tinted by whatever stray colour touched its edge;</item>
    /// <item>chroma is capped (<see cref="EdgeMaxChroma"/>) so neon covers don't paint the
    /// whole page in highlighter; hue and lightness are kept.</item>
    /// </list>
    /// Null when there are no pixels.
    /// </summary>
    internal static Color? PickEdgeBackgroundColor(byte[] rgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3) return null;

        var depth = Math.Max(2, Math.Min(width, height) / 10);
        var bins = new Dictionary<int, EdgeCluster>();
        double total = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var dist = Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
            if (dist >= depth) continue;
            var weight = 1.0 - 0.5 * dist / depth;
            var i = (y * width + x) * 3;
            var (l, a, b) = ToOkLab(rgb[i], rgb[i + 1], rgb[i + 2]);
            var key = ((int)(l * 32) << 16) | ((int)((a + 0.5) * 40) << 8) | (int)((b + 0.5) * 40);
            if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new EdgeCluster();
            bin.W += weight; bin.L += l * weight; bin.A += a * weight; bin.B += b * weight;
            total += weight;
        }
        if (bins.Count == 0 || total <= 0) return null;

        // Greedy merge, heaviest bins first: each bin joins the first cluster whose mean is
        // within the merge distance, else seeds a new one.
        var clusters = new List<EdgeCluster>();
        foreach (var bin in bins.Values.OrderByDescending(v => v.W))
        {
            EdgeCluster? home = null;
            foreach (var c in clusters)
            {
                double dl = c.MeanL - bin.MeanL, da = c.MeanA - bin.MeanA, db = c.MeanB - bin.MeanB;
                if (dl * dl + da * da + db * db < EdgeClusterMergeDistance * EdgeClusterMergeDistance)
                {
                    home = c;
                    break;
                }
            }
            if (home == null) clusters.Add(home = new EdgeCluster());
            home.W += bin.W; home.L += bin.L; home.A += bin.A; home.B += bin.B;
        }
        clusters.Sort((p, q) => q.W.CompareTo(p.W));

        var chosen = clusters[0];
        if (chosen.IsNeutralExtreme && chosen.W / total < 0.45)
        {
            var coloured = clusters.FirstOrDefault(c => !c.IsNeutralExtreme && c.W / total >= 0.12);
            if (coloured != null) chosen = coloured;
        }

        double okL = chosen.MeanL, okA = chosen.MeanA, okB = chosen.MeanB;
        var chroma = Math.Sqrt(okA * okA + okB * okB);
        if (chroma > EdgeMaxChroma)
        {
            var k = EdgeMaxChroma / chroma;
            okA *= k;
            okB *= k;
        }
        return FromOkLab(okL, okA, okB);
    }

    private static double SrgbToLinear(byte c)
    {
        var v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte LinearToSrgb(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
    }

    /// <summary>sRGB → OKLab (Björn Ottosson's reference matrices).</summary>
    internal static (double L, double A, double B) ToOkLab(byte r, byte g, byte b)
    {
        double lr = SrgbToLinear(r), lg = SrgbToLinear(g), lb = SrgbToLinear(b);
        var l = Math.Cbrt(0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb);
        var m = Math.Cbrt(0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb);
        var s = Math.Cbrt(0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>OKLab → sRGB, clamped into gamut.</summary>
    internal static Color FromOkLab(double okL, double okA, double okB)
    {
        var l = Math.Pow(okL + 0.3963377774 * okA + 0.2158037573 * okB, 3);
        var m = Math.Pow(okL - 0.1055613458 * okA - 0.0638541728 * okB, 3);
        var s = Math.Pow(okL - 0.0894841775 * okA - 1.2914855480 * okB, 3);
        return Color.FromRgb(
            LinearToSrgb(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            LinearToSrgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            LinearToSrgb(-0.0041960863 * l - 0.7034186948 * m + 1.7076147010 * s));
    }

    /// <summary>
    /// Creates a flat solid background brush for album detail pages, tinted by the cover's
    /// ambient color. Apple-Music-inspired: the page reads as the album's color rather than
    /// a generic dark theme.
    /// </summary>
    public static IBrush CreateAlbumDetailGradient(Color color)
    {
        return new SolidColorBrush(color);
    }

    /// <summary>sRGB relative luminance (WCAG), 0 = black … 1 = white. Used to decide
    /// whether page text over a tint must flip dark.</summary>
    public static double GetRelativeLuminance(Color color)
    {
        static double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(color.R) + 0.7152 * Lin(color.G) + 0.0722 * Lin(color.B);
    }

    private static readonly ConcurrentDictionary<string, Color> EdgeFileCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Path-based twin of <see cref="ExtractEdgeBackgroundColor(Bitmap?)"/> that decodes
    /// with SkiaSharp instead of an Avalonia RenderTargetBitmap, so it is safe to run
    /// on a worker thread (the RTB path must run on the UI thread and visibly stalled
    /// page opens). Same algorithm (<see cref="PickEdgeBackgroundColor"/> over a
    /// 64×64 downscale). Cached per path; null when the file can't be read.
    /// </summary>
    /// <summary>Largest decode the edge extractor will allocate (a codec that cannot
    /// subsample decodes at native size); beyond this the page simply stays untinted.</summary>
    private const int MaxEdgeDecodeDimension = 8192;

    public static Color? ExtractEdgeBackgroundColorFromFile(string? artworkPath)
    {
        if (string.IsNullOrEmpty(artworkPath)) return null;
        if (EdgeFileCache.TryGetValue(artworkPath, out var cached)) return cached;

        const int sampleSize = EdgeSampleSize;
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(artworkPath);
            if (codec == null) return null;
            var info = codec.Info;
            // Ask the codec for a subsampled decode so a huge cover never allocates at
            // native size (same guard as ShareCardRenderer.LoadArtwork). The decode only
            // succeeds at a size the codec itself reports for that scale: JPEG rounds its
            // 1/2..1/8 steps, and PNG (what most library covers are, whatever their
            // extension says) cannot subsample at all and reports its native size. Asking
            // for width/sample directly returned null for both, so the page never tinted.
            var longest = Math.Max(info.Width, info.Height);
            var sample = 1;
            while (longest / sample > 512) sample *= 2;
            var scaled = sample > 1 ? codec.GetScaledDimensions(1f / sample) : info.Size;
            if (Math.Max(scaled.Width, scaled.Height) > MaxEdgeDecodeDimension) return null;
            using var raw = SkiaSharp.SKBitmap.Decode(codec, new SkiaSharp.SKImageInfo(
                Math.Max(1, scaled.Width), Math.Max(1, scaled.Height),
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            if (raw == null) return null;
            using var small = raw.Resize(new SkiaSharp.SKImageInfo(sampleSize, sampleSize,
                SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul), SkiaSharp.SKFilterQuality.High);
            if (small == null) return null;

            var pixels = small.Pixels;
            var rgb = new byte[sampleSize * sampleSize * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgb[i * 3] = pixels[i].Red;
                rgb[i * 3 + 1] = pixels[i].Green;
                rgb[i * 3 + 2] = pixels[i].Blue;
            }
            if (PickEdgeBackgroundColor(rgb, sampleSize, sampleSize) is not { } color) return null;

            if (EdgeFileCache.Count >= MaxCacheSize) EdgeFileCache.Clear();
            EdgeFileCache.TryAdd(artworkPath, color);
            return color;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the cached edge colour for a path (artwork replaced in place).</summary>
    public static void InvalidateEdgeColor(string? artworkPath)
    {
        if (!string.IsNullOrEmpty(artworkPath)) EdgeFileCache.TryRemove(artworkPath, out _);
    }

    /// <summary>
    /// Returns the average color of the bitmap by downscaling it to a single pixel.
    /// Useful for predicting the apparent tint of a heavily-blurred cover image, where
    /// the blurred surface reads as the bitmap's average tone rather than its dominant
    /// accent. Cheap (a single GPU downscale + 1-byte read).
    /// </summary>
    public static Color ExtractAverageColor(Bitmap? bitmap)
    {
        if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
            return FallbackColor;

        try
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(1, 1));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(bitmap,
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                    new Rect(0, 0, 1, 1));
            }

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            using var decoded = WriteableBitmap.Decode(ms);
            using var fb = decoded.Lock();

            var pixel = new byte[4];
            Marshal.Copy(fb.Address, pixel, 0, 4);
            // Avalonia uses BGRA layout.
            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }
        catch
        {
            return FallbackColor;
        }
    }

    private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;

        if (Math.Abs(max - min) < 0.001)
            return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == rd)
            h = ((gd - bd) / d + (gd < bd ? 6 : 0)) / 6.0;
        else if (max == gd)
            h = ((bd - rd) / d + 2) / 6.0;
        else
            h = ((rd - gd) / d + 4) / 6.0;

        return (h, s, l);
    }

    private static Color HslToColor(double h, double s, double l)
    {
        if (s < 0.001)
        {
            var gray = (byte)(l * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return Color.FromRgb(
            (byte)(HueToRgb(p, q, h + 1.0 / 3.0) * 255),
            (byte)(HueToRgb(p, q, h) * 255),
            (byte)(HueToRgb(p, q, h - 1.0 / 3.0) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

}
