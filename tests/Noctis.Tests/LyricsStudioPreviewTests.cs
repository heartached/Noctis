using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio "Preview on lyrics page": unsaved Studio text replaces the current
/// track's lyrics in memory only, through the same parser as an .elrc/.lrc sidecar,
/// and the real lyrics come back on ClearPreview or a track change.
/// </summary>
public class LyricsStudioPreviewTests
{
    // ── Harness (mirrors LyricsSettingsToggleTests) ──

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

    private static (LyricsViewModel Vm, PlayerViewModel Player) MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromSeconds(30);
        vm.SetLyricsSurfaceVisible(true);
        return (vm, player);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    private const string RealLrc = "[00:01.00]Real first\n[00:04.00]Real second";
    private const string PreviewElrc =
        "[00:01.00]<00:01.00>Hello <00:01.50>big <00:02.00>world<00:02.50>\n" +
        "[00:04.00]<00:04.00>Second <00:04.60>line<00:05.20>";

    private static bool Shows(LyricsViewModel vm, string text) => vm.LyricLines.Any(l => l.Text == text);

    private static Track MakeTrack(string dir, string name, string syncedLyrics) => new()
    {
        Title = name, Artist = "Test",
        FilePath = Path.Combine(dir, name + ".mp3"),
        SyncedLyrics = syncedLyrics,
    };

    private static string MakeDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-studio-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaFact]
    public async Task ElrcPreview_ReplacesRealLyrics_WithWordTimings_AndClearRestores()
    {
        var dir = MakeDir();
        try
        {
            var (vm, player) = MakeViewModel();
            var track = MakeTrack(dir, "song", RealLrc);
            player.CurrentTrack = track;
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Real first"), "real lyrics to load");
            Assert.False(vm.IsPreviewActive);

            vm.ShowPreview(track, PreviewElrc);
            await WaitUntil(() => Shows(vm, "Hello big world"), "preview lines to replace the real ones");

            Assert.True(vm.IsPreviewActive);
            Assert.True(vm.IsSynced);
            Assert.False(Shows(vm, "Real first"));
            var line = vm.LyricLines.Single(l => l.Text == "Hello big world");
            Assert.Equal(TimeSpan.FromSeconds(1), line.Timestamp);
            Assert.NotNull(line.Words);
            Assert.Equal(new[] { "Hello", "big", "world" }, line.Words!.Select(w => w.Text.Trim()).ToArray());
            Assert.Equal(TimeSpan.FromMilliseconds(1500), line.Words[1].Start);
            Assert.Equal(TimeSpan.FromMilliseconds(2500), line.Words[2].End);

            // Word-level karaoke runs off the same active-line tracking as a saved .elrc.
            player.Position = TimeSpan.FromSeconds(4.2);
            await WaitUntil(() => vm.ActiveLineIndex >= 0 &&
                                  vm.LyricLines[vm.ActiveLineIndex].Text == "Second line", "active line to follow the preview");

            vm.ClearPreview();
            Assert.False(vm.IsPreviewActive);
            await WaitUntil(() => Shows(vm, "Real first"), "real lyrics to come back");
            Assert.False(Shows(vm, "Hello big world"));

            // Memory only: the track's own lyrics and the folder are untouched.
            Assert.Equal(RealLrc, track.SyncedLyrics);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task LrcPreview_RendersLineLevel()
    {
        var dir = MakeDir();
        try
        {
            var (vm, player) = MakeViewModel();
            var track = MakeTrack(dir, "song", RealLrc);
            player.CurrentTrack = track;
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Real first"), "real lyrics to load");

            vm.ShowPreview(track, "[00:02.00]Line only\n[00:05.00]Another");
            await WaitUntil(() => Shows(vm, "Line only"), "LRC preview to load");

            Assert.True(vm.IsPreviewActive);
            Assert.True(vm.IsSynced);
            var line = vm.LyricLines.Single(l => l.Text == "Line only");
            Assert.Equal(TimeSpan.FromSeconds(2), line.Timestamp);
            Assert.True(line.Words == null || line.Words.Count == 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task TrackChange_DropsThePreview_AndTheOldTrackComesBackReal()
    {
        var dir = MakeDir();
        try
        {
            var (vm, player) = MakeViewModel();
            var track = MakeTrack(dir, "song", RealLrc);
            var other = MakeTrack(dir, "other", "[00:01.00]Other track line");
            player.CurrentTrack = track;
            vm.EnsureLyricsForCurrentTrack();
            vm.ShowPreview(track, PreviewElrc);
            await WaitUntil(() => Shows(vm, "Hello big world"), "preview to show");

            player.CurrentTrack = other;
            Assert.False(vm.IsPreviewActive);
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Other track line"), "next track's lyrics");

            // Back on the previewed track: its real lyrics, not the dropped preview.
            player.CurrentTrack = track;
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Real first"), "real lyrics of the first track");
            Assert.False(Shows(vm, "Hello big world"));
            Assert.False(vm.IsPreviewActive);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task PreviewSetBeforeTheTrackStarts_ShowsOnceItIsCurrent()
    {
        var dir = MakeDir();
        try
        {
            var (vm, player) = MakeViewModel();
            var playing = MakeTrack(dir, "playing", "[00:01.00]Playing now");
            var target = MakeTrack(dir, "target", RealLrc);
            player.CurrentTrack = playing;
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Playing now"), "current track's lyrics");

            vm.ShowPreview(target, PreviewElrc);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Shows(vm, "Playing now"));   // not the current track yet — untouched
            Assert.False(vm.IsPreviewActive);

            player.CurrentTrack = target;
            vm.EnsureLyricsForCurrentTrack();
            await WaitUntil(() => Shows(vm, "Hello big world"), "preview once the target is current");
            Assert.True(vm.IsPreviewActive);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
