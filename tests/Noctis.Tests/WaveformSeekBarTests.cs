using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Waveform;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #93 waveform seek bar, headless: the control's line → waveform reveal and bar
/// layout, the island / mini player wiring (visible only with the setting on, the plain
/// line hidden then, the bar tracking the seek Slider's value and never taking its
/// input), and the PlayerViewModel glue that only ever shows the CURRENT track's
/// waveform. With NOCTIS_TEST_SKIA=1 the probe at the bottom also renders the island
/// and mini player to PNGs for an eye check (NOCTIS_RENDER_OUT = output folder,
/// NOCTIS_WAVEFORM_SAMPLE = an audio file to decode for a real shape).
/// </summary>
[Collection("MetadataServiceStatics")]
public class WaveformSeekBarTests
{
    private readonly ITestOutputHelper _out;
    public WaveformSeekBarTests(ITestOutputHelper output) => _out = output;

    private static WaveformData Synthetic(int buckets = 1024)
    {
        // Quiet intro, a verse, a louder chorus, a breakdown and an outro fade.
        var peaks = new byte[buckets];
        var rms = new byte[buckets];
        var rng = new Random(93);
        for (var i = 0; i < buckets; i++)
        {
            var t = i / (double)buckets;
            var env = t < 0.08 ? 0.15 + t * 3
                : t < 0.35 ? 0.55
                : t < 0.6 ? 0.95
                : t < 0.68 ? 0.25
                : t < 0.9 ? 0.9
                : 0.9 * (1 - (t - 0.9) / 0.1);
            var r = Math.Clamp(env * (0.8 + 0.2 * rng.NextDouble()), 0, 1);
            rms[i] = (byte)(r * 255);
            peaks[i] = (byte)(Math.Clamp(r * 1.4, 0, 1) * 255);
        }
        return new WaveformData(peaks, rms);
    }

    private static void Frame()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 3000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!done() && Environment.TickCount64 < end)
        {
            Frame();
            Thread.Sleep(8);
        }
    }

    // ── Control ──────────────────────────────────────────────

    [AvaloniaFact]
    public void DrawsTheLineUntilDataArrives_ThenRevealsWithoutALayoutChange()
    {
        var bar = new WaveformSeekBar { Width = 312, Height = 7, Value = 0.4 };
        var win = new Window { Width = 400, Height = 100, Content = bar };
        win.Show();
        try
        {
            Frame();
            var before = bar.Bounds;
            Assert.Equal(0, bar.RevealProgress);

            bar.Waveform = Synthetic();
            Frame();
            Assert.InRange(bar.RevealProgress, 0, 0.99); // animating, not snapped

            PumpUntil(() => bar.RevealProgress >= 1);
            Assert.Equal(1, bar.RevealProgress);
            Assert.Equal(before, bar.Bounds);

            // A new track: the old shape never lingers — straight back to the line.
            bar.Waveform = null;
            Assert.Equal(0, bar.RevealProgress);
            Assert.Equal(before, bar.Bounds);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void LaysBarsOverTheThumbTravel_AndNeverTakesInput()
    {
        var bar = new WaveformSeekBar { Width = 312, Height = 16 };
        var win = new Window { Width = 400, Height = 100, Content = bar };
        win.Show();
        try
        {
            Frame();
            // (312 − 2·6 inset + 1 gap) / (2 bar + 1 gap) = 100 bars.
            Assert.Equal(100, bar.BarCount);
            Assert.False(bar.IsHitTestVisible);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void OffScreen_TheWaveformLandsFinished()
    {
        var bar = new WaveformSeekBar { Waveform = Synthetic() };
        Assert.Equal(1, bar.RevealProgress);
    }

    // ── Island ───────────────────────────────────────────────

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
        {
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        }
    }

    [AvaloniaFact]
    public void Island_ShowsTheWaveformOnlyWithTheSetting_AndFollowsTheSeekSlider()
    {
        EnsureAppResources();
        var player = MakePlayer();
        player.CurrentTrack = new Track { Title = "Song", Artist = "Artist", FilePath = @"C:\m\song.flac" };
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        win.Show();
        try
        {
            Frame();
            var waveform = bar.FindControl<WaveformSeekBar>("SeekWaveform")!;
            var line = bar.FindControl<Border>("SeekTrackBackground")!;
            var fill = bar.FindControl<Border>("SeekTrackFill")!;
            var slider = bar.FindControl<Slider>("SeekSlider")!;

            // Off (default): the plain line only.
            Assert.False(waveform.IsVisible);
            Assert.True(line.IsVisible && fill.IsVisible);

            player.WaveformSeekBarEnabled = true;
            player.CurrentWaveform = Synthetic();
            Frame();
            Assert.True(waveform.IsEffectivelyVisible);
            Assert.False(line.IsVisible || fill.IsVisible);
            Assert.Same(player.CurrentWaveform, waveform.Waveform);

            // Same horizontal extent as the seek slider (so the split sits under its thumb),
            // centred on the old line's centre line (3.5px into the 6px row).
            Assert.Equal(slider.Bounds.Width, waveform.Bounds.Width, 3);
            Assert.Equal(7, waveform.Bounds.Height, 3);
            Assert.False(waveform.IsHitTestVisible);

            slider.Value = 0.3;
            Frame();
            Assert.Equal(0.3, waveform.Value, 6);
        }
        finally { win.Close(); }
    }

    // ── Mini player ──────────────────────────────────────────

    private static MiniPlayerViewModel MakeMiniViewModel(PlayerViewModel player)
    {
        var library = new FakeLibraryService();
        var lyrics = new LyricsViewModel(
            player, new NullLrcLib(), new NullNetEase(), new NullMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NullPlayHistory());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    [AvaloniaFact]
    public void MiniPlayer_EverySeekSliderHasAWaveformUnderIt()
    {
        EnsureAppResources();
        var player = MakePlayer();
        player.CurrentTrack = new Track { Title = "Song", Artist = "Artist", FilePath = @"C:\m\song.flac" };
        var vm = MakeMiniViewModel(player);
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 520 };
        win.Show();
        try
        {
            PumpUntil(() => false, 150);
            var names = new[] { "SeekSlider", "BarSeekSlider", "LargeSeekSlider", "LyricsSeekSlider", "PillSeekSlider", "SleeveSeekSlider" };
            foreach (var name in names)
            {
                var slider = win.FindControl<Slider>(name)!;
                var parent = (Panel)slider.GetVisualParent()!;
                var waveform = parent.Children.OfType<WaveformSeekBar>().Single();
                Assert.True(parent.Children.IndexOf(waveform) < parent.Children.IndexOf(slider), $"{name}: waveform must sit under the slider");
                Assert.False(waveform.IsVisible, $"{name}: shown with the setting off");
                Assert.False(slider.Classes.Contains("waveform"));
            }

            player.WaveformSeekBarEnabled = true;
            player.CurrentWaveform = Synthetic();
            PumpUntil(() => false, 150);

            var large = win.FindControl<Slider>("LargeSeekSlider")!;
            var largeWave = ((Panel)large.GetVisualParent()!).Children.OfType<WaveformSeekBar>().Single();
            Assert.True(large.Classes.Contains("waveform"));
            Assert.True(largeWave.IsEffectivelyVisible);
            Assert.Same(player.CurrentWaveform, largeWave.Waveform);
            Assert.Equal(large.Bounds, largeWave.Bounds);
            // The Fluent line is hidden (its borders go transparent); the thumb stays.
            var lineBorders = large.GetVisualDescendants().OfType<Avalonia.Controls.RepeatButton>()
                .SelectMany(r => r.GetVisualDescendants().OfType<Border>()).ToList();
            Assert.NotEmpty(lineBorders);
            Assert.All(lineBorders, b => Assert.Equal(0, b.Opacity));
        }
        finally { win.Close(); }
    }

    // ── PlayerViewModel glue ─────────────────────────────────

    private sealed class InstantDecoder : IWaveformDecoder
    {
        public readonly List<string> Decoded = new();
        public WaveformData? Decode(string path, CancellationToken ct)
        {
            lock (Decoded) Decoded.Add(Path.GetFileName(path));
            return Synthetic(64);
        }
    }

    [AvaloniaFact]
    public void Player_OnlyEverShowsTheCurrentTracksWaveform()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "waveform-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.flac");
        var b = Path.Combine(dir, "b.flac");
        File.WriteAllBytes(a, new byte[10]);
        File.WriteAllBytes(b, new byte[20]);
        var decoder = new InstantDecoder();
        using var service = new WaveformService(decoder, new WaveformCache(Path.Combine(dir, "cache")),
            new WaveformService.Options(TimeSpan.Zero, LowPriorityThread: false));
        try
        {
            var player = MakePlayer();
            player.SetWaveformService(service);
            player.CurrentTrack = new Track { FilePath = a };
            player.UpNext.Add(new Track { FilePath = b });

            // Off: nothing planned, nothing decoded.
            Assert.Empty(service.CurrentPlan);
            PumpUntil(() => false, 100);
            Assert.Empty(decoder.Decoded);
            Assert.Null(player.CurrentWaveform);

            player.WaveformSeekBarEnabled = true;
            Assert.Equal(new[] { a, b }, service.CurrentPlan);
            PumpUntil(() => player.CurrentWaveform != null);
            var shownForA = player.CurrentWaveform;
            Assert.NotNull(shownForA);

            // Next track: a's waveform is dropped at once, b's (precomputed) arrives.
            PumpUntil(() => decoder.Decoded.Count == 2);
            player.CurrentTrack = new Track { FilePath = b };
            Assert.Null(player.CurrentWaveform);
            PumpUntil(() => player.CurrentWaveform != null);
            Assert.NotNull(player.CurrentWaveform);
            Assert.NotSame(shownForA, player.CurrentWaveform);
            Assert.Equal(2, decoder.Decoded.Count); // b was not decoded twice

            // Off again: cleared and idle.
            player.WaveformSeekBarEnabled = false;
            Assert.Null(player.CurrentWaveform);
            Assert.Empty(service.CurrentPlan);
        }
        finally
        {
            service.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── PNG probe (NOCTIS_TEST_SKIA=1) ───────────────────────

    [AvaloniaFact]
    public void Probe_RenderIslandAndMiniPlayerToPng()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering

        var outDir = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Path.GetTempPath(), "NoctisTests", "waveform-png");
        Directory.CreateDirectory(outDir);

        var data = Synthetic();
        var sample = Environment.GetEnvironmentVariable("NOCTIS_WAVEFORM_SAMPLE");
        if (!string.IsNullOrWhiteSpace(sample) && File.Exists(sample))
        {
            data = new FfmpegWaveformDecoder(new AudioConverterService(() => string.Empty, new MetadataService()))
                .Decode(sample, CancellationToken.None) ?? data;
        }

        EnsureAppResources();
        var app = Application.Current!;
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            // App.axaml's own font resource, which Styles.axaml references.
            if (!app.Resources.ContainsKey("InterSemiBold"))
                app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
            });
        }

        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var accent = new SolidColorBrush(Color.Parse("#E74856")); // the default accent
            foreach (var (label, fraction, waveform) in new[] { ("line", 0.42, (WaveformData?)null), ("waveform", 0.42, data) })
            {
                var player = MakePlayer();
                player.CurrentTrack = new Track { Title = "Waveform seek bar", Artist = "GitHub #93", FilePath = @"C:\m\song.flac" };
                player.WaveformSeekBarEnabled = true;
                player.CurrentWaveform = waveform;
                var island = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
                var win = new Window
                {
                    Width = 720, Height = 90, Content = island,
                    RequestedThemeVariant = theme,
                    Background = theme == ThemeVariant.Dark ? new SolidColorBrush(Color.Parse("#252525")) : Brushes.White,
                };
                win.Resources["AccentColorBrush"] = accent;
                win.Resources["IslandSliderFilled"] = accent;
                win.Show();
                try
                {
                    island.FindControl<Slider>("SeekSlider")!.Value = fraction;
                    PumpUntil(() => island.FindControl<WaveformSeekBar>("SeekWaveform")!.RevealProgress >= 1 || waveform == null, 2000);
                    Frame();
                    var frame = win.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    var file = Path.Combine(outDir, $"island-{theme}-{label}.png");
                    frame!.Save(file);
                    _out.WriteLine(file);
                }
                finally { win.Close(); }
            }

            // Mini player: two classic forms (size-driven) and the two light designs.
            foreach (var (form, style, w, h, sliderName) in new[]
            {
                ("large", "Classic", 340.0, 520.0, "LargeSeekSlider"),
                ("card", "Classic", 360.0, 432.0, "SeekSlider"),
                ("pill", "Pill", 360.0, 150.0, "PillSeekSlider"),
                ("sleeve", "Sleeve", 320.0, 420.0, "SleeveSeekSlider"),
            })
            {
                var player = MakePlayer();
                player.CurrentTrack = new Track { Title = "Waveform seek bar", Artist = "GitHub #93", FilePath = @"C:\m\song.flac" };
                player.WaveformSeekBarEnabled = true;
                player.CurrentWaveform = data;
                var vm = MakeMiniViewModel(player);
                vm.Settings.MiniPlayerStyle = style;
                var win = new MiniPlayerWindow { DataContext = vm, Width = w, Height = h, RequestedThemeVariant = theme };
                win.Resources["AccentColorBrush"] = accent;
                win.Resources["IslandSliderFilled"] = accent;
                win.Show();
                try
                {
                    win.FindControl<Slider>(sliderName)!.Value = 0.42;
                    PumpUntil(() => false, 900);
                    var frame = win.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    var file = Path.Combine(outDir, $"mini-{form}-{theme}.png");
                    frame!.Save(file);
                    _out.WriteLine($"{file} form={vm.Form} size={win.Bounds.Size}");
                }
                finally { win.Close(); }
            }
        }
    }

    // ── Minimal service stubs for LyricsViewModel / SettingsViewModel ──

    private sealed class NullLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class NullNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class NullMetadata : IMetadataService
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

    private sealed class NullPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
