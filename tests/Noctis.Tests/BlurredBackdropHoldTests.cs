using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: Settings over the live blur (Liquid Glass off) scrolled choppily. Any change
/// under a BlurEffect makes Avalonia repaint the whole effect visual, so with anything moving
/// behind the sheet the whole window was re-rendered and re-blurred every frame (harness:
/// 2.52 Mpx every idle frame). While the sheet scrolls or switches page, the live blur is now
/// held: a 1:1 render of the app layer with its effect stands in for it and the app layer is
/// skipped, until 300 ms after the last scroll step, when it goes live again under a fade.
/// </summary>
public class BlurredBackdropHoldTests : IDisposable
{
    public void Dispose()
    {
        BackdropSnapshot.BlurRadius = BackdropSnapshot.DefaultBlurRadius;
        AppGlass.Clear();
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private sealed record Scene(Window Win, Panel Layer, BlurredBackdrop Scrim, ScrollViewer Scroll, TranslateTransform Lyric);

    /// <summary>MainWindow's shape: the app layer (a page with a moving "lyric line"), the scrim
    /// over it, and a sheet with a scrollable list over both.</summary>
    private static Scene Build(bool live = true, double interfaceSize = 1)
    {
        var lyric = new TranslateTransform();
        var layer = new Panel
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 18, 30)),
            Children =
            {
                new StackPanel
                {
                    Children =
                    {
                        new Border { Height = 120, Background = Brushes.OrangeRed },
                        new Border { Height = 120, Background = Brushes.SteelBlue },
                        new Border { Height = 120, Background = Brushes.Gold },
                    },
                },
                new Border
                {
                    Width = 160, Height = 40, Background = Brushes.White, RenderTransform = lyric,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(60, 400, 0, 0),
                },
            },
        };
        var scrim = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer, LiveBlur = live };
        var rows = new StackPanel();
        for (var i = 0; i < 60; i++) rows.Children.Add(new Border { Height = 40, Margin = new Thickness(0, 0, 0, 4), Background = Brushes.DimGray });
        var scroll = new ScrollViewer { Content = rows };
        var sheet = new Border { Width = 400, Height = 300, Background = Brushes.Black, Child = scroll };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Black,
            Content = interfaceSize == 1
                ? new Panel { Children = { layer, scrim, sheet } }
                // Interface Size: the window's content under a scale (UiScaleHost is a LayoutTransformControl).
                : new LayoutTransformControl
                {
                    LayoutTransform = new ScaleTransform(interfaceSize, interfaceSize),
                    Child = new Panel { Children = { layer, scrim, sheet } },
                },
        };
        win.Show();
        PumpUntil(() => false, 80);
        scrim.HoldWhileScrolling(sheet);
        return new Scene(win, layer, scrim, scroll, lyric);
    }

    private static void Open(BlurredBackdrop scrim)
    {
        scrim.IsVisible = true;
        var prepare = scrim.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted, 3000), "PrepareAsync never finished");
        scrim.Opacity = 1;
        PumpUntil(() => false, 60);
    }

    private static void Close(BlurredBackdrop scrim)
    {
        scrim.Opacity = 0;
        scrim.IsVisible = false;
        scrim.Release();
    }

    private static double RadiusOn(Visual target) => Assert.IsAssignableFrom<IBlurEffect>(target.Effect).Radius;

    [AvaloniaFact]
    public void AScrollStep_HoldsTheBlur_AndItGoesLiveAgainAfterTheDelay()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            var radius = RadiusOn(s.Layer);
            var dim = s.Scrim.DimLayer.Opacity;

            s.Scroll.Offset = new Vector(0, 80);
            Assert.True(PumpUntil(() => s.Scrim.IsHeld, 1000), "the blur was not held");
            // Held: the app layer is skipped (no effect, Opacity 0) and its frame shows instead,
            // under the same dim.
            Assert.Null(s.Layer.Effect);
            Assert.Equal(0, s.Layer.Opacity);
            Assert.True(s.Scrim.HeldImage.IsVisible);
            Assert.NotNull(s.Scrim.HeldImage.Source);
            Assert.Equal(1, s.Scrim.HeldImage.Opacity);
            Assert.Equal(s.Layer.Bounds.Size, new Size(s.Scrim.HeldImage.Width, s.Scrim.HeldImage.Height));
            Assert.Equal(dim, s.Scrim.DimLayer.Opacity);

            // Live again after the delay, at the very radius, the held frame fading out over it.
            Assert.True(PumpUntil(() => !s.Scrim.IsHeld, 1500), "the blur never went live again");
            Assert.Equal(radius, RadiusOn(s.Layer), 3);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.False(s.Layer.IsSet(Visual.OpacityProperty));
            Assert.True(PumpUntil(() => !s.Scrim.HeldImage.IsVisible, 1000), "the held frame stayed up");
            Assert.Null(s.Scrim.HeldImage.Source);
            Assert.Equal(1, s.Scrim.HoldCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void AContinuousScroll_StaysHeld_OnOneFrame()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            var sw = Stopwatch.StartNew();
            var y = 0.0;
            var heldThroughout = true;
            while (sw.ElapsedMilliseconds < 1200)
            {
                s.Scroll.Offset = new Vector(0, y += 10);
                PumpUntil(() => false, 60);
                if (sw.ElapsedMilliseconds > 200 && !s.Scrim.IsHeld) heldThroughout = false;
            }
            Assert.True(heldThroughout, "the blur went live mid-scroll");
            Assert.Equal(1, s.Scrim.HoldCount);
            Assert.True(PumpUntil(() => !s.Scrim.IsHeld, 1500));
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void WhileHeld_ThingsMovingBehind_DoNotTouchTheAppLayersEffect()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            s.Scrim.Hold();
            Assert.True(PumpUntil(() => s.Scrim.IsHeld, 1000));
            for (var i = 0; i < 5; i++)
            {
                s.Lyric.X += 20;
                s.Scrim.Hold();
                PumpUntil(() => false, 20);
            }
            Assert.True(s.Scrim.IsHeld);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(0, s.Layer.Opacity);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ClosingMidScroll_LeavesTheAppLayerAsItWas_AndNothingComesBack()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            s.Scroll.Offset = new Vector(0, 80);
            Assert.True(PumpUntil(() => s.Scrim.IsHeld, 1000));

            // The close fade starts: the app is back under the fading layer at once.
            s.Scrim.Opacity = 0.6;
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.NotNull(s.Layer.Effect);
            Close(s.Scrim);
            Assert.Null(s.Layer.Effect);
            Assert.False(s.Layer.IsSet(Visual.OpacityProperty));
            Assert.False(s.Layer.IsSet(Visual.ClipToBoundsProperty)); // the live blur's clip is gone too
            Assert.False(s.Scrim.HeldImage.IsVisible);

            // The hold's timer and any late snapshot find nothing to do.
            PumpUntil(() => false, BlurredBackdrop.HoldReleaseDelay.Milliseconds + 300);
            Assert.False(s.Scrim.IsHeld);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Layer.Opacity);

            // A scroll on the closed sheet holds nothing.
            s.Scroll.Offset = new Vector(0, 160);
            PumpUntil(() => false, 100);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(1, s.Scrim.HoldCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ClosingRightAfterAScrollStep_BeforeTheFrameLands_StaysClosed()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            s.Scrim.Hold(); // the snapshot is on its way
            Close(s.Scrim);
            PumpUntil(() => false, 300);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(0, s.Scrim.HoldCount);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.False(s.Scrim.HeldImage.IsVisible);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void BackgroundBlurOff_NothingToHold()
    {
        BackdropSnapshot.BlurRadius = 0;
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(s.Scrim.IsLive);
            Assert.Null(s.Layer.Effect);
            s.Scroll.Offset = new Vector(0, 80);
            PumpUntil(() => false, 200);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(0, s.Scrim.HoldCount);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.Equal(1, s.Scrim.DimLayer.Opacity, 3); // the plain dim, as before
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void TheSnapshotScrim_LiquidGlassOn_IsLeftAlone()
    {
        AppGlass.Set(true, Colors.Black, Colors.Black);
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.HasSnapshot, 3000));
            Assert.False(s.Scrim.IsLive);
            s.Scroll.Offset = new Vector(0, 80);
            PumpUntil(() => false, 200);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(0, s.Scrim.HoldCount);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.Null(s.Layer.Effect);
            Assert.False(s.Scrim.HeldImage.IsVisible);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void DuringTheOpenFade_NothingIsHeld()
    {
        var s = Build();
        try
        {
            s.Scrim.Transitions = null;
            s.Scrim.IsVisible = true;
            var prepare = s.Scrim.PrepareAsync();
            Assert.True(PumpUntil(() => prepare.IsCompleted, 3000));
            s.Scrim.Opacity = 0.5;
            s.Scrim.Hold();
            PumpUntil(() => false, 150);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(0, s.Scrim.HoldCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ALayoutChangeInTheSheet_ThatScrollsNothing_HoldsNothing()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            ((StackPanel)s.Scroll.Content!).Children.Add(new Border { Height = 400 }); // extent grows, offset stays
            PumpUntil(() => false, 150);
            Assert.False(s.Scrim.IsHeld);
            Assert.Equal(0, s.Scrim.HoldCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    /// <summary>The swap must not show: the held frame is the live frame, also under Interface
    /// Size's scale. Real Skia only.</summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheHeldFrame_IsTheLiveFrame_PixelForPixel(double interfaceSize)
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var s = Build(interfaceSize: interfaceSize);
        try
        {
            Open(s.Scrim);
            PumpUntil(() => false, 300);
            using var live = s.Win.CaptureRenderedFrame()!;
            // The live blur clips the app layer to its bounds (for the held frame's sake): on
            // screen that changes nothing, the window clips there anyway.
            Assert.True(s.Layer.ClipToBounds);
            s.Layer.ClipToBounds = false;
            PumpUntil(() => false, 100);
            using (var unclipped = s.Win.CaptureRenderedFrame()!)
            {
                var (clipDiff, clipAt) = MaxDiff(live, unclipped);
                Assert.True(clipDiff <= 1, $"clipping the live layer changed the screen by {clipDiff} at {clipAt}");
            }
            s.Layer.ClipToBounds = true;
            PumpUntil(() => false, 100);
            s.Scrim.Hold(TimeSpan.FromSeconds(5));
            Assert.True(PumpUntil(() => s.Scrim.IsHeld, 1000));
            PumpUntil(() => false, 100);
            using var held = s.Win.CaptureRenderedFrame()!;
            var (max, at) = MaxDiff(live, held);
            Assert.True(max <= 2, $"the held frame differs from the live one by {max} at {at}");
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    private static (int Max, PixelPoint At) MaxDiff(Bitmap a, Bitmap b)
    {
        var w = a.PixelSize.Width;
        var h = a.PixelSize.Height;
        Assert.Equal(a.PixelSize, b.PixelSize);
        var pa = new byte[w * h * 4];
        var pb = new byte[w * h * 4];
        foreach (var (bitmap, px) in new[] { (a, pa), (b, pb) })
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
            finally { handle.Free(); }
        }
        var max = 0;
        var at = default(PixelPoint);
        for (var i = 0; i < pa.Length; i++)
        {
            var d = Math.Abs(pa[i] - pb[i]);
            if (d > max) { max = d; at = new PixelPoint(i / 4 % w, i / 4 / w); }
        }
        return (max, at);
    }
}
