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
    /// <summary>Default box radius in snapshot pixels: the Background Blur slider's default 10%
    /// of <see cref="MaxBlurRadius"/> (owner 10-08). Three passes of radius 1 ≈ a gaussian of
    /// σ ≈ 1.4 px, ~6 layout px once the quarter-size image is stretched back.</summary>
    public const double DefaultBlurRadius = 1;
    /// <summary>Strongest radius the Settings slider offers (the slider's 100%).</summary>
    public const double MaxBlurRadius = 10;
    public const int BlurPasses = 3;

    private static double _blurRadius = DefaultBlurRadius;

    /// <summary>
    /// Settings → Appearance → Background Blur (owner 10-08): the box radius every pop-up and
    /// sheet backdrop is blurred with, fractional so the slider moves the blur smoothly
    /// (<see cref="BoxBlurSmooth"/>), 0 = off (no snapshot at all, the dim alone). Set by the
    /// app from its settings; <see cref="BlurRadiusChanged"/> lets an open backdrop follow.
    /// </summary>
    public static double BlurRadius
    {
        get => _blurRadius;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, MaxBlurRadius) : DefaultBlurRadius;
            if (clamped == _blurRadius) return;
            _blurRadius = clamped;
            BlurRadiusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>False when Background Blur is off: callers show the dim alone.</summary>
    public static bool IsBlurEnabled => _blurRadius > 0;

    /// <summary>Raised on the UI thread when <see cref="BlurRadius"/> changes.</summary>
    public static event EventHandler? BlurRadiusChanged;

    /// <summary>The flattened, not yet blurred snapshot: kept by a backdrop that re-blurs it
    /// live while the Background Blur slider moves (no new snapshot per step).</summary>
    public sealed class Source
    {
        internal Source(byte[] pixels, int width, int height, PixelFormat format, AlphaFormat alpha)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Format = format;
            Alpha = alpha;
        }

        internal byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        internal PixelFormat Format { get; }
        internal AlphaFormat Alpha { get; }

        /// <summary>This source blurred at <paramref name="radius"/> into
        /// <paramref name="into"/> (same size as the source); thread-safe, the source is only read.</summary>
        public void BlurInto(byte[] into, double radius)
        {
            Buffer.BlockCopy(Pixels, 0, into, 0, Pixels.Length);
            BoxBlurSmooth(into, Width, Height, radius, BlurPasses);
        }

        /// <summary>A new bitmap of this source's size holding <paramref name="pixels"/>.</summary>
        public WriteableBitmap CreateBitmap(byte[] pixels)
        {
            var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), Format, Alpha);
            CopyInto(bitmap, pixels);
            return bitmap;
        }

        /// <summary>Overwrites <paramref name="bitmap"/> (made by <see cref="CreateBitmap"/>)
        /// with <paramref name="pixels"/>; the Image showing it needs an InvalidateVisual.</summary>
        public void CopyInto(WriteableBitmap bitmap, byte[] pixels)
        {
            using var fb = bitmap.Lock();
            for (var y = 0; y < Height; y++)
                Marshal.Copy(pixels, y * Width * 4, fb.Address + y * fb.RowBytes, Width * 4);
        }
    }

    /// <summary>
    /// Renders <paramref name="visual"/> (and everything inside it) once, flattens it over
    /// <paramref name="baseFrom"/>'s own background made opaque, and blurs it at
    /// <see cref="BlurRadius"/>. Returns null when the visual has no size or the backend can't
    /// snapshot / read back (logged under <paramref name="logTag"/>), and at once when
    /// Background Blur is off (<see cref="IsBlurEnabled"/>); the caller owns (and disposes) the bitmap.
    /// </summary>
    public static async Task<WriteableBitmap?> CaptureBlurredAsync(Visual visual, TopLevel baseFrom, string logTag)
    {
        if (!IsBlurEnabled) return null;
        var source = await CaptureSourceAsync(visual, baseFrom, logTag);
        if (source is null) return null;
        var radius = BlurRadius;
        var pixels = new byte[source.Pixels.Length];
        await Task.Run(() => source.BlurInto(pixels, radius));
        return source.CreateBitmap(pixels);
    }

    /// <summary>
    /// The snapshot half of <see cref="CaptureBlurredAsync"/>: renders and flattens
    /// <paramref name="visual"/> but leaves the blur to the caller. Null on the same failures
    /// (logged); it does not look at <see cref="IsBlurEnabled"/>.
    /// </summary>
    public static async Task<Source?> CaptureSourceAsync(Visual visual, TopLevel baseFrom, string logTag)
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
            await Task.Run(() => FlattenOpaque(pixels, baseColor, rgba, premultiplied));
            return new Source(pixels, w, h, format, alpha);
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

    /// <summary>
    /// <see cref="BoxBlur"/> at a fractional <paramref name="radius"/>, so the blur grows
    /// continuously as the Background Blur slider moves rather than in whole-radius steps:
    /// each box is the whole-radius window plus its two next samples at the fractional weight
    /// (radius 4.0 is BoxBlur's 4, radius 4.999 all but its 5). 0 or less leaves the pixels sharp.
    /// </summary>
    internal static void BoxBlurSmooth(byte[] pixels, int w, int h, double radius, int passes)
    {
        if (!(radius > 0) || w < 1 || h < 1) return;
        var whole = (int)Math.Floor(radius);
        var edge = (int)Math.Round((radius - whole) * 256);
        if (edge >= 256) { whole++; edge = 0; }
        var tmp = new byte[pixels.Length];
        for (var p = 0; p < passes; p++)
        {
            SmoothBoxPass(pixels, tmp, w, h, whole, edge, 4, w * 4);
            SmoothBoxPass(tmp, pixels, h, w, whole, edge, w * 4, 4);
        }
    }

    /// <summary>
    /// One <see cref="BoxPass"/> with the window's two outer neighbours weighted
    /// <paramref name="edgeWeight"/>/256 (fixed point, integer math; weight 0 rounds exactly as
    /// BoxPass does). Built to follow a slider drag: the four channels share one walk, edge
    /// clamping is a lookup table instead of per-sample Min/Max, and lines run in parallel
    /// (each reads only <paramref name="src"/> and writes only its own line).
    /// </summary>
    private static void SmoothBoxPass(byte[] src, byte[] dst, int count, int lines, int radius, int edgeWeight, int step, int lineStep)
    {
        var last = count - 1;
        var denominator = 256 * (2 * radius + 1) + 2 * edgeWeight;
        var half = denominator / 2;
        // offsets[p + radius + 1] = the clamped byte offset of position p, p in [-radius-1, count+radius].
        var offsets = new int[count + 2 * radius + 2];
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = Math.Clamp(i - radius - 1, 0, last) * step;

        Parallel.For(0, lines, line =>
        {
            var origin = line * lineStep;
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (var i = 1; i <= 2 * radius + 1; i++) // positions -radius..radius
            {
                var o = origin + offsets[i];
                s0 += src[o]; s1 += src[o + 1]; s2 += src[o + 2]; s3 += src[o + 3];
            }
            for (var x = 0; x < count; x++)
            {
                var below = origin + offsets[x];                  // x - radius - 1 (leaves next)
                var above = origin + offsets[x + 2 * radius + 2]; // x + radius + 1 (enters next)
                var d = origin + x * step;
                dst[d] = (byte)((s0 * 256 + (src[below] + src[above]) * edgeWeight + half) / denominator);
                dst[d + 1] = (byte)((s1 * 256 + (src[below + 1] + src[above + 1]) * edgeWeight + half) / denominator);
                dst[d + 2] = (byte)((s2 * 256 + (src[below + 2] + src[above + 2]) * edgeWeight + half) / denominator);
                dst[d + 3] = (byte)((s3 * 256 + (src[below + 3] + src[above + 3]) * edgeWeight + half) / denominator);
                var leaving = origin + offsets[x + 1];            // x - radius
                s0 += src[above] - src[leaving];
                s1 += src[above + 1] - src[leaving + 1];
                s2 += src[above + 2] - src[leaving + 2];
                s3 += src[above + 3] - src[leaving + 3];
            }
        });
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
