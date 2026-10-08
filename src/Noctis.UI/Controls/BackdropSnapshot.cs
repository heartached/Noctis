using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// The blurred "rounded pill" backdrop (owner 10-08): a quarter-size copy of part of the app,
/// flattened over an opaque base and box-blurred once, off the UI thread. Shared by
/// <see cref="PillDialogHost"/> (a pop-up window over its owner) and
/// <see cref="BlurredBackdrop"/> (an in-window layer, the Settings sheet).
///
/// Why a snapshot rather than an OS or per-frame blur: an AcrylicBlur/Blur hint on a
/// borderless transparent window painted the whole owner black on Win32 (09-07), a frosted
/// window region left the Mini Player's edges jagged, and a GlassPanel re-blurs every frame
/// it repaints. The visual is rendered once by its own compositor (render thread, its own
/// GPU context — a UI-thread RenderTargetBitmap would run the Liquid Glass backdrop ops off
/// their thread and against a foreign surface), at a quarter size, then blurred once. After
/// that it is a plain bitmap draw: nothing re-blurs per frame during the fades.
/// </summary>
public static class BackdropSnapshot
{
    /// <summary>The visual is rendered at a quarter of its layout size: the blur throws that
    /// detail away anyway, and it keeps the snapshot and the blur pass a few ms.</summary>
    public const double SnapshotScaling = 0.25;
    /// <summary>Box radius in snapshot pixels; three passes ≈ a gaussian of σ ≈ 4.5 px,
    /// i.e. ~18 layout px once the quarter-size image is stretched back.</summary>
    public const int BlurRadius = 4;
    public const int BlurPasses = 3;

    /// <summary>
    /// Renders <paramref name="visual"/> (and everything inside it) once, flattens it over
    /// <paramref name="baseFrom"/>'s own background made opaque, and blurs it. Returns null
    /// when the visual has no size or the backend can't snapshot / read back (logged under
    /// <paramref name="logTag"/>); the caller owns (and disposes) the bitmap.
    /// </summary>
    public static async Task<WriteableBitmap?> CaptureBlurredAsync(Visual visual, TopLevel baseFrom, string logTag)
    {
        try
        {
            var compositionVisual = ElementComposition.GetElementVisual(visual);
            var size = visual is TopLevel top ? top.ClientSize : visual.Bounds.Size;
            if (compositionVisual is null || size.Width < 1 || size.Height < 1) return null;

            byte[] pixels;
            int w, h;
            PixelFormat format;
            AlphaFormat alpha;
            using (var snapshot = await compositionVisual.Compositor.CreateCompositionVisualSnapshot(compositionVisual, SnapshotScaling))
            using (var png = new MemoryStream())
            {
                // The snapshot is a render-target bitmap, which can't CopyPixels ("not
                // supported for this bitmap type"); a PNG round-trip of the quarter-size
                // image (~1 ms) gives a decoded bitmap that can, on every backend. Same
                // route DominantColorExtractor takes for its RenderTargetBitmaps.
                snapshot.Save(png, PngBitmapEncoderOptions.Default);
                png.Position = 0;
                using var raw = new Bitmap(png);
                w = raw.PixelSize.Width;
                h = raw.PixelSize.Height;
                if (w < 1 || h < 1) return null;
                format = raw.Format ?? PixelFormat.Bgra8888;
                alpha = raw.AlphaFormat ?? AlphaFormat.Premul;
                pixels = new byte[w * h * 4];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { raw.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4); }
                finally { handle.Free(); }
            }

            // Owner 10-08: white specks on the pill backdrop. Any snapshot pixel below alpha
            // 255 (a translucent surface — Liquid Glass paints the window root at 35% — or
            // whatever a GPU pass leaves half-covered) stays see-through after the blur, and
            // the sharp app shows through it: crisp white icon and text fragments over the
            // blur. Laid over the window's own opaque background first, the blurred copy is
            // opaque everywhere (a blur of alpha-255 pixels stays 255).
            var baseColor = OpaqueBaseColor(baseFrom);
            var rgba = format == PixelFormat.Rgba8888;
            var premultiplied = alpha != AlphaFormat.Unpremul;
            await Task.Run(() =>
            {
                FlattenOpaque(pixels, baseColor, rgba, premultiplied);
                BoxBlur(pixels, w, h, BlurRadius, BlurPasses);
            });

            var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), format, alpha);
            using (var fb = bitmap.Lock())
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            // Some backends can't snapshot or read back; the dim alone is a fine backdrop.
            DebugLogger.Warn(DebugLogger.Category.UI, logTag, ex.Message);
            return null;
        }
    }

    /// <summary>The colour the snapshot is flattened onto: the window's own background made
    /// opaque (with Liquid Glass on it is the theme surface at 35%, so its colour is the
    /// surface the glass tints toward), else black/white by theme.</summary>
    internal static Color OpaqueBaseColor(TopLevel top)
    {
        switch (top.Background)
        {
            case ISolidColorBrush solid:
                return Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
            case IGradientBrush { GradientStops.Count: > 0 } gradient:
                // Some themes paint the window root with a gradient (Smoke): its average.
                int r = 0, g = 0, b = 0, n = gradient.GradientStops.Count;
                foreach (var stop in gradient.GradientStops) { r += stop.Color.R; g += stop.Color.G; b += stop.Color.B; }
                return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            default:
                return top.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Colors.White : Colors.Black;
        }
    }

    /// <summary>
    /// Lays every pixel over the opaque <paramref name="baseColor"/>, in place, so the result
    /// has alpha 255 everywhere and no part of the backdrop is see-through (owner 10-08:
    /// white specks on the pill backdrop — the dialog is a transparent window over the real
    /// owner, so a snapshot pixel below 255 lets the sharp owner show through the blur).
    /// Fully opaque pixels are untouched. <paramref name="rgba"/> picks the channel order
    /// (else BGRA); <paramref name="premultiplied"/> says how the colour bytes are stored.
    /// </summary>
    internal static void FlattenOpaque(byte[] pixels, Color baseColor, bool rgba, bool premultiplied)
    {
        int c0 = rgba ? baseColor.R : baseColor.B, c1 = baseColor.G, c2 = rgba ? baseColor.B : baseColor.R;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            var rest = 255 - a;
            if (premultiplied)
            {
                // src-over onto an opaque base: c + base·(1 − a). Clamped, as a bad premultiplied
                // pixel (colour above its alpha) would otherwise wrap.
                pixels[i] = (byte)Math.Min(255, pixels[i] + (c0 * rest + 127) / 255);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (c1 * rest + 127) / 255);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (c2 * rest + 127) / 255);
            }
            else
            {
                pixels[i] = (byte)((pixels[i] * a + c0 * rest + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * a + c1 * rest + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * a + c2 * rest + 127) / 255);
            }
            pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// <paramref name="passes"/> box blurs of radius <paramref name="radius"/> (together ≈ a
    /// gaussian), clamp-to-edge, in place. Every byte channel is blurred independently, so it
    /// is channel-order agnostic, and premultiplied pixels stay valid.
    /// </summary>
    internal static void BoxBlur(byte[] pixels, int w, int h, int radius, int passes)
    {
        if (radius < 1 || w < 1 || h < 1) return;
        var tmp = new byte[pixels.Length];
        for (var p = 0; p < passes; p++)
        {
            BoxPass(pixels, tmp, w, h, radius, 4, w * 4);   // horizontal: step one pixel
            BoxPass(tmp, pixels, h, w, radius, w * 4, 4);   // vertical: step one row
        }
    }

    /// <summary>One running-sum box pass along <paramref name="count"/> pixels spaced
    /// <paramref name="step"/> bytes apart, for each of <paramref name="lines"/> lines spaced
    /// <paramref name="lineStep"/> bytes apart.</summary>
    private static void BoxPass(byte[] src, byte[] dst, int count, int lines, int radius, int step, int lineStep)
    {
        var window = 2 * radius + 1;
        var last = count - 1;
        for (var line = 0; line < lines; line++)
        {
            var origin = line * lineStep;
            for (var c = 0; c < 4; c++)
            {
                var basis = origin + c;
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += src[basis + Math.Clamp(i, 0, last) * step];
                for (var x = 0; x < count; x++)
                {
                    dst[basis + x * step] = (byte)((sum + window / 2) / window);
                    sum += src[basis + Math.Min(x + radius + 1, last) * step]
                         - src[basis + Math.Max(x - radius, 0) * step];
                }
            }
        }
    }
}
