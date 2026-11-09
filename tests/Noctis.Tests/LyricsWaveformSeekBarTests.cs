using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// GitHub #181: Settings › Lyrics › Waveform Progress Bar turns the lyrics page's progress
/// bar into the island's waveform (same <see cref="WaveformSeekBar"/>, same
/// <see cref="PlayerViewModel.CurrentWaveform"/>, no second pipeline). Off by default and
/// then exactly today's bar; on, the waveform replaces the 7px track with the same thumb
/// travel, so a click still seeks through the lyrics page's Slider.
/// </summary>
[Collection("MetadataServiceStatics")]
public class LyricsWaveformSeekBarTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-waveform-" + Guid.NewGuid().ToString("N"));

    public LyricsWaveformSeekBarTests() => Directory.CreateDirectory(_root);

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

    private sealed class InstantDecoder : IWaveformDecoder
    {
        public readonly List<string> Decoded = new();
        public WaveformData? Decode(string path, CancellationToken ct)
        {
            lock (Decoded) Decoded.Add(Path.GetFileName(path));
            return Synthetic(64);
        }
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    // No-lyrics centring off: its slide moves the whole left column (timeline included)
    // for a moment after mount, which would make pointer positions racy.
    private static PlayerViewModel CreatePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService())
    {
        LyricsCenterWhenNoLyrics = false,
    };

    private static LyricsViewModel CreateLyrics(PlayerViewModel player) => new(
        player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
        new TestPersistenceService(), new FakeLibraryService())
    {
        ShowLyricsSearchDialog = _ => Task.CompletedTask,
        ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
    };

    private static WaveformData Synthetic(int buckets = 512)
    {
        var peaks = new byte[buckets];
        var rms = new byte[buckets];
        for (var i = 0; i < buckets; i++)
        {
            var r = 0.2 + 0.7 * Math.Abs(Math.Sin(i / 17.0));
            rms[i] = (byte)(r * 255);
            peaks[i] = (byte)(Math.Min(1, r * 1.3) * 255);
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
        Frame();
    }

    private Track LocalTrack(string name = "song.flac") => new()
    {
        Title = "Song",
        Artist = "Artist",
        Duration = TimeSpan.FromSeconds(200),
        FilePath = Path.Combine(_root, name),
        SourceType = SourceType.Local,
    };

    // ── Setting ──────────────────────────────────────────────

    [Fact]
    public void FreshInstall_IsOff()
    {
        Assert.False(new AppSettings().LyricsWaveformSeekBarEnabled);
        Assert.False(CreatePlayer().LyricsWaveformSeekBarEnabled);
    }

    [AvaloniaFact]
    public async Task Setting_ReachesThePlayerLive_AndSurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.LyricsWaveformSeekBarEnabled);
        var player = CreatePlayer();
        vm.SetPlayer(player);

        vm.LyricsWaveformSeekBarEnabled = true;
        Assert.True(player.LyricsWaveformSeekBarEnabled);
        // The island's own setting is a separate choice.
        Assert.False(player.WaveformSeekBarEnabled);
        await vm.SaveAsync();

        var saved = await new PersistenceService(_root).LoadSettingsAsync();
        Assert.True(saved.LyricsWaveformSeekBarEnabled);
        Assert.False(saved.WaveformSeekBarEnabled);

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.LyricsWaveformSeekBarEnabled);

        vm.LyricsWaveformSeekBarEnabled = false;
        Assert.False(player.LyricsWaveformSeekBarEnabled);
    }

    // ── Lyrics page ──────────────────────────────────────────

    [AvaloniaFact]
    public void LyricsPage_ShowsTheWaveformOnlyWithTheSetting_AndFollowsTheSlider()
    {
        var player = CreatePlayer();
        player.CurrentTrack = LocalTrack();
        var view = new LyricsView { DataContext = CreateLyrics(player) };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        try
        {
            PumpUntil(() => false, 100);
            var waveform = view.FindControl<WaveformSeekBar>("LyricsTimelineWaveform")!;
            var track = view.FindControl<Border>("LyricsTimelineTrackBackground")!;
            var fill = view.FindControl<Border>("LyricsTimelineTrackFill")!;
            var thumb = view.FindControl<Border>("LyricsTimelineThumb")!;
            var slider = view.FindControl<Slider>("LyricsTimelineSlider")!;

            // Off (default): exactly today's bar.
            Assert.False(waveform.IsVisible);
            Assert.True(track.IsEffectivelyVisible && fill.IsEffectivelyVisible && thumb.IsEffectivelyVisible);

            // On, waveform not worked out yet: the control stands in, drawing the plain line.
            player.LyricsWaveformSeekBarEnabled = true;
            Frame();
            Assert.True(waveform.IsEffectivelyVisible);
            Assert.False(track.IsVisible || fill.IsVisible);
            Assert.True(thumb.IsEffectivelyVisible);
            Assert.Equal(0, waveform.RevealProgress);

            player.CurrentWaveform = Synthetic();
            Frame();
            Assert.Same(player.CurrentWaveform, waveform.Waveform);
            PumpUntil(() => waveform.RevealProgress >= 1);
            Assert.Equal(1, waveform.RevealProgress);

            // Same horizontal extent as the seek Slider (the split sits under its thumb),
            // never taking the Slider's input.
            Assert.Equal(slider.Bounds.Width, waveform.Bounds.Width, 3);
            Assert.Equal(24, waveform.Bounds.Height, 3);
            Assert.False(waveform.IsHitTestVisible);

            slider.Value = 0.3;
            Frame();
            Assert.Equal(0.3, waveform.Value, 6);

            // The island inside the lyrics page keeps its own setting (off here).
            var island = view.FindControl<PlaybackBarView>("LyricsPlaybackBar")!;
            Assert.False(island.FindControl<WaveformSeekBar>("SeekWaveform")!.IsVisible);

            // Off again: back to today's bar.
            player.LyricsWaveformSeekBarEnabled = false;
            Frame();
            Assert.False(waveform.IsVisible);
            Assert.True(track.IsEffectivelyVisible && fill.IsEffectivelyVisible);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void LyricsPage_ClickOnTheWaveform_SeeksToThatPoint()
    {
        var player = CreatePlayer();
        player.CurrentTrack = LocalTrack();
        player.Duration = TimeSpan.FromSeconds(200);
        player.LyricsWaveformSeekBarEnabled = true;
        player.CurrentWaveform = Synthetic();
        var view = new LyricsView { DataContext = CreateLyrics(player) };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show();
        try
        {
            PumpUntil(() => false, 100);
            var waveform = view.FindControl<WaveformSeekBar>("LyricsTimelineWaveform")!;
            var slider = view.FindControl<Slider>("LyricsTimelineSlider")!;
            Assert.True(waveform.IsEffectivelyVisible);
            Assert.True(slider.Bounds.Width > 100);

            var seeks = new List<TimeSpan>();
            player.Seeked += (_, t) => seeks.Add(t);

            // 75% along the waveform's bar span (8px thumb-radius inset each side).
            var span = waveform.Bounds.Width - 2 * waveform.TrackInset;
            var localX = waveform.TrackInset + 0.75 * span;
            var at = waveform.TranslatePoint(new Point(localX, waveform.Bounds.Height / 2), win)!.Value;
            win.MouseDown(at, MouseButton.Left);
            Frame();
            Assert.Equal(0.75, slider.Value, 2);
            Assert.Equal(0.75, waveform.Value, 2);
            win.MouseUp(at, MouseButton.Left);

            PumpUntil(() => seeks.Count > 0, 2000);
            Assert.Single(seeks);
            Assert.Equal(150, seeks[0].TotalSeconds, 0);
        }
        finally { win.Close(); }
    }

    // ── PlayerViewModel glue ─────────────────────────────────

    [AvaloniaFact]
    public void Player_LyricsSettingAloneDecodesTheCurrentTrack_StreamsGetNone()
    {
        var a = Path.Combine(_root, "a.flac");
        File.WriteAllBytes(a, new byte[10]);
        var decoder = new InstantDecoder();
        using var service = new WaveformService(decoder, new WaveformCache(Path.Combine(_root, "cache")),
            new WaveformService.Options(TimeSpan.Zero, LowPriorityThread: false));
        var player = CreatePlayer();
        player.SetWaveformService(service);
        player.CurrentTrack = new Track { FilePath = a, SourceType = SourceType.Local };

        Assert.Empty(service.CurrentPlan);
        player.LyricsWaveformSeekBarEnabled = true;
        Assert.Equal(new[] { a }, service.CurrentPlan);
        PumpUntil(() => player.CurrentWaveform != null);
        Assert.NotNull(player.CurrentWaveform);
        Assert.False(player.WaveformSeekBarEnabled);

        // A streamed track has no waveform: nothing planned, the bar stays plain.
        player.CurrentTrack = new Track { FilePath = "https://example.invalid/stream/1", SourceType = SourceType.Jellyfin };
        Assert.Null(player.CurrentWaveform);
        Assert.Empty(service.CurrentPlan);

        // With the island setting on, turning the lyrics one off keeps decoding.
        player.CurrentTrack = new Track { FilePath = a, SourceType = SourceType.Local };
        player.WaveformSeekBarEnabled = true;
        player.LyricsWaveformSeekBarEnabled = false;
        Assert.Equal(new[] { a }, service.CurrentPlan);

        // Both off: cleared and idle.
        player.WaveformSeekBarEnabled = false;
        Assert.Null(player.CurrentWaveform);
        Assert.Empty(service.CurrentPlan);
    }

    // ── PNG probe (NOCTIS_TEST_SKIA=1, NOCTIS_RENDER_OUT = output folder) ──

    [AvaloniaFact]
    public void Probe_RenderLyricsTimelineToPng()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering

        var outDir = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-waveform-png");
        Directory.CreateDirectory(outDir);

        foreach (var (label, on, data) in new[] { ("off", false, (WaveformData?)null), ("loading", true, null), ("waveform", true, Synthetic(1024)) })
        {
            var player = CreatePlayer();
            player.CurrentTrack = LocalTrack();
            player.LyricsWaveformSeekBarEnabled = on;
            player.CurrentWaveform = data;
            var view = new LyricsView { DataContext = CreateLyrics(player) };
            var win = new Window { Width = 1200, Height = 800, Content = view };
            win.Show();
            try
            {
                view.FindControl<Slider>("LyricsTimelineSlider")!.Value = 0.42;
                PumpUntil(() => false, 600);
                var frame = win.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame!.Save(Path.Combine(outDir, $"lyrics-timeline-{label}.png"));
            }
            finally { win.Close(); }
        }
    }
}
