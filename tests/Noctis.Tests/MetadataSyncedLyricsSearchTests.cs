using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Metadata editor's "Search Lyrics" (Timestamp Lyrics tab) flow:
/// an exact /get hit applies, a fuzzy /search alternate must validate against the
/// edited tags + track duration before it applies (requireSynced), and a provider
/// error surfaces as a human message — never as "No Lyrics found" and never as the
/// raw exception text. Selector-level validation rules are pinned by
/// <see cref="LyricsSearchSelectorTests"/>; these pin the VM wiring around them.
/// </summary>
public class MetadataSyncedLyricsSearchTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public LrcLibResult? GetResult;
        public Exception? GetError;
        public List<LrcLibResult> SearchResults = new();

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => GetError != null ? Task.FromException<LrcLibResult?>(GetError) : Task.FromResult(GetResult);

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(SearchResults);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static MetadataViewModel Vm(StubLrcLib lrcLib, IPersistenceService persistence)
    {
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "A",
            AlbumArtist = "X",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), "noctistest", $"meta-lyrics-{Guid.NewGuid():N}.flac"),
        };
        return new MetadataViewModel(track, new StubMetadata(),
            new FakeLibraryService { TrackList = { track } }, persistence, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null, lrcLib: lrcLib);
    }

    private static LrcLibResult Synced(string lrc, double duration = 200,
        string track = "Test Song", string artist = "Test Artist") => new()
    {
        TrackName = track,
        ArtistName = artist,
        Duration = duration,
        SyncedLyrics = lrc,
    };

    // ── Tests ──

    [Fact]
    public async Task ExactGetHit_AppliesSyncedLyrics_AndMarksCustom()
    {
        var lrcLib = new StubLrcLib { GetResult = Synced("[00:01.00]exact match") };
        using var p = new TestPersistenceService();
        var vm = Vm(lrcLib, p);

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.Equal("[00:01.00]exact match", vm.SyncedLyrics);
        // Save gates the synced write on HasCustomSyncedLyrics — without it the
        // found lyrics would be silently dropped on Save.
        Assert.True(vm.HasCustomSyncedLyrics);
        Assert.Equal("Lyrics found", vm.SyncedLyricsSearchStatus);
        Assert.NotEmpty(vm.SyncedLyricLines);
    }

    [Fact]
    public async Task WrongDurationSearchAlternate_IsRejected_NotApplied()
    {
        var lrcLib = new StubLrcLib
        {
            GetResult = null,
            // Same names, but a full minute longer — a different recording whose
            // timestamps would drift; the fuzzy pick must not apply it.
            SearchResults = { Synced("[00:01.00]wrong recording", duration: 260) },
        };
        using var p = new TestPersistenceService();
        var vm = Vm(lrcLib, p);

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.True(string.IsNullOrWhiteSpace(vm.SyncedLyrics));
        Assert.False(vm.HasCustomSyncedLyrics);
        Assert.Equal("No Lyrics found", vm.SyncedLyricsSearchStatus);
    }

    [Fact]
    public async Task MatchingSearchAlternate_IsApplied_WhenGetMisses()
    {
        var lrcLib = new StubLrcLib
        {
            GetResult = null,
            SearchResults = { Synced("[00:01.00]validated alternate") },
        };
        using var p = new TestPersistenceService();
        var vm = Vm(lrcLib, p);

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.Equal("[00:01.00]validated alternate", vm.SyncedLyrics);
        Assert.True(vm.HasCustomSyncedLyrics);
        Assert.Equal("Lyrics found", vm.SyncedLyricsSearchStatus);
    }

    [Fact]
    public async Task ProviderError_ShowsConnectionMessage_NotRawException()
    {
        var lrcLib = new StubLrcLib
        {
            GetError = new LyricsProviderException("LRCLIB", new TimeoutException("The request was canceled")),
        };
        using var p = new TestPersistenceService();
        var vm = Vm(lrcLib, p);

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.Equal("Search failed — check your internet connection.", vm.SyncedLyricsSearchStatus);
        Assert.False(vm.HasCustomSyncedLyrics);
    }
}
