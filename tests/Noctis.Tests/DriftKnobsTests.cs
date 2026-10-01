using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Converters;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #111: Movement / Saturation / Blur knobs for the Drift flowing background.
/// 100% on every knob must be today's look exactly (same motion time, the same cached
/// pre-blurred bitmap), the knobs survive a restart, and every Drift surface picks them
/// up live — the motion through the animator's pace, blur/saturation baked into the
/// small pre-blurred bitmap once per knob change (nothing per frame).
/// </summary>
public class DriftKnobsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static PlayerViewModel CreatePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static LyricsViewModel CreateLyrics(PlayerViewModel player) => new(
        player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
        new TestPersistenceService(), new FakeLibraryService());

    private static async Task Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static double RotationOf(Visual layer) =>
        ((TransformGroup)layer.RenderTransform!).Children.OfType<RotateTransform>().Single().Angle;

    private static FlowingArtworkAnimator FlowOf(object view) =>
        (FlowingArtworkAnimator)view.GetType()
            .GetField("_flow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;

    // ── Settings ──

    [Fact]
    public void FreshInstall_EveryKnobIsTheStockLook()
    {
        var fresh = new AppSettings();
        Assert.Equal(100, AppSettings.LyricsDriftKnobDefault);
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, fresh.LyricsDriftMovement);
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, fresh.LyricsDriftSaturation);
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, fresh.LyricsDriftBlur);

        var player = CreatePlayer();
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, player.LyricsDriftMovement);
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, player.LyricsDriftSaturation);
        Assert.Equal(AppSettings.LyricsDriftKnobDefault, player.LyricsDriftBlur);
    }

    [AvaloniaFact]
    public async Task Knobs_ReachThePlayerLive_AndSurviveSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        var player = CreatePlayer();
        vm.SetPlayer(player);

        vm.LyricsDriftMovement = 0;
        vm.LyricsDriftSaturation = 150;
        vm.LyricsDriftBlur = 40;
        Assert.Equal(0, player.LyricsDriftMovement);
        Assert.Equal(150, player.LyricsDriftSaturation);
        Assert.Equal(40, player.LyricsDriftBlur);
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.Equal(0, reloaded.LyricsDriftMovement);
        Assert.Equal(150, reloaded.LyricsDriftSaturation);
        Assert.Equal(40, reloaded.LyricsDriftBlur);
    }

    [AvaloniaFact]
    public async Task Load_ClampsHandEditedKnobsToTheSliderRanges()
    {
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings
        {
            LyricsDriftMovement = 999,
            LyricsDriftSaturation = -5,
            LyricsDriftBlur = 500,
        });

        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(300, vm.LyricsDriftMovement);
        Assert.Equal(0, vm.LyricsDriftSaturation);
        Assert.Equal(200, vm.LyricsDriftBlur);
    }

    [AvaloniaFact]
    public async Task Sliders_ShowOnlyWhileADriftStyleIsPicked()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedFlowingOption = vm.FlowingOptions.First(o => o.Key == SettingsViewModel.FlowingOff);
        Assert.False(vm.IsDriftStyle);

        vm.SelectedFlowingOption = vm.FlowingOptions.First(o => o.Key == FlowingStyles.Drift);
        Assert.True(vm.IsDriftStyle);
        Assert.Contains(nameof(SettingsViewModel.IsDriftStyle), raised);

        vm.SelectedFlowingOption = vm.FlowingOptions.First(o => o.Key == FlowingStyles.DriftCalm);
        Assert.True(vm.IsDriftStyle);

        raised.Clear();
        vm.SelectedFlowingOption = vm.FlowingOptions.First(o => o.Key == FlowingStyles.Kawarp);
        Assert.False(vm.IsDriftStyle);
        Assert.True(vm.IsKawarpStyle);
        Assert.Contains(nameof(SettingsViewModel.IsDriftStyle), raised);
    }

    // ── Movement ──

    [Theory]
    [InlineData(0.0)]
    [InlineData(16.6667)]
    [InlineData(123456.789)]
    [InlineData(7_200_000.5)]
    public void MotionTime_AtStockMovement_IsTheClockExactly(double nowMs)
    {
        // Bit-identical, so the stock pose at any instant is what it was before the knob.
        Assert.Equal(nowMs, FlowingArtworkMotion.MotionTimeMs(nowMs, 1.0, 0.0));
        Assert.Equal(FlowingArtworkMotion.Evaluate(nowMs / 1000.0, 1280, 720, 0.3),
            FlowingArtworkMotion.Evaluate(FlowingArtworkMotion.MotionTimeMs(nowMs, 1.0, 0.0) / 1000.0, 1280, 720, 0.3));
    }

    [Fact]
    public void SpeedChange_KeepsThePose_AndOnlyChangesThePace()
    {
        const double now = 98_765.4;
        var half = FlowingArtworkMotion.RebaseOffsetMs(now, 1.0, 0.0, 0.5);
        // No jump at the switch…
        Assert.Equal(now, FlowingArtworkMotion.MotionTimeMs(now, 0.5, half), 6);
        // …then half the pace.
        Assert.Equal(now + 500, FlowingArtworkMotion.MotionTimeMs(now + 1000, 0.5, half), 6);

        // 0 holds the motion time — the layers stay put — and back to 1 resumes from there.
        var still = FlowingArtworkMotion.RebaseOffsetMs(now + 1000, 0.5, half, 0.0);
        Assert.Equal(now + 500, FlowingArtworkMotion.MotionTimeMs(now + 60_000, 0.0, still), 6);
        var again = FlowingArtworkMotion.RebaseOffsetMs(now + 60_000, 0.0, still, 1.0);
        Assert.Equal(now + 500, FlowingArtworkMotion.MotionTimeMs(now + 60_000, 1.0, again), 6);
        Assert.Equal(now + 1500, FlowingArtworkMotion.MotionTimeMs(now + 61_000, 1.0, again), 6);
    }

    [AvaloniaFact]
    public async Task Animator_AtMovementZero_HoldsTheLayersStill()
    {
        var backdrop = new Grid();
        var layer1 = new Border();
        var layer2 = new Border();
        var glow = new Border();
        backdrop.Children.Add(layer1);
        backdrop.Children.Add(layer2);
        backdrop.Children.Add(glow);
        var host = new Grid { Width = 200, Height = 120, Children = { backdrop } };
        var flow = new FlowingArtworkAnimator(host, backdrop, layer1, layer2, glow, () => new BeatContext(0, 0, false));
        var win = new Window { Width = 200, Height = 120, Content = host };
        win.Show();
        try
        {
            Assert.Equal(1.0, flow.Speed);
            flow.Speed = double.NaN; // a corrupt value falls back to the stock pace
            Assert.Equal(1.0, flow.Speed);
            flow.Speed = -2;
            Assert.Equal(0.0, flow.Speed);

            flow.Enabled = true;
            await Pump(60);
            Assert.True(flow.IsRunning, "the beat pulse still needs the frame clock at 0%");
            var held = RotationOf(layer1);
            await Pump(150);
            Assert.Equal(held, RotationOf(layer1));

            flow.Speed = 1;
            await Pump(150);
            Assert.NotEqual(held, RotationOf(layer1));
        }
        finally
        {
            flow.Dispose();
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task LyricsPage_MovementKnob_ReachesTheAnimatorLive()
    {
        var player = CreatePlayer();
        var vm = CreateLyrics(player);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        try
        {
            player.LyricsFlowingLightEnabled = true;
            await Pump(60);
            Assert.Equal(1.0, FlowOf(view).Speed);

            player.LyricsDriftMovement = 0;
            await Pump(40);
            Assert.Equal(0.0, FlowOf(view).Speed);
            var layer1 = view.FindControl<Image>("FlowLayer1")!;
            var held = RotationOf(layer1);
            await Pump(150);
            Assert.Equal(held, RotationOf(layer1));

            player.LyricsDriftMovement = 250;
            await Pump(20);
            Assert.Equal(2.5, FlowOf(view).Speed);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task LyricsPanel_MovementKnob_ReachesTheAnimatorLive()
    {
        var player = CreatePlayer();
        var vm = CreateLyrics(player);
        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 360, Height = 720, Content = view };
        win.Show();
        try
        {
            player.LyricsFlowingLightEnabled = true;
            player.LyricsDriftMovement = 50;
            await Pump(40);
            Assert.Equal(0.5, FlowOf(view).Speed);
        }
        finally
        {
            win.Close();
        }
    }

    // ── Saturation / Blur (baked into the pre-blurred bitmap) ──

    /// <summary>A small opaque premultiplied BGRA test card: hue bands with a soft gradient.</summary>
    private static byte[] Card(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var band = x * 3 / w;
                var shade = (byte)(80 + 140 * y / Math.Max(1, h - 1));
                var r = band == 0 ? shade : (byte)30;
                var g = band == 1 ? shade : (byte)40;
                var b = band == 2 ? shade : (byte)50;
                px[i] = (byte)(b * alpha / 255);
                px[i + 1] = (byte)(g * alpha / 255);
                px[i + 2] = (byte)(r * alpha / 255);
                px[i + 3] = alpha;
            }
        return px;
    }

    private static double Roughness(byte[] px, int w, int h)
    {
        // Mean SQUARED difference between horizontal neighbours: lower = smoother. (The plain
        // absolute sum is useless here — a blur spreads a step without changing its total.)
        double sum = 0;
        var n = 0;
        for (var y = 0; y < h; y++)
            for (var x = 1; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    double d = px[(y * w + x) * 4 + c] - px[(y * w + x - 1) * 4 + c];
                    sum += d * d;
                    n++;
                }
        return sum / n;
    }

    private static double Chroma(byte[] px)
    {
        double sum = 0;
        for (var i = 0; i < px.Length; i += 4)
            sum += Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) - Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
        return sum / (px.Length / 4);
    }

    [Fact]
    public void Tune_NoBlurAndStockSaturation_IsAnUntouchedCopy()
    {
        var card = Card(48, 32);
        var copy = PreBlurredArtworkConverter.Tune(card, 48, 32, 0, 100);
        Assert.NotSame(card, copy);
        Assert.Equal(card, copy);
    }

    [Fact]
    public void Tune_BlurKnob_SoftensMoreAsItRises()
    {
        var card = Card(48, 32);
        var none = Roughness(PreBlurredArtworkConverter.Tune(card, 48, 32, 0, 100), 48, 32);
        var stock = Roughness(PreBlurredArtworkConverter.Tune(card, 48, 32, 100, 100), 48, 32);
        var heavy = Roughness(PreBlurredArtworkConverter.Tune(card, 48, 32, 200, 100), 48, 32);
        Assert.True(stock < none, $"stock {stock} vs none {none}");
        Assert.True(heavy < stock, $"heavy {heavy} vs stock {stock}");
    }

    [Fact]
    public void Tune_SaturationKnob_GreysOutAtZero_AndDeepensAbove100()
    {
        var card = Card(48, 32);
        var grey = PreBlurredArtworkConverter.Tune(card, 48, 32, 100, 0);
        for (var i = 0; i < grey.Length; i += 4)
        {
            Assert.Equal(grey[i], grey[i + 1]);
            Assert.Equal(grey[i + 1], grey[i + 2]);
        }
        var stock = Chroma(PreBlurredArtworkConverter.Tune(card, 48, 32, 100, 100));
        var rich = Chroma(PreBlurredArtworkConverter.Tune(card, 48, 32, 100, 180));
        Assert.True(rich > stock, $"rich {rich} vs stock {stock}");
    }

    [Fact]
    public void Tune_OversaturatedTranslucentPixels_StayValidPremultiplied()
    {
        var card = Card(24, 16, alpha: 120);
        var rich = PreBlurredArtworkConverter.Tune(card, 24, 16, 0, 200);
        for (var i = 0; i < rich.Length; i += 4)
        {
            Assert.InRange(rich[i], 0, rich[i + 3]);
            Assert.InRange(rich[i + 1], 0, rich[i + 3]);
            Assert.InRange(rich[i + 2], 0, rich[i + 3]);
        }
    }

    private static Bitmap Art(int size)
    {
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var px = Card(size, size);
        using (var fb = bmp.Lock())
            for (var y = 0; y < size; y++)
                Marshal.Copy(px, y * size * 4, fb.Address + y * fb.RowBytes, size * 4);
        return bmp;
    }

    private static object? Multi(PreBlurredArtworkConverter conv, Bitmap? art, bool drift, int blur, int saturation)
        => conv.Convert(new object?[] { art, drift, blur, saturation }, typeof(IImage), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Converter_StockKnobsOrDriftOff_HandBackThePlainCachedPreBlur()
    {
        var art = Art(64);
        var conv = new PreBlurredArtworkConverter();
        var plain = conv.Convert(art, typeof(IImage), null, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<Bitmap>(plain);

        // Today's look, byte for byte: the very same cached bitmap.
        Assert.Same(plain, Multi(conv, art, drift: true, 100, 100));
        // Knobs only apply while Drift draws; the static artwork background keeps the stock blur.
        Assert.Same(plain, Multi(conv, art, drift: false, 40, 150));

        var tuned = Multi(conv, art, drift: true, 40, 150);
        Assert.IsAssignableFrom<Bitmap>(tuned);
        Assert.NotSame(plain, tuned);
        // Every image on every surface asks with the same knobs: one bitmap, not a rebuild each.
        Assert.Same(tuned, Multi(conv, art, drift: true, 40, 150));
        Assert.Same(tuned, Multi(new PreBlurredArtworkConverter(), art, drift: true, 40, 150));
        // Moving a knob rebuilds once.
        var moved = Multi(conv, art, drift: true, 45, 150);
        Assert.NotSame(tuned, moved);

        Assert.Null(Multi(conv, null, drift: true, 40, 150));
    }

    [AvaloniaFact]
    public async Task LyricsPage_BlurAndSaturationKnobs_ReachTheBackdropAndBothDriftCopies()
    {
        var player = CreatePlayer();
        var vm = CreateLyrics(player);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        try
        {
            player.AlbumArt = Art(96);
            player.LyricsFlowingLightEnabled = true;
            await Pump(40);
            var baseArt = view.FindControl<Image>("FlowBaseArt")!;
            var layer1 = view.FindControl<Image>("FlowLayer1")!;
            var layer2 = view.FindControl<Image>("FlowLayer2")!;
            var plain = new PreBlurredArtworkConverter().Convert(player.AlbumArt, typeof(IImage), null, CultureInfo.InvariantCulture);
            Assert.Same(plain, baseArt.Source);

            player.LyricsDriftBlur = 30;
            player.LyricsDriftSaturation = 160;
            await Pump(40);
            Assert.NotNull(baseArt.Source);
            Assert.NotSame(plain, baseArt.Source);
            Assert.Same(baseArt.Source, layer1.Source);
            Assert.Same(baseArt.Source, layer2.Source);

            // Flowing off: the static artwork background is the stock pre-blur again.
            player.LyricsFlowingLightEnabled = false;
            await Pump(40);
            Assert.Same(plain, baseArt.Source);
        }
        finally
        {
            win.Close();
        }
    }
}
