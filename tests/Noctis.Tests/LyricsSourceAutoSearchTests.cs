using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Issue #113: the automatic lookup searches the extra sources (Musixmatch, Kugou, YouTube
/// Music) switched on in Settings, prefers word-synced over line-synced over plain (earlier
/// source on a tie), and only waits on the extra sources while they could still improve the
/// pick. Word timings land in an .elrc beside a line-level .lrc, as Lyrics Studio's writer does.
/// </summary>
public class LyricsSourceAutoSearchTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);
        public int Calls;

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return GetImpl();
        }

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

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    /// <summary>An extra source whose answer the test controls; records every ask and its token.</summary>
    private sealed class StubSource
    {
        public Func<CancellationToken, Task<LrcLibResult?>> Impl = _ => Task.FromResult<LrcLibResult?>(null);
        public int Calls;
        public CancellationToken LastToken;

        public OnlineLyricsSource As(string name, Func<AppSettings, bool> enabled) => new(name, enabled, (_, _, _, ct) =>
        {
            Interlocked.Increment(ref Calls);
            LastToken = ct;
            return Impl(ct);
        });
    }

    private const string Elrc = "[00:01.00]<00:01.00>word <00:01.50>timed<00:02.00>";
    private const string Lrc = "[00:01.00]line timed";

    private static LrcLibResult Result(string? synced = null, string? plain = null) => new()
    {
        TrackName = "Test Song",
        ArtistName = "Test Artist",
        Duration = 200,
        SyncedLyrics = synced,
        PlainLyrics = plain ?? (synced == null ? null : LyricsTextHelper.StripTimestamps(synced)),
    };

    private static (LyricsViewModel Vm, Track Track, SettingsPersistence Persistence) Mount(
        StubLrcLib lrcLib, params OnlineLyricsSource[] extras)
    {
        var persistence = new SettingsPersistence();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new StubMetadata(), persistence, new FakeLibraryService())
        {
            ExtraLyricsSources = extras,
            // Trash = delete, so a replaced .elrc is gone and nothing reaches the real recycle bin.
            TrashSidecarFile = path => { File.Delete(path); return true; },
        };
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-113-{Guid.NewGuid():N}.mp3"),
        };
        player.CurrentTrack = track;
        return (vm, track, persistence);
    }

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Sidecar(Track track, string ext) => Path.ChangeExtension(track.FilePath, ext);

    private static void Cleanup(Track track)
    {
        foreach (var ext in new[] { ".lrc", ".elrc" })
        {
            var path = Sidecar(track, ext);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            AppWrittenSidecarRegistry.Default.Remove(path);
        }
    }

    // ── Format ranking ──

    private const string WordLyricsfile = """
        version: '1.0'
        lines:
        - text: hi there
          start_ms: 1000
          words:
          - text: 'hi '
            start_ms: 1000
          - text: there
            start_ms: 1400
        """;
    private const string LineLyricsfile = """
        version: '1.0'
        lines:
        - text: hi there
          start_ms: 1000
          end_ms: 2000
        """;

    /// <summary>
    /// Live check 10-05 ("The Bees Knees"): every LRCLIB result had a line-only Lyricsfile, so the
    /// picker labelled them "Word-synced" and auto picked LRCLIB as best over Kugou's real word
    /// timings. A source with real word timings must win over a line-only Lyricsfile.
    /// </summary>
    [Fact]
    public void LineOnlyLyricsfile_IsLineSynced_AndLosesToRealWordTimings()
    {
        var lrclib = Result(synced: Lrc);
        lrclib.Lyricsfile = LineLyricsfile;
        lrclib.HasWordSync = false;
        var kugou = Result(synced: Elrc);

        Assert.False(lrclib.HasWordSyncedLyricsfile);
        Assert.True(LyricsSearchSelector.FormatRank(kugou) > LyricsSearchSelector.FormatRank(lrclib));
    }

    [Fact]
    public void FormatRank_WordSyncedOverLineSyncedOverPlain()
    {
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(Result(synced: Elrc)));
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(new LrcLibResult { Lyricsfile = WordLyricsfile }));
        // LRCLIB sends a Lyricsfile with line-synced results too: it only times lines (10-05).
        Assert.Equal(2, LyricsSearchSelector.FormatRank(new LrcLibResult { Lyricsfile = LineLyricsfile, HasWordSync = false }));
        Assert.Equal(2, LyricsSearchSelector.FormatRank(new LrcLibResult { Lyricsfile = "lines: []" }));
        Assert.Equal(2, LyricsSearchSelector.FormatRank(Result(synced: Lrc)));
        Assert.Equal(1, LyricsSearchSelector.FormatRank(Result(plain: "just words")));
        Assert.Equal(0, LyricsSearchSelector.FormatRank(new LrcLibResult()));
        Assert.Equal(0, LyricsSearchSelector.FormatRank(null));
    }

    [Fact]
    public void PickBestResult_WordSyncedBeatsAnEarlierLineSyncedSource_TieGoesToTheEarlierSource()
    {
        var lrcLib = Result(synced: Lrc);
        var musixmatch = Result(synced: Elrc);
        var kugou = Result(synced: Elrc);

        var (primary, source, alternate, altSource) = LyricsViewModel.PickBestResult(
            new List<(LrcLibResult?, string)> { (lrcLib, "LRCLIB"), (null, "NetEase"), (musixmatch, "Musixmatch"), (kugou, "Kugou") });

        Assert.Same(musixmatch, primary);
        Assert.Equal("Musixmatch", source);
        Assert.Same(kugou, alternate);
        Assert.Equal("Kugou", altSource);
    }

    [Fact]
    public void ExtraPickIsFinal_OnlyWhenNoRunningSourceCouldStillBeatIt()
    {
        var word = Result(synced: Elrc);
        var line = Result(synced: Lrc);

        Assert.True(LyricsViewModel.ExtraPickIsFinal(LyricsSearchSelector.WordSyncedRank, new LrcLibResult?[] { null }, new[] { false }));
        // Musixmatch still running could tie Kugou's word-synced answer and win (earlier source).
        Assert.False(LyricsViewModel.ExtraPickIsFinal(2, new LrcLibResult?[] { null, word }, new[] { false, true }));
        Assert.True(LyricsViewModel.ExtraPickIsFinal(2, new LrcLibResult?[] { null, word, null }, new[] { true, true, false }));
        Assert.True(LyricsViewModel.ExtraPickIsFinal(2, new LrcLibResult?[] { word, null }, new[] { true, false }));
        Assert.False(LyricsViewModel.ExtraPickIsFinal(2, new LrcLibResult?[] { line, null }, new[] { true, false }));
    }

    // ── Automatic lookup ──

    [AvaloniaFact]
    public async Task AutoSearch_WordSyncedExtraSource_BeatsLineSyncedLrcLib_AndSavesElrcBesideALineLevelLrc()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) };
        var kugou = new StubSource { Impl = _ => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) };
        var (vm, track, _) = Mount(lrcLib, kugou.As("Kugou", s => s.KugouEnabled));

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == "Kugou" && File.Exists(Sidecar(track, ".elrc")));

            Assert.Equal("Kugou", vm.LyricsSourceName);
            Assert.Equal("Try LRCLIB", vm.AlternateLyricsLabel);
            Assert.NotNull(vm.LyricLines.First(l => l.IsSynced).Words);

            await PumpUntilAsync(() => File.Exists(Sidecar(track, ".lrc")));
            Assert.Equal(Elrc, File.ReadAllText(Sidecar(track, ".elrc")).Trim());
            var lrc = File.ReadAllText(Sidecar(track, ".lrc")).Trim();
            Assert.Equal("[00:01.00]word timed", lrc);
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task AutoSearch_SourceSwitchedOffInSettings_IsNotAsked()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) };
        var musixmatch = new StubSource { Impl = _ => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) };
        var (vm, track, persistence) = Mount(lrcLib, musixmatch.As("Musixmatch", s => s.MusixmatchEnabled));
        Assert.False(persistence.Settings.MusixmatchEnabled); // fresh-install default

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => lrcLib.Calls > 0 && !vm.IsSearching && vm.LyricsSourceName != "");

            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.Equal(0, musixmatch.Calls);
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task AutoSearch_WordSyncedBuiltInAnswer_DoesNotWaitForTheExtraSources()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) };
        var never = new TaskCompletionSource<LrcLibResult?>();
        var kugou = new StubSource { Impl = _ => never.Task };
        var (vm, track, _) = Mount(lrcLib, kugou.As("Kugou", s => s.KugouEnabled));
        vm.ExtraSourceGrace = TimeSpan.FromSeconds(30);
        vm.ExtraSourceTimeout = TimeSpan.FromSeconds(30);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == "LRCLIB" && !vm.IsSearching);

            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.Equal(1, kugou.Calls);
            Assert.True(kugou.LastToken.IsCancellationRequested); // the dropped source was told to stop
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task AutoSearch_SlowExtraSource_IsCutOffAfterTheGrace()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) };
        var never = new TaskCompletionSource<LrcLibResult?>();
        var kugou = new StubSource { Impl = _ => never.Task };
        var (vm, track, _) = Mount(lrcLib, kugou.As("Kugou", s => s.KugouEnabled));
        vm.ExtraSourceGrace = TimeSpan.FromMilliseconds(150);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == "LRCLIB" && !vm.IsSearching);

            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.False(vm.HasAlternateLyrics);
            Assert.True(kugou.LastToken.IsCancellationRequested);
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task AutoSearch_EveryProviderIncludingTheExtrasErrored_ShowsTheConnectionMessage()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromException<LrcLibResult?>(new LyricsProviderException("LRCLIB", new TimeoutException())) };
        var kugou = new StubSource { Impl = _ => Task.FromException<LrcLibResult?>(new LyricsProviderException("Kugou", new TimeoutException())) };
        var (vm, track, _) = Mount(lrcLib, kugou.As("Kugou", s => s.KugouEnabled));

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(() => vm.SearchFailedMessage != "");

        Assert.Equal("Search failed — check your internet connection.", vm.SearchFailedMessage);
    }

    [AvaloniaFact]
    public async Task AutoSearch_AnExtraSourceAnsweringEmptyHanded_IsANotFound_NotAConnectionError()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromException<LrcLibResult?>(new LyricsProviderException("LRCLIB", new TimeoutException())) };
        var kugou = new StubSource();
        var (vm, track, _) = Mount(lrcLib, kugou.As("Kugou", s => s.KugouEnabled));

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(() => vm.SearchFailedMessage != "");

        Assert.Equal("No Lyrics found.", vm.SearchFailedMessage);
    }
}
