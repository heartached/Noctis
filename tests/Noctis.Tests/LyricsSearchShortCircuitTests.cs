using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// LRCLIB /get returning instrumental=true used to fall through to fuzzy /search and
/// display a different song's lyrics on an instrumental track; and the "Unknown Artist"
/// placeholder default used to be sent verbatim as an exact-match /get query. These pin
/// the online-search short-circuits in <see cref="LyricsViewModel.SearchLyrics"/>.
/// </summary>
public class LyricsSearchShortCircuitTests
{
    // ── Harness ──

    private sealed class RecordingLrcLib : ILrcLibService
    {
        public LrcLibResult? GetResult;
        public List<LrcLibResult> SearchResults = new();
        public int GetCalls;
        public int SearchCalls;
        public string? LastSearchArtist;

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(GetResult);
        }

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        {
            SearchCalls++;
            LastSearchArtist = artist;
            return Task.FromResult(SearchResults);
        }
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

    /// <summary>Mounts a lyric-less track (local probes miss on the nonexistent path,
    /// which auto-triggers the online search) and pumps until that search settles.</summary>
    private static async Task<LyricsViewModel> MountAndSearch(RecordingLrcLib lrcLib, string artist)
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, lrcLib, new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        var track = new Track
        {
            Title = "Test Song",
            Artist = artist,
            Duration = TimeSpan.FromSeconds(200),
            // Unique per mount: displayed online lyrics get persisted to a sidecar
            // next to this path, which a later test's local probe would then find.
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-shortcircuit-{Guid.NewGuid():N}.mp3"),
        };
        player.CurrentTrack = track;

        // Loads the track, awaits the (missing) local probe, then auto-triggers
        // the online search — the same flow as playback/context-menu search.
        vm.SearchLyricsForTrack(track);

        // Local probe (off-thread) -> auto search -> provider stubs; wall-clock budget
        // like the other lyrics harnesses, since the pool may be busy under a full run.
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline &&
               (lrcLib.GetCalls + lrcLib.SearchCalls == 0 || vm.IsSearching))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    // ── Tests ──

    [AvaloniaFact]
    public async Task InstrumentalGetResult_ShortCircuits_NeverFallsThroughToSearch()
    {
        var lrcLib = new RecordingLrcLib
        {
            GetResult = new LrcLibResult { TrackName = "Test Song", ArtistName = "Test Artist", Instrumental = true },
            // Poisoned fallback: a wrong song's synced lyrics the old code would have shown.
            SearchResults = { new LrcLibResult
            {
                TrackName = "Test Song", ArtistName = "Test Artist",
                Duration = 200, SyncedLyrics = "[00:01.00]wrong song",
            } },
        };

        var vm = await MountAndSearch(lrcLib, artist: "Test Artist");

        Assert.Equal(1, lrcLib.GetCalls);
        Assert.Equal(0, lrcLib.SearchCalls);
        Assert.Equal("This track is instrumental.", vm.SearchFailedMessage);
        Assert.Empty(vm.LyricLines);
    }

    [AvaloniaFact]
    public async Task UnknownArtistPlaceholder_SkipsGet_SearchesTitleOnly()
    {
        var lrcLib = new RecordingLrcLib
        {
            SearchResults = { new LrcLibResult
            {
                TrackName = "Test Song", ArtistName = "Real Artist",
                Duration = 200, SyncedLyrics = "[00:01.00]validated hit",
            } },
        };

        var vm = await MountAndSearch(lrcLib, artist: "Unknown Artist");

        Assert.Equal(0, lrcLib.GetCalls);
        Assert.Equal(1, lrcLib.SearchCalls);
        Assert.Equal("", lrcLib.LastSearchArtist);
        Assert.NotEmpty(vm.LyricLines);
    }
}
