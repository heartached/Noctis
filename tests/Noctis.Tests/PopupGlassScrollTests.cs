using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: scrolling inside a pop-up "sometimes 60 fps, sometimes 30, sometimes 10". With
/// Liquid Glass on, every scroll step inside a frosted card cost a second frame that repainted and
/// re-blurred the whole card, its blur ring and its shadow (PopupScrollCostTests' harness: 1.34 Mpx
/// and two frames per wheel notch instead of 0.47 Mpx and one): the compositor's dirty rect runs a
/// pixel or two past what changed (Avalonia 12 inflates it by 1 px and rounds out), so a list
/// scrolling edge to edge inside the card read as "something beside the panel changed". Pinned here
/// on a raster surface replaying the compositor.
/// </summary>
public class PopupGlassScrollTests
{
    private const int W = 240, H = 140;
    private static readonly SKRect Panel = new(40, 30, 200, 110);
    private static readonly SKRectI PanelI = new(40, 30, 200, 110);
    private const float Sigma = 8;
    private static readonly SKRectI Whole = new(0, 0, W, H);

    private static void Beneath(SKCanvas c)
    {
        c.Clear(SKColors.Black);
        using var white = new SKPaint { Color = SKColors.White };
        c.DrawRect(new SKRect(W / 2f, 0, W, H), white);
        using var red = new SKPaint { Color = new SKColor(220, 40, 40) };
        c.DrawRect(new SKRect(60, 50, 90, 80), red);
    }

    private static SKPath PanelPath()
    {
        var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Panel, 18));
        return path;
    }

    /// <summary>One compositor frame: only <paramref name="dirty"/> is repainted; beneath is the
    /// same every frame (only the card's own content, drawn above the frost, changed).</summary>
    private static void Frame(SKSurface s, SKRectI dirty, Action<SKCanvas> op)
    {
        var c = s.Canvas;
        c.Save();
        c.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height));
        Beneath(c);
        op(c);
        using var tint = new SKPaint { Color = new SKColor(37, 37, 37, 140), IsAntialias = true };
        c.DrawRoundRect(new SKRoundRect(Panel, 18), tint);
        c.Restore();
    }

    private static SKSurface NewSurface() => SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

    internal static (int X, int Y) Where;
    private static int MaxDiff(SKSurface a, SKSurface b)
    {
        using var ia = a.Snapshot();
        using var ib = b.Snapshot();
        using var pa = SKBitmap.FromImage(ia);
        using var pb = SKBitmap.FromImage(ib);
        var max = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var p = pa.GetPixel(x, y);
                var q = pb.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                if (d > max) { max = d; Where = (x, y); }
            }
        return max;
    }

    private static SKRectI Inflated(SKRectI r, int left, int top, int right, int bottom) =>
        new(r.Left - left, r.Top - top, r.Right + right, r.Bottom + bottom);

    /// <summary>Repaints asked for by one partial frame of a list scrolling inside the panel, its
    /// dirty rect being <paramref name="dirty"/>; the frost must still match a full frame.</summary>
    private static int RepaintsAfterOneScrollStep(SKRectI dirty)
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        try
        {
            using var s = NewSurface();
            using var path = PanelPath();
            Frame(s, Whole, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
            Frame(s, dirty, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));

            using var reference = NewSurface();
            Frame(reference, Whole, c => GlassBlur.Draw(c, reference, Panel, path, Sigma));
            Assert.True(MaxDiff(s, reference) <= 1, $"the partial frame drifted by {MaxDiff(s, reference)} at {Where} dirty {dirty}");
            return repaints;
        }
        finally { cache.Release(); }
    }

    [Fact]
    public void AListScrollingEdgeToEdge_InsideTheCard_AsksForNoSecondFullFrame()
    {
        // The list's viewport is the panel's width; the compositor's 1 px inflation (and 1 px
        // round-out at a fractional position) puts the dirty rect just past both sides. (Rows
        // clear of the rounded corners: a partial frame across a corner's antialiased rim is off
        // by a few levels there with or without a follow-up, a separate matter.)
        Assert.Equal(0, RepaintsAfterOneScrollStep(Inflated(new SKRectI(40, 50, 200, 90), 1, 0, 1, 0)));
        Assert.Equal(0, RepaintsAfterOneScrollStep(Inflated(new SKRectI(40, 50, 200, 90), 2, 1, 2, 1)));
    }

    [Fact]
    public void AChangeReachingFurtherPastTheCard_StillAsksForOne()
    {
        // 3 px past the panel is more than the compositor's slack: something beside it changed.
        Assert.Equal(1, RepaintsAfterOneScrollStep(Inflated(new SKRectI(40, 50, 200, 90), 0, 0, 3, 0)));
    }

    [Fact]
    public void TheBlurRing_IgnoresTheCompositorsSlack_ButNotARealChangeBesideThePanel()
    {
        Assert.False(GlassReachOp.ShouldRepaint(Inflated(new SKRectI(40, 50, 200, 90), 1, 0, 1, 0), PanelI, IntPtr.Zero, null));
        Assert.False(GlassReachOp.ShouldRepaint(Inflated(new SKRectI(40, 50, 200, 90), 2, 2, 2, 2), PanelI, IntPtr.Zero, null));
        Assert.True(GlassReachOp.ShouldRepaint(Inflated(new SKRectI(40, 50, 200, 90), 0, 0, 3, 0), PanelI, IntPtr.Zero, null));
        Assert.True(GlassReachOp.ShouldRepaint(new SKRectI(205, 80, 230, 95), PanelI, IntPtr.Zero, null));
    }
}
