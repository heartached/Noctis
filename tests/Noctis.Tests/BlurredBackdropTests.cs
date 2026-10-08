using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08 "inside of the settings, make the background blur just like the metadata pop
/// up": the Settings scrim (BlurredBackdrop) snapshots the app layer once per open, shows it
/// blurred under a lighter dim, frees it on close, works again on every reopen, never catches
/// itself, and retakes once after a resize. The headless compositor snapshots too; only the
/// pixel check needs real Skia (NOCTIS_TEST_SKIA=1).
/// </summary>
public class BlurredBackdropTests
{
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

    /// <summary>A window shaped like MainWindow's: an app layer (the target: bright stripes)
    /// and the backdrop as its sibling over it, hidden until "Settings" opens.</summary>
    private static (Window win, Panel content, BlurredBackdrop backdrop) Build(IBrush? fill = null)
    {
        var content = new StackPanel();
        var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273" };
        for (var i = 0; i < 12; i++)
            content.Children.Add(new Border { Height = 50, Background = fill ?? new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
        var layer = new Panel { Children = { content } };
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
            Content = new Panel { Children = { layer, backdrop } },
        };
        win.Show();
        PumpUntil(() => false, 60);
        return (win, layer, backdrop);
    }

    /// <summary>Open as MainWindow does: layer visible at 0, snapshot, then fade it up.</summary>
    private static void Open(BlurredBackdrop backdrop)
    {
        backdrop.IsVisible = true;
        var prepare = backdrop.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted, 3000), "PrepareAsync never finished");
        backdrop.Opacity = 1;
    }

    private static void Close(BlurredBackdrop backdrop)
    {
        backdrop.Opacity = 0;
        backdrop.IsVisible = false;
        backdrop.Release();
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var px = new byte[w * h * 4];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { handle.Free(); }
        return px;
    }

    [AvaloniaFact]
    public void OpenClose_ShowsSnapshotUnderLighterDim_FreesIt_AndWorksOnEveryReopen()
    {
        var (win, _, backdrop) = Build();
        try
        {
            Bitmap? previous = null;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Open(backdrop);
                Assert.True(PumpUntil(() => backdrop.HasSnapshot, 3000), $"cycle {cycle}: no snapshot");
                Assert.Same(backdrop.Snapshot, backdrop.SnapshotImage.Source);
                Assert.NotSame(previous, backdrop.Snapshot);
                Assert.Equal(1, backdrop.SnapshotImage.Opacity, 3);
                Assert.Equal(backdrop.BlurDimOpacity, backdrop.DimLayer.Opacity, 3);
                Assert.InRange(backdrop.BlurDimOpacity, 0.47, 0.49); // #A6 → #50
                previous = backdrop.Snapshot;

                Close(backdrop);
                Assert.False(backdrop.HasSnapshot);
                Assert.Null(backdrop.Snapshot);
                Assert.Null(backdrop.SnapshotImage.Source);
                Assert.Equal(0, backdrop.SnapshotImage.Opacity, 3);
                Assert.Equal(1, backdrop.DimLayer.Opacity, 3);
            }
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Closed_NoRefreshScheduled_AndResizeDoesNothing()
    {
        var (win, _, backdrop) = Build();
        try
        {
            backdrop.ScheduleRefresh();
            Assert.False(backdrop.IsRefreshPending);
            win.Width = 900;
            PumpUntil(() => false, 60);
            Assert.False(backdrop.IsRefreshPending);
            Assert.False(backdrop.HasSnapshot);
        }
        finally { win.Close(); }
    }

    /// <summary>The dim is fully up while the snapshot is taken (the Settings reopen from
    /// Statistics shows it at once): the snapshot is of the app layer only, so it stays as
    /// bright as the app, not darkened by the dim it sits under.</summary>
    [AvaloniaFact]
    public void Snapshot_NeverCatchesTheBackdropItself()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var (win, _, backdrop) = Build(Brushes.White);
        try
        {
            backdrop.IsVisible = true;
            backdrop.Opacity = 1;
            PumpUntil(() => false, 60);
            var capture = backdrop.CaptureAsync();
            Assert.True(PumpUntil(() => capture.IsCompleted, 3000));
            Assert.True(capture.Result);
            var px = Pixels(backdrop.Snapshot!);
            long sum = 0;
            int n = 0;
            for (var i = 0; i + 3 < px.Length; i += 4) { sum += px[i + 1]; n++; }
            // White stripes over a black window: ≥ 60% bright on average; under the #A6
            // dim it would be at most ~35%.
            Assert.True(sum / (double)n > 0.6 * 255, $"mean green {sum / (double)n:F0}");
            // Landed while showing: the blur cross-fades in rather than popping.
            Assert.NotNull(backdrop.SnapshotImage.Transitions);
        }
        finally { Close(backdrop); win.Close(); }
    }

    [AvaloniaFact]
    public void CaptureLandingAfterClose_IsThrownAway()
    {
        var (win, _, backdrop) = Build();
        try
        {
            backdrop.IsVisible = true;
            var capture = backdrop.CaptureAsync();
            Close(backdrop);
            Assert.True(PumpUntil(() => capture.IsCompleted, 3000));
            Assert.False(capture.Result);
            Assert.False(backdrop.HasSnapshot);
            Assert.Null(backdrop.SnapshotImage.Source);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Resize_WhileOpen_StretchesThenRetakesOnce()
    {
        var (win, _, backdrop) = Build();
        try
        {
            Open(backdrop);
            Assert.True(PumpUntil(() => backdrop.HasSnapshot, 3000));
            var first = backdrop.Snapshot!;
            var firstWidth = first.PixelSize.Width;

            win.Width = 1200;
            PumpUntil(() => false, 40);
            Assert.True(backdrop.IsRefreshPending);
            // Meanwhile the old snapshot is still the one shown, stretched to the new size.
            Assert.Same(first, backdrop.SnapshotImage.Source);

            Assert.True(PumpUntil(() => !ReferenceEquals(backdrop.Snapshot, first), 3000),
                $"never retaken (pending={backdrop.IsRefreshPending})");
            if (HeadlessTestApp.RealRendering) // the stubs' snapshot is always 1×1
                Assert.True(backdrop.Snapshot!.PixelSize.Width > firstWidth,
                    $"{firstWidth} -> {backdrop.Snapshot.PixelSize.Width}");
            Assert.Same(backdrop.Snapshot, backdrop.SnapshotImage.Source);
            Assert.False(backdrop.IsRefreshPending);
        }
        finally { Close(backdrop); win.Close(); }
    }
}
