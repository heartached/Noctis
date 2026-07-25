using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics engine resolves the active line from a cursor into the line list.
/// Forward motion has always been free, but backward motion used to depend on a
/// single sample landing more than 750ms behind the previous one — which a timeline
/// drag never produces, because the sync timer samples every 100ms and the word
/// clock every rendered frame. Dragging backwards therefore left the cursor parked
/// on a later line and the lyrics frozen until release. These pin both directions.
/// </summary>
public class LyricsBackwardSeekTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds)
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

    private const int LineCount = 31;
    private const int LineSpacingSeconds = 3;

    /// <summary>Synced LRC with a line every 3s from 0:00 to 1:30.</summary>
    private static string MakeLrc() => string.Join("\n",
        Enumerable.Range(0, LineCount).Select(i =>
        {
            var t = TimeSpan.FromSeconds(i * LineSpacingSeconds);
            return $"[{t.Minutes:00}:{t.Seconds:00}.00]Line {i}";
        }));

    /// <summary>Loads synced lyrics through the real embedded-tag path (the sidecar
    /// probes miss because FilePath does not exist), then hands back the pair.</summary>
    private static async Task<(LyricsViewModel Vm, PlayerViewModel Player)> MountSyncedLyrics()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        player.Duration = TimeSpan.FromSeconds(LineCount * LineSpacingSeconds);
        player.CurrentTrack = new Track
        {
            Title = "Backward Seek",
            Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-backward-seek-no-such-file.mp3"),
            SyncedLyrics = MakeLrc(),
        };

        vm.EnsureLyricsForCurrentTrack();

        // The local probe runs off the UI thread and posts its apply back. Budgeted in
        // wall-clock rather than iterations: under a full-suite run the thread pool is
        // busy and a fixed iteration count timed out, failing the harness assert below.
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !vm.IsSynced)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(vm.IsSynced, "harness failed to load synced lyrics");
        return (vm, player);
    }

    /// <summary>Timestamp of the line the view would highlight, or null if none is active.</summary>
    private static TimeSpan? ActiveTimestamp(LyricsViewModel vm) =>
        vm.ActiveLineIndex >= 0 && vm.ActiveLineIndex < vm.LyricLines.Count
            ? vm.LyricLines[vm.ActiveLineIndex].Timestamp
            : null;

    /// <summary>Feeds the position in 100ms steps, the cadence the sync timer delivers
    /// while the user drags the timeline slider (the word clock is finer still).</summary>
    private static void Drag(PlayerViewModel player, int fromMs, int toMs)
    {
        var step = fromMs <= toMs ? 100 : -100;
        for (var ms = fromMs; step > 0 ? ms <= toMs : ms >= toMs; ms += step)
            player.Position = TimeSpan.FromMilliseconds(ms);
    }

    // ── Tests ──

    [AvaloniaFact]
    public async Task DraggingBackwards_FollowsTheLyricsWhileDragging()
    {
        var (vm, player) = await MountSyncedLyrics();

        player.Position = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(60), ActiveTimestamp(vm));

        Drag(player, 60_000, 15_000);

        Assert.Equal(TimeSpan.FromSeconds(15), ActiveTimestamp(vm));
    }

    [AvaloniaFact]
    public async Task DraggingForwards_FollowsTheLyricsWhileDragging()
    {
        var (vm, player) = await MountSyncedLyrics();

        Drag(player, 0, 45_000);

        Assert.Equal(TimeSpan.FromSeconds(45), ActiveTimestamp(vm));
    }

    [AvaloniaFact]
    public async Task SmallBackwardStep_ResolvesOnTheSameSample()
    {
        var (vm, player) = await MountSyncedLyrics();

        player.Position = TimeSpan.FromSeconds(30);
        Assert.Equal(TimeSpan.FromSeconds(30), ActiveTimestamp(vm));

        // One step back, well inside the old 750ms seek-backwards threshold.
        player.Position = TimeSpan.FromMilliseconds(29_500);

        Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(vm));
    }

    [AvaloniaFact]
    public async Task HoldingTheSliderWhilePlaying_DoesNotDriftTheLyricsForward()
    {
        var (vm, player) = await MountSyncedLyrics();

        // Playing: the extrapolating clock is live, which is the real condition while
        // dragging (playback is not paused for a scrub).
        player.State = PlaybackState.Playing;
        player.BeginSeek();
        try
        {
            // 29s sits inside the line that starts at 27s — the next begins at 30s.
            player.Position = TimeSpan.FromSeconds(29);
            await PumpDispatcher(150);
            Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(vm));

            // Hold the slider still. Nothing is playing forward, so the active line must
            // not advance; extrapolation used to march it into the 30s line.
            await PumpDispatcher(1400);

            Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(vm));
        }
        finally
        {
            player.EndSeek();
        }
    }

    /// <summary>Runs the dispatcher for a wall-clock window so the 100ms sync timer ticks.</summary>
    private static async Task PumpDispatcher(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task LargeBackwardJump_StillResolvesImmediately()
    {
        var (vm, player) = await MountSyncedLyrics();

        player.Position = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(60), ActiveTimestamp(vm));

        // Clicking a lyric line far above — the path that already worked.
        player.Position = TimeSpan.FromSeconds(9);

        Assert.Equal(TimeSpan.FromSeconds(9), ActiveTimestamp(vm));
    }
}
