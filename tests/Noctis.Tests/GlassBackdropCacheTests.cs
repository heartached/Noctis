using System;
using Noctis.Controls;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The glass frost without a per-frame full repaint. The compositor repaints only a frame's
/// dirty rect; these tests replay that on a raster surface: clip to the dirty rect, repaint
/// what lies beneath, run the backdrop op, then the panel's own tint (as GlassPanel does).
/// A partial frame must come out exactly like a full one — the old frost double-frosted it
/// ("a lighter box") unless the whole panel was repainted every frame.
/// </summary>
public class GlassBackdropCacheTests
{
    private const int W = 240, H = 140;
    private static readonly SKRect Panel = new(40, 30, 200, 110);
    private const float Sigma = 8;

    /// <summary>What lies under the panel: a hard black/white edge down the middle and a
    /// few coloured blocks, so any double blur or double tint shows.</summary>
    private static void Beneath(SKCanvas c, bool changed = false)
    {
        c.Clear(SKColors.Black);
        using var white = new SKPaint { Color = SKColors.White };
        c.DrawRect(new SKRect(W / 2f, 0, W, H), white);
        using var red = new SKPaint { Color = new SKColor(220, 40, 40) };
        c.DrawRect(new SKRect(60, 50, 90, 80), red);
        if (changed)
        {
            using var blue = new SKPaint { Color = new SKColor(30, 60, 230) };
            c.DrawRect(new SKRect(96, 56, 116, 76), blue);
        }
    }

    private static SKPath PanelPath()
    {
        var path = new SKPath();
        path.AddRoundRect(new SKRoundRect(Panel, 18));
        return path;
    }

    /// <summary>The panel's tint over its frost, like GlassPanel.Render.</summary>
    private static void Tint(SKCanvas c)
    {
        using var tint = new SKPaint { Color = new SKColor(37, 37, 37, 140), IsAntialias = true };
        c.DrawRoundRect(new SKRoundRect(Panel, 18), tint);
    }

    private static SKSurface NewSurface() =>
        SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

    /// <summary>One compositor frame: only <paramref name="dirty"/> is repainted.</summary>
    private static void Frame(SKSurface s, SKRectI dirty, bool changed, Action<SKCanvas> op)
    {
        var c = s.Canvas;
        c.Save();
        c.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height));
        Beneath(c, changed);
        op(c);
        Tint(c);
        c.Restore();
    }

    private static readonly SKRectI Whole = new(0, 0, W, H);

    private static SKBitmap Pixels(SKSurface s)
    {
        using var img = s.Snapshot();
        return SKBitmap.FromImage(img);
    }

    private static int MaxDiff(SKBitmap a, SKBitmap b, SKRectI area)
    {
        var max = 0;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                max = Math.Max(max, Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))));
            }
        return max;
    }

    /// <summary>A full frame of <paramref name="changed"/> content: the reference any
    /// partial frame must match.</summary>
    private static SKBitmap FullFrame(bool changed, GlassLensFrame? lens = null)
    {
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, changed, c => GlassBlur.Draw(c, s, Panel, path, Sigma, 1, lens));
        return Pixels(s);
    }

    /// <summary>The phone's Liquid Glass lens on the same panel: a refracting rim, a colour split,
    /// a slight zoom and vibrancy, so any stale or double-frosted pixel it reads shows.</summary>
    private static readonly GlassLensFrame Lens = new(Panel, new GlassCornerRadii(18, 18, 18, 18), 16, 16, 0.6f, 1.05f, 1.3f);

    [Fact]
    public void ChildOnlyRepaint_FrostsExactlyLikeAFullFrame_AndAsksForNothingMore()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        using var reference = FullFrame(changed: false);
        Assert.True(MaxDiff(Pixels(s), reference, Whole) <= 1);

        // A child over the panel changed (hover, the scrolling title): only its rect repaints,
        // and what lies beneath it is the same as before.
        var dirty = new SKRectI(100, 50, 130, 80);
        Frame(s, dirty, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));

        using var after = Pixels(s);
        Assert.True(MaxDiff(after, reference, Whole) <= 1, $"partial frame drifted by {MaxDiff(after, reference, Whole)}");
        Assert.Equal(0, repaints);
    }

    [Fact]
    public void WithTheLens_AChildOnlyRepaint_StillFrostsExactlyLikeAFullFrame()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true, Lens));
        using var reference = FullFrame(changed: false, Lens);
        Assert.True(MaxDiff(Pixels(s), reference, Whole) <= 1);
        // The lens really drew: it differs from the plain frost.
        using var plain = FullFrame(changed: false);
        Assert.True(MaxDiff(reference, plain, Whole) > 12);

        var dirty = new SKRectI(100, 50, 130, 80);
        Frame(s, dirty, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false, Lens));
        using var after = Pixels(s);
        Assert.True(MaxDiff(after, reference, Whole) <= 1, $"partial frame drifted by {MaxDiff(after, reference, Whole)}");
        Assert.Equal(0, repaints);
        // And on the rim, where the lens reads from furthest away.
        var rim = new SKRectI(42, 60, 60, 80);
        Frame(s, rim, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false, Lens));
        Assert.True(MaxDiff(Pixels(s), reference, Whole) <= 1);
        Assert.Equal(0, repaints);
    }

    [Fact]
    public void WithTheLens_AChangeBeneath_IsExactInsideTheDirtyRect_AndAsksForOneFullRepaint()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true, Lens));

        var dirty = new SKRectI(92, 52, 120, 80);
        Frame(s, dirty, true, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false, Lens));
        using var reference = FullFrame(changed: true, Lens);
        Assert.True(MaxDiff(Pixels(s), reference, dirty) <= 1);
        Assert.Equal(1, repaints);

        Frame(s, Whole, true, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true, Lens));
        Assert.True(MaxDiff(Pixels(s), reference, Whole) <= 1);
    }

    [Fact]
    public void TheOldFrost_DoubleFrostsTheSamePartialFrame()
    {
        // Guard for the test above: the plain blur, on the same partial frame, reads last
        // frame's frosted, tinted pixels around the dirty rect and visibly misses.
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => GlassBlur.Draw(c, s, Panel, path, Sigma));
        var dirty = new SKRectI(100, 50, 130, 80);
        Frame(s, dirty, false, c => GlassBlur.Draw(c, s, Panel, path, Sigma));
        using var reference = FullFrame(changed: false);
        Assert.True(MaxDiff(Pixels(s), reference, dirty) > 12, $"only {MaxDiff(Pixels(s), reference, dirty)}");
    }

    [Fact]
    public void ChangeBeneathThePanel_IsExactInsideTheDirtyRect_AndAsksForOneFullRepaint()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));

        var dirty = new SKRectI(92, 52, 120, 80);
        Frame(s, dirty, true, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));
        using var reference = FullFrame(changed: true);
        Assert.True(MaxDiff(Pixels(s), reference, dirty) <= 1);
        // The frost moves up to 3σ past the dirty rect, which this frame did not repaint.
        Assert.Equal(1, repaints);

        // A second change before that repaint ran does not ask again.
        Frame(s, dirty, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));
        Assert.Equal(1, repaints);

        // The requested repaint (new panel render data: whole) settles everything...
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        using var settled = FullFrame(changed: false);
        Assert.True(MaxDiff(Pixels(s), settled, Whole) <= 1);
        // ...and the next change may ask again.
        Frame(s, dirty, true, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));
        Assert.Equal(2, repaints);
    }

    [Fact]
    public void RepaintBesideThePanel_WithinTheBlursReach_AsksForAFullRepaint()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        using var before = Pixels(s);

        var beside = new SKRectI(204, 50, 220, 70); // 4 px right of the panel, inside 3σ = 24
        Frame(s, beside, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));
        Assert.Equal(1, repaints);
        Assert.Equal(0, MaxDiff(Pixels(s), before, new SkiaSharp.SKRectI(40, 30, 200, 110)));
    }

    [Fact]
    public void AfterAFrameInAnOpacityLayer_ThePartialFrameAsksForAFullRepaint()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        cache.MarkStale();
        Assert.True(cache.IsSuspect);
        Frame(s, new SKRectI(100, 50, 130, 80), false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: false));
        Assert.Equal(1, repaints);
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        Assert.False(cache.IsSuspect);
    }

    [Fact]
    public void ReleasedCache_StillFrostsAndNeverCallsBack()
    {
        var repaints = 0;
        var cache = new GlassBackdropCache(() => repaints++);
        using var s = NewSurface();
        using var path = PanelPath();
        Frame(s, Whole, false, c => cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true));
        cache.Release();
        cache.Release(); // idempotent
        Frame(s, Whole, true, c => Assert.True(cache.Draw(c, s, path, Sigma, 1, wholePanelRepainted: true)));
        using var reference = FullFrame(changed: true);
        Assert.True(MaxDiff(Pixels(s), reference, Whole) <= 1);
        Assert.Equal(0, repaints);
        // A raster copy is freed on release; nothing waits for the render thread.
        Assert.False(GlassBackdropCache.HasRetired);
    }

    // ---- Two panels within each other's blur reach (the phone's mini player 10 dp above
    // the tab capsule) ---------------------------------------------------------------------

    private static readonly SKRectI Upper = new(40, 30, 200, 60), Lower = new(40, 72, 200, 102);

    /// <summary>
    /// Replays the compositor for the two stacked panels: each frame repaints the union of
    /// what was invalidated; every panel the dirty rect meets draws (a requested repaint is new
    /// render data, so whole), and every reach ring it meets asks its panel for a full repaint
    /// when <paramref name="reachRule"/> says so. Frame 1 is a change right above the upper
    /// panel only (a row scrolling past its edge). Returns the full repaints asked for in
    /// frames 2..<paramref name="frames"/>: anything above 0 is a repaint loop.
    /// </summary>
    private static int RepaintsAfterOneChange(Func<SKRectI, SKRectI, IntPtr, GlassBackdropCache, bool> reachRule, int frames)
    {
        using var s = NewSurface();
        var rects = new[] { Upper, Lower };
        var pending = new bool[2];
        var caches = new GlassBackdropCache[2];
        for (var i = 0; i < 2; i++)
        {
            var k = i;
            caches[k] = new GlassBackdropCache(() => pending[k] = true);
        }
        var reach = (int)Math.Ceiling(3 * Sigma);
        var asked = 0;
        try
        {
            void RunFrame(SKRectI dirty, bool[] whole)
            {
                var c = s.Canvas;
                c.Save();
                c.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height));
                Beneath(c);
                for (var i = 0; i < 2; i++)
                {
                    if (SKRectI.Intersect(rects[i], dirty).IsEmpty) continue;
                    using var path = new SKPath();
                    path.AddRoundRect(new SKRoundRect(SKRect.Create(rects[i].Left, rects[i].Top, rects[i].Width, rects[i].Height), 12));
                    caches[i].NoteOwnRect(rects[i], s.Handle);
                    caches[i].Draw(c, s, path, Sigma, 1, wholePanelRepainted: whole[i]);
                }
                c.Restore();
                for (var i = 0; i < 2; i++)
                {
                    var ring = new SKRectI(rects[i].Left - reach, rects[i].Top - reach, rects[i].Right + reach, rects[i].Bottom + reach);
                    if (SKRectI.Intersect(ring, dirty).IsEmpty) continue;
                    if (reachRule(dirty, rects[i], s.Handle, caches[i])) pending[i] = true;
                }
            }

            RunFrame(Whole, new[] { true, true });
            Array.Clear(pending);
            RunFrame(new SKRectI(60, 4, 120, 18), new[] { false, false });
            for (var f = 2; f <= frames; f++)
            {
                if (!pending[0] && !pending[1]) break;
                var whole = (bool[])pending.Clone();
                var dirty = SKRectI.Empty;
                for (var i = 0; i < 2; i++)
                    if (whole[i]) dirty = dirty.IsEmpty ? rects[i] : SKRectI.Union(dirty, rects[i]);
                Array.Clear(pending);
                RunFrame(dirty, whole);
                asked += (pending[0] ? 1 : 0) + (pending[1] ? 1 : 0);
            }
            return asked;
        }
        finally
        {
            foreach (var cache in caches) cache.Release();
        }
    }

    [Fact]
    public void AdjacentPanels_SettleAfterAChangeBesideOne_InsteadOfReFrostingEachOtherForever()
    {
        Assert.Equal(0, RepaintsAfterOneChange(GlassReachOp.ShouldRepaint, frames: 30));
    }

    [Fact]
    public void TheOldReachRule_PingPongsBetweenAdjacentPanels()
    {
        // Guard for the test above: the rule before the fix (any repaint in the ring that
        // neither lies inside the panel nor covers it) keeps the two panels repainting each
        // other, one per frame — the phone's bar rendered at the display rate while idle.
        static bool OldRule(SKRectI clip, SKRectI panel, IntPtr surface, GlassBackdropCache self) =>
            !(clip.IsEmpty || panel.Contains(clip) || clip.Contains(panel));
        Assert.Equal(29, RepaintsAfterOneChange(OldRule, frames: 30));
    }

    [Fact]
    public void AChangeBesideAPanel_StillReachesIt_EvenNextToAnotherPanel()
    {
        using var s = NewSurface();
        var upper = new GlassBackdropCache();
        var lower = new GlassBackdropCache();
        try
        {
            upper.NoteOwnRect(Upper, s.Handle);
            lower.NoteOwnRect(Lower, s.Handle);
            // The upper panel repainting itself: not news for the lower one.
            Assert.False(GlassReachOp.ShouldRepaint(Upper, Lower, s.Handle, lower));
            // A row scrolling in the gap and under the upper panel: content beside the lower one.
            Assert.True(GlassReachOp.ShouldRepaint(new SKRectI(30, 40, 210, 70), Lower, s.Handle, lower));
            // Content beside it with no panel around.
            Assert.True(GlassReachOp.ShouldRepaint(new SKRectI(205, 80, 230, 95), Lower, s.Handle, lower));
            // A panel on another surface says nothing about this one.
            Assert.True(GlassReachOp.ShouldRepaint(Upper, Lower, IntPtr.Zero + 1, lower));
            // Once the upper panel's copy is released it no longer counts.
            upper.Release();
            Assert.True(GlassReachOp.ShouldRepaint(Upper, Lower, s.Handle, lower));
        }
        finally
        {
            upper.Release();
            lower.Release();
        }
    }

    [Fact]
    public void Draw_CopiesOnlyTheRegionItReads()
    {
        // The frost used to snapshot the whole window surface for every panel, every frame.
        using var s = NewSurface();
        using var img = GlassBlur.Snapshot(s, GlassBlur.SourceRect(Panel, Sigma), out var at);
        Assert.NotNull(img);
        Assert.Equal(new SKRectI(16, 6, 224, 134), at); // panel + 3σ (24) each side
        Assert.Equal(at.Width, img!.Width);

        // Clipped to the surface at its edges.
        using var edge = GlassBlur.Snapshot(s, new SKRectI(-10, -10, 30, 30), out var atEdge);
        Assert.Equal(new SKRectI(0, 0, 30, 30), atEdge);
        Assert.Equal(30, edge!.Width);
    }
}
