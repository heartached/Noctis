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
    /// Extracts the dominant color from a bitmap using center-weighted pixel sampling.
    /// Downscales to ~50x50 for performance and skips near-black/near-white pixels.
    /// </summary>
    public static Color ExtractDominantColor(Bitmap? bitmap)
    {
        if (bitmap == null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
            return FallbackColor;

        const int sampleSize = 50;
        const int brightnessMin = 15;
        const int brightnessMax = 240;

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

            int width = fb.Size.Width;
            int height = fb.Size.Height;
            int rowBytes = fb.RowBytes;

            double totalR = 0, totalG = 0, totalB = 0;
            double totalWeight = 0;

            double cx = width / 2.0;
            double cy = height / 2.0;
            double maxDist = Math.Sqrt(cx * cx + cy * cy);

            // Copy pixel data to managed array for safe access
            int bufferSize = rowBytes * height;
            var pixels = new byte[bufferSize];
            Marshal.Copy(fb.Address, pixels, 0, bufferSize);

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
        const int brightnessMin = 15;
        const int brightnessMax = 240;

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
        catch
        {
            return (FallbackColor, Color.FromRgb(0x3A, 0x1C, 0x71));
        }
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
    /// Extracts the cover's ambient color — the bright/canvas hue rather than the dark subject.
    /// Uses 2-cluster palette extraction, picks the lighter cluster, then normalizes it to a
    /// vivid mid-lightness tone so the resulting page background reads as "the album's color"
    /// without being too dark (loses the cover's identity) or too light (white text becomes
    /// unreadable). Inspired by Apple Music's album page tinting.
    /// </summary>
    public static Color ExtractAmbientColor(Bitmap? bitmap)
    {
        if (bitmap == null) return FallbackColor;

        var (a, b) = ExtractColorPalette(bitmap);

        // ExtractColorPalette returns (dominant=darker, secondary=lighter).
        // We want the lighter cluster — that's the cover's canvas/ambient color.
        var picked = b;

        var (hue, sat, _) = RgbToHsl(picked.R, picked.G, picked.B);

        // Boost saturation so the tint is unmistakably the cover's color rather than a wash.
        // Floor at 0.45 so even fairly desaturated covers (greys, washed-out art) still read
        // as tinted; cap at 0.85 so we don't blow out already-vivid covers into neon.
        sat = Math.Clamp(sat * 1.25, 0.45, 0.85);

        // Normalize lightness to a mid value: bright enough to clearly be "the color",
        // dark enough that the existing white text on the album page stays readable
        // (L=0.45 against white is ~3.4:1, acceptable for large bold headings).
        const double targetLightness = 0.45;

        return HslToColor(hue, sat, targetLightness);
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
