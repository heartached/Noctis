using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Noctis.Controls;

/// <summary>
/// A sidebar / Settings-rail PNG icon: a square filled with <see cref="Foreground"/> through
/// the PNG's alpha. Replaces a Border with an ImageBrush opacity mask, which let the GPU
/// shrink the 48 px PNG to whatever size the icon landed at. At 100% that lands on whole
/// pixels; at another Interface Size (GitHub #145; Discord, Mistery 11-03: blurry, misshapen
/// icons at 90% / 110%) the layout transform puts the box between pixels (y = 157.3 at 110%)
/// and the sampler at a new ratio, so strokes came out uneven, blocky or smeared.
/// Under that transform this draws the icon as one bitmap, resampled once on the CPU to the
/// exact device-pixel size (an area average, so a stroke keeps one weight), tinted, and placed
/// 1:1 on whole device pixels. At 100% (no Interface Size transform above it) it draws the
/// old way: pixel-identical to before.
/// </summary>
public sealed class MaskIcon : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<MaskIcon, Bitmap?>(nameof(Source));

    /// <summary>Fill colour; inherits the surrounding text colour like PathIcon does.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MaskIcon>();

    // Resampled masks per source and pixel size (a handful: one per size the icons show at).
    private static readonly ConditionalWeakTable<Bitmap, Dictionary<int, Bitmap>> Scaled = new();
    // Interface Size path: area-averaged alpha per pixel size, and tinted bitmaps per size + colour.
    private static readonly ConditionalWeakTable<Bitmap, Dictionary<int, byte[]>> AreaAlpha = new();
    private static readonly ConditionalWeakTable<Bitmap, Dictionary<long, Bitmap>> Tinted = new();

    // The device placement the last Render used; a layout pass that moves the icon onto
    // another sub-pixel offset or scale redraws it (Render output is otherwise kept as is).
    private (double Scale, double FracX, double FracY) _drawnAt = (double.NaN, 0, 0);

    static MaskIcon()
    {
        AffectsRender<MaskIcon>(SourceProperty, ForegroundProperty);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible && Placement() is { } now && now != _drawnAt)
            InvalidateVisual();
    }

    /// <summary>Device pixels per unit here, and where (0,0) falls between device pixels.</summary>
    private (double Scale, double FracX, double FracY)? Placement()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.TransformToVisual(top) is not { } m)
            return null;
        var render = top.RenderScaling;
        var origin = m.Transform(default) * render;
        return (Math.Abs(m.M11) * render, origin.X - Math.Floor(origin.X), origin.Y - Math.Floor(origin.Y));
    }

    public override void Render(DrawingContext context)
    {
        var source = Source;
        var brush = Foreground;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (source is null || brush is null || side <= 0)
            return;
        var placement = Placement() ?? (1.0, 0, 0);
        _drawnAt = placement;
        var (scale, fracX, fracY) = placement;
        if (scale <= 0 || !double.IsFinite(scale))
            return;

        // Centred like Stretch="Uniform", then pulled back onto whole device pixels.
        var px = Math.Max(1, (int)Math.Round(side * scale));
        var x = (Bounds.Width - side) / 2;
        var y = (Bounds.Height - side) / 2;
        x += (Math.Round(fracX + x * scale) - (fracX + x * scale)) / scale;
        y += (Math.Round(fracY + y * scale) - (fracY + y * scale)) / scale;
        var rect = new Rect(x, y, px / scale, px / scale);

        // Under an Interface Size transform an opacity mask is drawn into a layer at the
        // untransformed size and then stretched (probe at 110%: 20 px of rows and columns spread
        // over 22, two of them doubled), so there the icon is one tinted bitmap drawn 1:1.
        if (TopLevel.GetTopLevel(this) is { } top && Math.Abs(scale - top.RenderScaling) > 1e-6
            && brush is ISolidColorBrush solid && TintedAt(source, px, solid) is { } tinted)
        {
            context.DrawImage(tinted, new Rect(0, 0, px, px), rect);
            return;
        }
        var mask = new ImageBrush(MaskAt(source, px)) { Stretch = Stretch.Fill };
        using (context.PushOpacityMask(mask, rect))
            context.FillRectangle(brush, rect);
    }

    private static Bitmap MaskAt(Bitmap source, int px)
    {
        if (source.PixelSize.Width == px && source.PixelSize.Height == px)
            return source;
        var sizes = Scaled.GetOrCreateValue(source);
        if (!sizes.TryGetValue(px, out var bmp))
        {
            try { bmp = source.CreateScaledBitmap(new PixelSize(px, px), BitmapInterpolationMode.HighQuality); }
            catch { bmp = source; }
            sizes[px] = bmp;
        }
        return bmp;
    }

    private static Bitmap? TintedAt(Bitmap source, int px, ISolidColorBrush brush)
    {
        var c = brush.Color;
        var a = (byte)Math.Round(c.A * Math.Clamp(brush.Opacity, 0, 1));
        var key = ((long)px << 32) | ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        var tints = Tinted.GetOrCreateValue(source);
        if (tints.TryGetValue(key, out var bmp))
            return bmp;
        try
        {
            var n = source.PixelSize.Width;
            if (n != source.PixelSize.Height || px > n) return null;
            if (!AreaAlpha.GetOrCreateValue(source).TryGetValue(px, out var alpha))
                AreaAlpha.GetOrCreateValue(source)[px] = alpha = AreaScaledAlpha(ReadAlpha(source), n, px);
            bmp = Tint(alpha, px, Color.FromArgb(a, c.R, c.G, c.B));
        }
        catch { return null; }
        if (tints.Count >= 32) tints.Clear(); // themes and accents come and go
        tints[key] = bmp;
        return bmp;
    }

    private static byte[] ReadAlpha(Bitmap source)
    {
        var n = source.PixelSize.Width;
        var alpha = new byte[n * n];
        using var copy = new WriteableBitmap(source.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = copy.Lock();
        source.CopyPixels(fb);
        var row = new byte[n * 4];
        for (var y = 0; y < n; y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, row.Length);
            for (var x = 0; x < n; x++) alpha[y * n + x] = row[x * 4 + 3];
        }
        return alpha;
    }

    /// <summary>The colour at each pixel's coverage, premultiplied BGRA.</summary>
    private static Bitmap Tint(byte[] alpha, int px, Color c)
    {
        var result = new WriteableBitmap(new PixelSize(px, px), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var row = new byte[px * 4];
        using var fb = result.Lock();
        for (var y = 0; y < px; y++)
        {
            for (var x = 0; x < px; x++)
            {
                var k = alpha[y * px + x] * c.A / 255.0;
                row[x * 4] = (byte)Math.Round(c.B * k / 255);
                row[x * 4 + 1] = (byte)Math.Round(c.G * k / 255);
                row[x * 4 + 2] = (byte)Math.Round(c.R * k / 255);
                row[x * 4 + 3] = (byte)Math.Round(k);
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="alpha"/> (<paramref name="n"/>²) shrunk to <paramref name="px"/>² by area
    /// averaging: each output pixel is the coverage-weighted mean of the source pixels under it.
    /// A GPU or bicubic resample at a fractional ratio (the 48 px PNGs to 22 px) samples some
    /// source columns twice and skips others: uneven stroke weights.
    /// </summary>
    internal static byte[] AreaScaledAlpha(byte[] alpha, int n, int px)
    {
        var ratio = (double)n / px;
        var result = new byte[px * px];
        for (var oy = 0; oy < px; oy++)
        {
            for (var ox = 0; ox < px; ox++)
            {
                double sum = 0;
                for (var sy = (int)(oy * ratio); sy < Math.Min(n, (int)Math.Ceiling((oy + 1) * ratio)); sy++)
                {
                    var wy = Math.Min(sy + 1, (oy + 1) * ratio) - Math.Max(sy, oy * ratio);
                    for (var sx = (int)(ox * ratio); sx < Math.Min(n, (int)Math.Ceiling((ox + 1) * ratio)); sx++)
                    {
                        var wx = Math.Min(sx + 1, (ox + 1) * ratio) - Math.Max(sx, ox * ratio);
                        sum += alpha[sy * n + sx] * wx * wy;
                    }
                }
                result[oy * px + ox] = (byte)Math.Clamp(Math.Round(sum / (ratio * ratio)), 0, 255);
            }
        }
        return result;
    }
}
