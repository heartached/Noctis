using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Settings → Background Blur, a smooth slider (like the opacity ones) for the blur
/// behind pop-ups (the metadata editor) and the Settings sheet, all the way left = off. The
/// setting persists and clamps, drives BackdropSnapshot's (fractional) radius, and an open
/// Settings sheet follows it live: it re-blurs the snapshot it already has as the thumb moves,
/// and fades to the plain dim at Off and back.
/// </summary>
public class BackgroundBlurSettingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        BackdropSnapshot.BlurRadius = BackdropSnapshot.DefaultBlurRadius; // process-wide
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
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

    /// <summary>Owner 10-08: the default is 10%, and the backdrop starts at that radius before
    /// the settings have loaded.</summary>
    [Fact]
    public void Default_IsTenPercent_AndTheBackdropStartsThere()
    {
        Assert.Equal(0.1, new AppSettings().BackgroundBlurAmount, 6);
        Assert.Equal(BackdropSnapshot.DefaultBlurRadius,
            SettingsViewModel.BackdropRadiusFor(new AppSettings().BackgroundBlurAmount), 6);
    }

    [AvaloniaFact]
    public async Task Setting_LoadsSavesClamps_AndDrivesTheBackdropRadius()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { BackgroundBlurAmount = 0.73 });

        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Assert.Equal(0.73, vm.BackgroundBlurAmount, 6);
        Assert.Equal(7.3, BackdropSnapshot.BlurRadius, 6); // fractional: the slider is continuous
        Assert.True(BackdropSnapshot.IsBlurEnabled);
        Assert.Equal(0.73.ToString("P0"), vm.BackgroundBlurLabel);

        vm.BackgroundBlurAmount = 0; // thumb all the way left
        Assert.Equal(0, BackdropSnapshot.BlurRadius);
        Assert.False(BackdropSnapshot.IsBlurEnabled);
        Assert.Equal("Off", vm.BackgroundBlurLabel);
        await vm.SaveAsync();
        Assert.Equal(0, (await persistence.LoadSettingsAsync()).BackgroundBlurAmount);

        vm.BackgroundBlurAmount = 3;
        Assert.Equal(1, vm.BackgroundBlurAmount);
        Assert.Equal(BackdropSnapshot.MaxBlurRadius, BackdropSnapshot.BlurRadius);
    }

    [AvaloniaFact]
    public async Task Off_TakesNoSnapshot()
    {
        var win = new Window { Width = 200, Height = 150, Background = Brushes.Black };
        win.Show();
        try
        {
            BackdropSnapshot.BlurRadius = 0;
            Assert.Null(await BackdropSnapshot.CaptureBlurredAsync(win, win, "test"));
        }
        finally { win.Close(); }
    }

    /// <summary>A sharp 1px white line on black, blurred at radius r: the centre value.</summary>
    private static byte CentreAfterBlur(double radius)
    {
        const int w = 41, h = 1;
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4) px[i + 3] = 255;
        px[20 * 4] = px[20 * 4 + 1] = px[20 * 4 + 2] = 255;
        BackdropSnapshot.BoxBlurSmooth(px, w, h, radius, BackdropSnapshot.BlurPasses);
        return px[20 * 4];
    }

    [Fact]
    public void SmoothBlur_MatchesWholeRadii_AndGrowsSteadilyBetweenThem()
    {
        // Whole radii are byte for byte the reference integer blur, on a random 2D image (both
        // directions, all four channels, edges clamped the same way).
        const int iw = 37, ih = 23;
        var image = new byte[iw * ih * 4];
        new Random(7).NextBytes(image);
        foreach (var r in new[] { 1, 4, 7, 10 })
        {
            var a = (byte[])image.Clone();
            var b = (byte[])image.Clone();
            BackdropSnapshot.BoxBlur(a, iw, ih, r, BackdropSnapshot.BlurPasses);
            BackdropSnapshot.BoxBlurSmooth(b, iw, ih, r, BackdropSnapshot.BlurPasses);
            Assert.Equal(a, b);
        }
        // Between them the line spreads a little more with every step (no jump at a whole
        // radius). Each of the three passes rounds to a byte, so a step may wobble by one level.
        var previous = CentreAfterBlur(1);
        for (var r = 1.1; r <= 6.0001; r += 0.1)
        {
            var now = CentreAfterBlur(r);
            Assert.True(now <= previous + 1, $"radius {r:F1}: centre {now} > {previous}");
            Assert.True(previous - now <= 12, $"radius {r:F1}: jumped {previous} -> {now}");
            previous = now;
        }
        for (var r = 1; r < 6; r++)
            Assert.True(CentreAfterBlur(r + 1) < CentreAfterBlur(r), $"radius {r + 1} not wider than {r}");
        Assert.Equal(255, CentreAfterBlur(0)); // 0 leaves it sharp
    }

    /// <summary>A live re-blur (one per slider step while dragging) of a full-window snapshot:
    /// quarter of 1920×1080 and of 2560×1440, at a fractional radius.</summary>
    [Theory]
    [InlineData(480, 270)]
    [InlineData(640, 360)]
    public void Reblur_IsFastEnoughToFollowADrag(int w, int h)
    {
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        var work = new byte[px.Length];
        BackdropSnapshot.BoxBlurSmooth((byte[])px.Clone(), w, h, 7.3, BackdropSnapshot.BlurPasses); // warm up
        var sw = Stopwatch.StartNew();
        const int runs = 10;
        for (var i = 0; i < runs; i++)
        {
            Buffer.BlockCopy(px, 0, work, 0, px.Length);
            BackdropSnapshot.BoxBlurSmooth(work, w, h, 7.3, BackdropSnapshot.BlurPasses);
        }
        var ms = sw.Elapsed.TotalMilliseconds / runs;
        _output.WriteLine($"{w}x{h}: {ms:F1} ms per re-blur");
        Assert.True(ms < 100, $"{w}x{h}: {ms:F1} ms per re-blur");
    }

    private readonly ITestOutputHelper _output;
    public BackgroundBlurSettingTests(ITestOutputHelper output) => _output = output;

    private static (Window win, BlurredBackdrop backdrop) OpenSheet()
    {
        var layer = new Panel { Children = { new Border { Height = 300, Background = Brushes.SteelBlue } } };
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer };
        var win = new Window
        {
            Width = 600, Height = 400, RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Black,
            Content = new Panel { Children = { layer, backdrop } },
        };
        win.Show();
        PumpUntil(() => false, 60);
        backdrop.IsVisible = true;
        var prepare = backdrop.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted));
        backdrop.Opacity = 1;
        return (win, backdrop);
    }

    private static void CloseSheet(Window win, BlurredBackdrop backdrop)
    {
        backdrop.Opacity = 0;
        backdrop.IsVisible = false;
        backdrop.Release();
        win.Close();
    }

    [AvaloniaFact]
    public void Dragging_ReblursTheSameSnapshot_FollowingTheThumb()
    {
        var (win, backdrop) = OpenSheet();
        try
        {
            Assert.True(PumpUntil(() => backdrop.HasSnapshot), "no snapshot at the default strength");
            var snapshot = backdrop.Snapshot;
            Assert.Equal(BackdropSnapshot.DefaultBlurRadius, backdrop.ShownRadius, 6);

            // A drag: many small steps in a burst. No new snapshot and no wait for the thumb to
            // rest; the shown blur ends at the last value with at most one re-blur per step.
            for (var r = 4.1; r <= 7.0001; r += 0.1) BackdropSnapshot.BlurRadius = r;
            Assert.False(backdrop.IsRefreshPending);
            Assert.True(PumpUntil(() => Math.Abs(backdrop.ShownRadius - 7.0) < 1e-6), $"shown {backdrop.ShownRadius}");
            Assert.Same(snapshot, backdrop.Snapshot);
            Assert.Same(snapshot, backdrop.SnapshotImage.Source);
            Assert.InRange(backdrop.ReblurCount, 1, 30);
        }
        finally { CloseSheet(win, backdrop); }
    }

    [AvaloniaFact]
    public void Off_FadesToThePlainDim_AndBack_KeepingTheSnapshot()
    {
        var (win, backdrop) = OpenSheet();
        try
        {
            Assert.True(PumpUntil(() => backdrop.HasSnapshot));
            var snapshot = backdrop.Snapshot;

            BackdropSnapshot.BlurRadius = 0;
            Assert.NotNull(backdrop.SnapshotImage.Transitions); // a fade, not a cut
            Assert.True(PumpUntil(() => backdrop.SnapshotImage.Opacity < 0.01 && backdrop.DimLayer.Opacity > 0.99),
                $"image {backdrop.SnapshotImage.Opacity} dim {backdrop.DimLayer.Opacity}");

            BackdropSnapshot.BlurRadius = 6;
            Assert.True(PumpUntil(() => backdrop.SnapshotImage.Opacity > 0.99
                    && Math.Abs(backdrop.DimLayer.Opacity - backdrop.BlurDimOpacity) < 0.01),
                $"image {backdrop.SnapshotImage.Opacity} dim {backdrop.DimLayer.Opacity}");
            Assert.True(PumpUntil(() => Math.Abs(backdrop.ShownRadius - 6) < 1e-6));
            Assert.Same(snapshot, backdrop.Snapshot); // no new snapshot for the round trip
        }
        finally { CloseSheet(win, backdrop); }
    }

    [AvaloniaFact]
    public void OpenedWithBlurOff_TurningItOn_TakesOneAndFadesIn()
    {
        BackdropSnapshot.BlurRadius = 0;
        var (win, backdrop) = OpenSheet();
        try
        {
            Assert.False(backdrop.HasSnapshot);
            Assert.Equal(1, backdrop.DimLayer.Opacity, 3);

            BackdropSnapshot.BlurRadius = 3.5;
            Assert.True(PumpUntil(() => backdrop.HasSnapshot), "never taken after turning blur on");
            Assert.Equal(3.5, backdrop.ShownRadius, 6);
            Assert.True(PumpUntil(() => backdrop.SnapshotImage.Opacity > 0.99));
        }
        finally { CloseSheet(win, backdrop); }
    }

    [AvaloniaFact]
    public void ClosedSheet_IgnoresTheSlider()
    {
        var layer = new Panel();
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer };
        var win = new Window { Width = 300, Height = 200, Content = new Panel { Children = { layer, backdrop } } };
        win.Show();
        try
        {
            BackdropSnapshot.BlurRadius = 0;
            BackdropSnapshot.BlurRadius = 6;
            PumpUntil(() => false, 300);
            Assert.False(backdrop.HasSnapshot);
            Assert.Equal(0, backdrop.ReblurCount);
        }
        finally { win.Close(); }
    }
}
