using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// 11-27: a theme switch made from the open Settings sheet (where Settings › Appearance lives)
/// hitched right after the switch, from the backdrop's retake. The retake now starts after the
/// frame that shows the new look, never inside the switch's own jobs; its cross-fade keeps the
/// app under the sheet hidden (both snapshots are opaque), and what ends on screen is the new
/// look, the same as a fresh snapshot.
/// </summary>
public class BlurredBackdropThemeRetakeTests
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

    /// <summary>MainWindow's shape: the app layer inside a host of its own (AppContentHost), the
    /// backdrop over it, opened and settled as the Settings sheet opens it.</summary>
    private static (Window Win, StackPanel Rows, Panel Host, BlurredBackdrop Backdrop) OpenSheet(IBrush fill)
    {
        var rows = new StackPanel();
        for (var i = 0; i < 12; i++)
            rows.Children.Add(new Border { Height = 50, Background = fill });
        var layer = new Panel { Children = { rows } };
        var host = new Panel { Children = { layer } };
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer, TargetHost = host };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Black,
            Content = new Panel { Children = { host, backdrop } },
        };
        win.Show();
        PumpUntil(() => false, 60);
        backdrop.IsVisible = true;
        var prepare = backdrop.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted, 3000), "PrepareAsync never finished");
        backdrop.Opacity = 1;
        Assert.True(PumpUntil(() => backdrop.HasSnapshot, 3000), "no snapshot");
        PumpUntil(() => false, 300); // the open's own fade settles
        return (win, rows, host, backdrop);
    }

    private static void Close(Window win, BlurredBackdrop backdrop)
    {
        backdrop.Opacity = 0;
        backdrop.IsVisible = false;
        backdrop.Release();
        win.Close();
    }

    /// <summary>Records, each time it draws, whether the backdrop was already taking a snapshot.</summary>
    private sealed class DrawProbe : Control
    {
        public static readonly StyledProperty<IBrush?> FillProperty =
            AvaloniaProperty.Register<DrawProbe, IBrush?>(nameof(Fill));

        static DrawProbe() => AffectsRender<DrawProbe>(FillProperty);

        public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public Func<bool>? Capturing;
        public readonly List<bool> Seen = new();

        public override void Render(DrawingContext context)
        {
            Seen.Add(Capturing?.Invoke() ?? false);
            context.FillRectangle(Fill ?? Brushes.Transparent, new Rect(Bounds.Size));
        }
    }

    /// <summary>The theme flags ask for the retake and the theme lands in the same job. The frame
    /// that draws the new look must come first: when it is drawn, no capture has started (before
    /// 11-27 a Background hop alone could run the capture ahead of that frame).</summary>
    [AvaloniaFact]
    public void ThemeSwitch_StartsTheRetakeAfterTheSwitchFrame_NotInsideIt()
    {
        var (win, rows, _, backdrop) = OpenSheet(Brushes.White);
        try
        {
            var probe = new DrawProbe { Height = 50, Fill = Brushes.White, Capturing = () => backdrop.IsCapturing };
            rows.Children.Add(probe);
            PumpUntil(() => false, 100);
            var first = backdrop.Snapshot!;
            var captures = backdrop.CaptureCount;
            var afterFrame = backdrop.RetakesAfterFrame;
            probe.Seen.Clear();

            backdrop.RefreshSoon();
            probe.Fill = Brushes.Black;
            foreach (var row in rows.Children.OfType<Border>()) row.Background = Brushes.Black;
            Assert.True(PumpUntil(() => !ReferenceEquals(backdrop.Snapshot, first), 3000), "never retaken");
            Assert.NotEmpty(probe.Seen);
            Assert.False(probe.Seen[0], "the new look's first frame was drawn with the retake already running");
            Assert.Equal(captures + 1, backdrop.CaptureCount);
            Assert.Equal(afterFrame + 1, backdrop.RetakesAfterFrame);
        }
        finally { Close(win, backdrop); }
    }

    /// <summary>The cross-fade from the old snapshot to the new one is opaque throughout: the app
    /// under the sheet stays hidden (not drawn), as it is before and after.</summary>
    [AvaloniaFact]
    public void RetakeCrossFade_KeepsTheAppUnderTheSheetHidden()
    {
        var (win, rows, host, backdrop) = OpenSheet(Brushes.White);
        try
        {
            Assert.True(backdrop.IsOpaqueCover);
            Assert.True(backdrop.IsTargetHostHidden);
            Assert.Equal(0, host.Opacity);
            var first = backdrop.Snapshot!;

            backdrop.RefreshSoon();
            foreach (var row in rows.Children) ((Border)row).Background = Brushes.Black;
            Assert.True(PumpUntil(() => !ReferenceEquals(backdrop.Snapshot, first), 3000), "never retaken");
            Assert.Same(first, backdrop.FadingImage.Source); // mid cross-fade
            Assert.True(backdrop.SnapshotImage.Opacity < 1 || backdrop.SnapshotImage.Transitions != null);
            Assert.True(backdrop.IsOpaqueCover, "the cross-fade let the app under the sheet show");
            Assert.True(backdrop.IsTargetHostHidden);
            Assert.Equal(0, host.Opacity);

            Assert.True(PumpUntil(() => backdrop.FadingImage.Source is null, 2000), "old snapshot never let go");
            Assert.True(backdrop.IsOpaqueCover);
            Assert.True(backdrop.IsTargetHostHidden);
        }
        finally { Close(win, backdrop); }
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

    /// <summary>No stale old-theme backdrop is left: once the switch's retake has landed and
    /// faded in, what is shown is pixel for pixel a fresh snapshot of the new look.</summary>
    [AvaloniaFact]
    public void AfterTheSwitch_TheBackdropIsTheNewLook_LikeAFreshSnapshot()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var (win, rows, _, backdrop) = OpenSheet(Brushes.White);
        try
        {
            var first = backdrop.Snapshot!;
            backdrop.RefreshSoon();
            foreach (var row in rows.Children) ((Border)row).Background = new SolidColorBrush(Color.Parse("#1C2433"));
            Assert.True(PumpUntil(() => !ReferenceEquals(backdrop.Snapshot, first), 3000), "never retaken");
            Assert.True(PumpUntil(() => backdrop.FadingImage.Source is null, 2000));
            Assert.Same(backdrop.Snapshot, backdrop.SnapshotImage.Source);
            var shown = Pixels(backdrop.Snapshot!);

            var fresh = backdrop.CaptureAsync();
            Assert.True(PumpUntil(() => fresh.IsCompleted, 3000));
            Assert.True(fresh.Result);
            var again = Pixels(backdrop.Snapshot!);
            Assert.Equal(again.Length, shown.Length);
            var worst = 0;
            for (var i = 0; i < shown.Length; i++) worst = Math.Max(worst, Math.Abs(shown[i] - again[i]));
            Assert.True(worst <= 2, $"shown backdrop differs from a fresh one by up to {worst} levels");
            // And it is the new look: the dark fill, not the old white.
            long green = 0;
            for (var i = 1; i < shown.Length; i += 4) green += shown[i];
            Assert.True(green / (shown.Length / 4.0) < 80, $"mean green {green / (shown.Length / 4.0):F0} (old white look)");
        }
        finally { Close(win, backdrop); }
    }
}
