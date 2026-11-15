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
/// Owner 11-14/15: Settings over the live blur (Liquid Glass off) was choppy, scrolling or at
/// rest. Any change under a BlurEffect makes Avalonia repaint the whole effect visual, so with
/// anything moving behind the sheet the whole window was re-rendered and re-blurred every frame
/// (harness: 2.52 Mpx every frame). Once the scrim is fully up a stand-in takes over: the app's
/// host is skipped, and the blur is drawn around the sheet from a low-resolution render of the
/// app, retaken only when something behind changes, at most ~30 times a second.
/// </summary>
public class BlurredBackdropStandInTests : IDisposable
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

    internal sealed record Scene(Window Win, Panel Host, Panel Layer, BlurredBackdrop Scrim, Border Sheet, ScrollViewer Scroll, TranslateTransform Lyric);

    /// <summary>MainWindow's shape: the app layer (stripes and a moving "lyric line") alone in its
    /// host, the scrim over it, and an opaque sheet with a scrollable list over both.</summary>
    internal static Scene Build(bool live = true, double interfaceSize = 1, bool withHost = true, bool withHole = true)
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
        var host = new Panel { Children = { layer } };
        var rows = new StackPanel();
        for (var i = 0; i < 60; i++) rows.Children.Add(new Border { Height = 40, Margin = new Thickness(0, 0, 0, 4), Background = Brushes.DimGray });
        var scroll = new ScrollViewer { Content = rows };
        var sheet = new Border { Width = 400, Height = 300, Background = Brushes.Black, CornerRadius = new CornerRadius(20), Child = scroll };
        var scrim = new BlurredBackdrop
        {
            IsVisible = false, Opacity = 0, Target = layer, LiveBlur = live,
            TargetHost = withHost ? host : null, Hole = withHole ? sheet : null,
        };
        var root = new Panel { Children = { host, scrim, sheet } };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Black,
            Content = interfaceSize == 1
                ? root
                : new LayoutTransformControl { LayoutTransform = new ScaleTransform(interfaceSize, interfaceSize), Child = root },
        };
        win.Show();
        PumpUntil(() => false, 80);
        return new Scene(win, host, layer, scrim, sheet, scroll, lyric);
    }

    internal static void Open(BlurredBackdrop scrim)
    {
        scrim.IsVisible = true;
        var prepare = scrim.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted, 3000), "PrepareAsync never finished");
        scrim.Opacity = 1;
        PumpUntil(() => false, 60);
    }

    internal static void Close(BlurredBackdrop scrim)
    {
        scrim.Opacity = 0;
        scrim.IsVisible = false;
        scrim.Release();
    }

    private static double RadiusOn(Visual target) => Assert.IsAssignableFrom<IBlurEffect>(target.Effect).Radius;

    [AvaloniaFact]
    public void FullyUp_TheStandInTakesOver_AroundTheSheet()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            var dim = s.Scrim.DimLayer.Opacity;
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000), "no stand-in");
            // The app is skipped (its host at 0) but itself untouched, without the effect.
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Layer.Opacity);
            Assert.Equal(0, s.Host.Opacity);
            Assert.True(s.Scrim.IsTargetHostHidden);
            Assert.Equal(dim, s.Scrim.DimLayer.Opacity);
            // Four bands around the opaque sheet, none over its inside.
            var bands = s.Scrim.Bands.Where(b => b.IsVisible).ToList();
            Assert.Equal(4, bands.Count);
            var sheet = s.Sheet.Bounds;
            var inner = sheet.Deflate(s.Sheet.CornerRadius.TopLeft + 1);
            foreach (var band in bands)
            {
                Assert.NotNull(band.Source);
                Assert.False(band.Bounds.Intersects(inner.Deflate(1)), $"a band {band.Bounds} covers the sheet's inside {inner}");
            }
            Assert.Equal(1, s.Scrim.StandInEntries);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void NothingMovingBehind_NoRetakes()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));
            PumpUntil(() => false, 600);
            Assert.Equal(0, s.Scrim.RetakeCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void SomethingMovingBehind_IsRetaken_AtMostThirtyTimesASecond_AndStopsWhenItStops()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                s.Lyric.X = (s.Lyric.X + 3) % 300;
                PumpUntil(() => false, 8);
            }
            var during = s.Scrim.RetakeCount;
            Assert.InRange(during, 4, 34);
            // It stops: one trailing retake at most, then nothing.
            PumpUntil(() => false, (int)BlurredBackdrop.TrailingRetakeDelay.TotalMilliseconds + 300);
            var settled = s.Scrim.RetakeCount;
            Assert.InRange(settled - during, 0, 4); // the one in flight, the trailing one (headless: and a scroll bar fading)
            PumpUntil(() => false, 500);
            Assert.Equal(settled, s.Scrim.RetakeCount);
            Assert.True(s.Scrim.IsStandingIn);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ScrollingTheSheet_RetakesNothing()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("the change probe reads the frame's dirty rect: needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));
            PumpUntil(() => false, 300);
            var before = s.Scrim.RetakeCount;
            for (var i = 1; i <= 30; i++)
            {
                s.Scroll.Offset = new Vector(0, i * 20);
                PumpUntil(() => false, 10);
            }
            PumpUntil(() => false, 300);
            Assert.Equal(before, s.Scrim.RetakeCount);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ANewBlurStrength_IsRetaken_InPlace()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));
            var before = s.Scrim.RetakeCount;
            BackdropSnapshot.BlurRadius = 4;
            Assert.True(PumpUntil(() => s.Scrim.RetakeCount > before, 1000), "no retake for the new strength");
            Assert.True(s.Scrim.IsStandingIn);
            Assert.Null(s.Layer.Effect);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void TheCloseFade_RunsOnTheLiveBlur_AndClosingLeavesTheAppAsItWas()
    {
        var s = Build();
        try
        {
            Open(s.Scrim);
            var full = BlurredBackdrop.LiveRadiusFor(BackdropSnapshot.BlurRadius);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));

            s.Scrim.Opacity = 0.6;
            Assert.False(s.Scrim.IsStandingIn);
            Assert.False(s.Host.IsSet(Visual.OpacityProperty));
            Assert.Equal(full * 0.6, RadiusOn(s.Layer), 3);
            Assert.False(s.Scrim.Bands.Any(b => b.IsVisible && s.Scrim.IsStandingIn));

            Close(s.Scrim);
            Assert.Null(s.Layer.Effect);
            Assert.False(s.Layer.IsSet(Visual.ClipToBoundsProperty));
            Assert.False(s.Layer.IsSet(Visual.OpacityProperty));
            Assert.False(s.Host.IsSet(Visual.OpacityProperty));
            PumpUntil(() => false, 400);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Host.Opacity);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void ClosingBeforeTheFirstFrameLands_StaysClosed()
    {
        var s = Build();
        try
        {
            s.Scrim.IsVisible = true;
            var prepare = s.Scrim.PrepareAsync();
            Assert.True(PumpUntil(() => prepare.IsCompleted, 3000));
            s.Scrim.Opacity = 1; // the stand-in's first render is on its way
            Close(s.Scrim);
            PumpUntil(() => false, 300);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.Equal(0, s.Scrim.StandInEntries);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Host.Opacity);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void DuringTheOpenFade_ItStaysLive()
    {
        var s = Build();
        try
        {
            s.Scrim.Transitions = null;
            s.Scrim.IsVisible = true;
            var prepare = s.Scrim.PrepareAsync();
            Assert.True(PumpUntil(() => prepare.IsCompleted, 3000));
            s.Scrim.Opacity = 0.5;
            PumpUntil(() => false, 200);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.NotNull(s.Layer.Effect);
            Assert.Equal(1, s.Host.Opacity);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void BackgroundBlurOff_StaysThePlainDim()
    {
        BackdropSnapshot.BlurRadius = 0;
        var s = Build();
        try
        {
            Open(s.Scrim);
            PumpUntil(() => false, 300);
            Assert.True(s.Scrim.IsLive);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.Null(s.Layer.Effect);
            Assert.Equal(1, s.Host.Opacity);
            Assert.Equal(1, s.Scrim.DimLayer.Opacity, 3);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void WithoutAHost_TheLiveBlurStaysAsItWas()
    {
        var s = Build(withHost: false);
        try
        {
            Open(s.Scrim);
            PumpUntil(() => false, 300);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.Equal(BlurredBackdrop.LiveRadiusFor(BackdropSnapshot.BlurRadius), RadiusOn(s.Layer), 3);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void WithoutAnOpaqueSheet_OneBandCoversItAll()
    {
        var s = Build(withHole: false);
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsStandingIn, 1000));
            var band = Assert.Single(s.Scrim.Bands, b => b.IsVisible);
            Assert.Equal(s.Layer.Bounds.Size, band.Bounds.Size);
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    [AvaloniaFact]
    public void LiquidGlassOn_TheOpaqueSnapshotHidesTheApp_AndRetakesStillWork()
    {
        AppGlass.Set(true, Colors.Black, Colors.Black);
        var s = Build();
        try
        {
            Open(s.Scrim);
            Assert.True(PumpUntil(() => s.Scrim.IsOpaqueCover, 3000));
            Assert.False(s.Scrim.IsLive);
            Assert.False(s.Scrim.IsStandingIn);
            Assert.Equal(0, s.Host.Opacity);
            Assert.Equal(1, s.Layer.Opacity);

            // A retake (a theme switch from the sheet) renders the app itself, host hidden or not.
            var captures = s.Scrim.CaptureCount;
            s.Scrim.RefreshSoon();
            Assert.True(PumpUntil(() => s.Scrim.CaptureCount > captures, 3000), "no retake");

            Close(s.Scrim);
            Assert.False(s.Host.IsSet(Visual.OpacityProperty));
        }
        finally { Close(s.Scrim); s.Win.Close(); }
    }

    /// <summary>The stand-in must look like the live blur: same brightness and tint, no seams at
    /// the bands, no edge change, at the default and the strongest blur, at 100/125/150%.</summary>
    [AvaloniaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(1.0, 10.0)]
    [InlineData(1.5, 10.0)]
    public void TheStandIn_LooksLikeTheLiveBlur(double interfaceSize, double blur)
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        BackdropSnapshot.BlurRadius = blur;
        var s = Build(interfaceSize: interfaceSize, withHost: false); // no host: stays live
        Bitmap? live = null;
        try
        {
            Open(s.Scrim);
            PumpUntil(() => false, 300);
            live = s.Win.CaptureRenderedFrame()!;
        }
        finally { Close(s.Scrim); s.Win.Close(); }

        var t = Build(interfaceSize: interfaceSize);
        try
        {
            Open(t.Scrim);
            Assert.True(PumpUntil(() => t.Scrim.IsStandingIn, 1000));
            // A retake too (the first frame is rendered with the effect, retakes blur separately).
            t.Scrim.RequestRetake();
            Assert.True(PumpUntil(() => t.Scrim.RetakeCount > 0, 1000));
            PumpUntil(() => false, 100);
            using var standIn = t.Win.CaptureRenderedFrame()!;
            if (Environment.GetEnvironmentVariable("NOCTIS_STANDIN_DUMP") is { } dump)
            {
                live!.Save(System.IO.Path.Combine(dump, $"live-{interfaceSize}-{blur}.png"));
                standIn.Save(System.IO.Path.Combine(dump, $"standin-{interfaceSize}-{blur}.png"));
            }
            var (max, mean, psnr, at) = Compare(live!, standIn);
            if (Environment.GetEnvironmentVariable("NOCTIS_STANDIN_DUMP") is { } log)
                System.IO.File.AppendAllText(System.IO.Path.Combine(log, "compare.txt"),
                    $"size {interfaceSize} blur {blur}: PSNR {psnr:0.0} dB, mean {mean:0.00}, max {max} at {at}" + Environment.NewLine);
            Assert.True(psnr >= 45 && max <= 12,
                $"stand-in vs live: PSNR {psnr:0.0} dB, mean {mean:0.00}, max {max} at {at} (Interface Size {interfaceSize}, blur {blur})");
        }
        finally { live?.Dispose(); Close(t.Scrim); t.Win.Close(); }
    }

    internal static (int Max, double Mean, double Psnr, PixelPoint At) Compare(Bitmap a, Bitmap b)
    {
        var w = a.PixelSize.Width;
        var h = a.PixelSize.Height;
        Assert.Equal(a.PixelSize, b.PixelSize);
        var pa = Pixels(a);
        var pb = Pixels(b);
        var max = 0;
        var at = default(PixelPoint);
        double sum = 0, sq = 0;
        var n = 0;
        for (var i = 0; i < pa.Length; i++)
        {
            if (i % 4 == 3) continue;
            var d = Math.Abs(pa[i] - pb[i]);
            sum += d;
            sq += d * d;
            n++;
            if (d > max) { max = d; at = new PixelPoint(i / 4 % w, i / 4 / w); }
        }
        var mse = sq / n;
        return (max, sum / n, mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse), at);
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var px = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { handle.Free(); }
        return px;
    }
}
