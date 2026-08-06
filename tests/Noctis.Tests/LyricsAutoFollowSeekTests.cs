using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A committed seek is an explicit "go here" and must resume lyrics auto-follow,
/// exactly like clicking a lyric line does (see LyricsViewModel.SeekToLine). The
/// timeline path used to leave IsAutoFollowPaused=true: after a wheel scroll, a
/// timeline seek moved the active line off-screen, the ±9 dim window faded every
/// visible line to opacity 0, and the page sat blank until the 5s auto-resume plus
/// the next line boundary.
/// </summary>
public class LyricsAutoFollowSeekTests
{
    // ── Harness (mirrors LyricsBackwardSeekTests) ──

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

    private static (LyricsViewModel Vm, PlayerViewModel Player) Mount()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        player.Duration = TimeSpan.FromSeconds(90);
        player.CurrentTrack = new Track
        {
            Title = "Auto Follow Seek",
            Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-auto-follow-seek-no-such-file.mp3"),
        };
        return (vm, player);
    }

    [AvaloniaFact]
    public void CommittedSeek_ResumesAutoFollow()
    {
        var (vm, player) = Mount();
        vm.IsAutoFollowPaused = true;

        // Same commit path the timeline uses (EndSeek's debounce lands here too).
        player.SeekToPositionCommand.Execute(0.5);

        Assert.False(vm.IsAutoFollowPaused);
    }
}
