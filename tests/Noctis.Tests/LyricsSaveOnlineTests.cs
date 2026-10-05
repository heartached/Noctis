using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #115: save online lyrics next to the song, under the song's name, replacing what is
/// there — a .ttml included, since the lyrics page reads it before .elrc and .lrc and it would
/// hide the save. Word-synced lyrics go to .elrc (+ the line-level .lrc), line-synced to .lrc.
/// The user is asked first, and a lyrics file Noctis didn't write goes to the Recycle Bin.
/// </summary>
public class LyricsSaveOnlineTests
{
    // ── Harness (same shape as LyricsSourcePickerTests) ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => GetImpl();

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
        public int Writes;
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) { Interlocked.Increment(ref Writes); return false; }
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

    private sealed class Harness
    {
        public required LyricsViewModel Vm;
        public required Track Track;
        public required StubMetadata Metadata;
        public readonly List<string> Trashed = new();
        public readonly List<string> Prompts = new();
        public bool Answer = true;
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

    private static Harness Mount(StubLrcLib? lrcLib = null, SourceType source = SourceType.Local)
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var metadata = new StubMetadata();
        var vm = new LyricsViewModel(player, lrcLib ?? new StubLrcLib(), new StubNetEase(), metadata, new SettingsPersistence(), new FakeLibraryService())
        {
            // The picker is a no-op here; tests apply a pick directly.
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
        };
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Duration = TimeSpan.FromSeconds(200),
            SourceType = source,
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-115-{Guid.NewGuid():N}.mp3"),
        };
        var h = new Harness { Vm = vm, Track = track, Metadata = metadata };
        // Trash = delete (recorded), so nothing reaches the real recycle bin.
        vm.TrashSidecarFile = path => { h.Trashed.Add(Path.GetFileName(path)); File.Delete(path); return true; };
        vm.ConfirmReplaceLyrics = message => { h.Prompts.Add(message); return Task.FromResult(h.Answer); };
        player.CurrentTrack = track;
        return h;
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

    /// <summary>Lets every queued sidecar write land (the writer lane is FIFO).</summary>
    private static async Task DrainWriterLaneAsync()
    {
        await LyricsViewModel.EnqueueLyricsFileWork(() => { });
        Dispatcher.UIThread.RunJobs();
    }

    private static string Sidecar(Track track, string ext) => Path.ChangeExtension(track.FilePath, ext);

    private static string Name(Track track, string ext) => Path.GetFileName(Sidecar(track, ext));

    private static void Cleanup(Track track)
    {
        foreach (var ext in new[] { ".lrc", ".elrc", ".ttml", ".lyricsfile", ".txt" })
        {
            var path = Sidecar(track, ext);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            AppWrittenSidecarRegistry.Default.Remove(path);
        }
    }

    private static string Ttml(string text) => TimedLyricsBuilder.BuildTtml(new[]
    {
        new AlignedLine(text, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), Array.Empty<AlignedWord>(), 1, false),
    });

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n").Trim();

    /// <summary>The song's own .ttml on the page, then online lyrics picked over it in Search Lyrics.</summary>
    private static async Task ShowPickOverOwnTtmlAsync(Harness h, LrcLibResult pick)
    {
        File.WriteAllText(Sidecar(h.Track, ".ttml"), Ttml("my ttml line"));
        h.Vm.SearchLyricsForTrack(h.Track);
        await PumpUntilAsync(() => h.Vm.LyricLines.Any(l => l.Text == "my ttml line"));
        Assert.Contains(h.Vm.LyricLines, l => l.Text == "my ttml line");

        h.Vm.ApplySearchedLyrics(h.Track, pick, "Kugou");
        await DrainWriterLaneAsync();
    }

    // ── Word timings LRCLIB sends as a Lyricsfile (live check 10-05) ──

    /// <summary>What LRCLIB answers for a song it has word timings for: a line-level
    /// syncedLyrics AND the word timings in a Lyricsfile.</summary>
    private const string Lyricsfile = """
        version: '1.0'
        metadata:
          title: Test Song
        lines:
          - text: word timed
            start_ms: 1000
            end_ms: 2000
            words:
              - text: 'word '
                start_ms: 1000
                end_ms: 1500
              - text: timed
                start_ms: 1500
                end_ms: 2000
        """;

    private static LrcLibResult LrclibWordResult() => new()
    {
        TrackName = "Test Song", ArtistName = "Test Artist", Duration = 200,
        SyncedLyrics = "[00:01.00]word timed", PlainLyrics = "word timed", Lyricsfile = Lyricsfile,
    };

    [Fact]
    public void Lyricsfile_ToElrc_KeepsTheWordTimings()
    {
        Assert.Equal(Elrc, LyricsfileParser.ToElrc(Lyricsfile));
        Assert.Equal(Elrc, LyricsViewModel.SyncedToWrite(LrclibWordResult()));
        Assert.Equal(Lrc, LyricsViewModel.SyncedToWrite(Result(synced: Lrc)));
    }

    [AvaloniaFact]
    public async Task Save_LrclibWordSynced_WritesTheWordTimings_NotJustTheLineLevelTwin()
    {
        var h = Mount();
        try
        {
            await ShowPickOverOwnTtmlAsync(h, LrclibWordResult());

            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            Assert.Equal(Elrc, Read(Sidecar(h.Track, ".elrc")));
            Assert.Equal("[00:01.00]word timed", Read(Sidecar(h.Track, ".lrc")));
        }
        finally { Cleanup(h.Track); }
    }

    /// <summary>The automatic save wrote only the line-level .lrc, which out-ranks the cached
    /// Lyricsfile: word-by-word on the first play, line-by-line ever after.</summary>
    [AvaloniaFact]
    public async Task AutoSave_LrclibWordSynced_KeepsTheSongWordSyncedNextTime()
    {
        var h = Mount();
        try
        {
            h.Vm.ApplySearchedLyrics(h.Track, LrclibWordResult(), "LRCLIB");
            await DrainWriterLaneAsync();

            Assert.Equal(Elrc, Read(Sidecar(h.Track, ".elrc")));
        }
        finally { Cleanup(h.Track); }
    }

    // ── Save ──

    [AvaloniaFact]
    public async Task Save_WordSyncedOverTheSongsOwnTtml_AsksNamingIt_TrashesIt_WritesElrcAndLrc_AndThePageShowsTheSave()
    {
        var h = Mount();
        try
        {
            await ShowPickOverOwnTtmlAsync(h, Result(synced: Elrc));
            Assert.True(h.Vm.CanSaveToFile);

            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            var prompt = Assert.Single(h.Prompts);
            Assert.Contains($"“{Name(h.Track, ".ttml")}”", prompt);
            Assert.Contains("Recycle Bin", prompt);
            Assert.Contains(Name(h.Track, ".ttml"), h.Trashed);
            Assert.False(File.Exists(Sidecar(h.Track, ".ttml")));
            Assert.Equal(Elrc, Read(Sidecar(h.Track, ".elrc")));
            Assert.Equal("[00:01.00]word timed", Read(Sidecar(h.Track, ".lrc")));
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(Sidecar(h.Track, ".elrc")));
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(Sidecar(h.Track, ".lrc")));
            Assert.False(File.Exists(Sidecar(h.Track, ".txt")));
            Assert.Equal(0, h.Metadata.Writes); // the audio file itself is left alone

            // The page re-reads the files: what shows is what was saved, not the old .ttml.
            await PumpUntilAsync(() => h.Vm.LyricLines.Any(l => l.Text == "word timed"));
            Assert.Contains(h.Vm.LyricLines, l => l.Text == "word timed");
            Assert.DoesNotContain(h.Vm.LyricLines, l => l.Text == "my ttml line");
            Assert.Equal(Elrc, h.Track.SyncedLyrics);
        }
        finally { Cleanup(h.Track); }
    }

    [AvaloniaFact]
    public async Task Save_LineSyncedOverTheUsersOwnLrcAndElrc_TrashesBothFirst_AndWritesOnlyLrc()
    {
        var h = Mount();
        File.WriteAllText(Sidecar(h.Track, ".lrc"), "[00:05.00]my own line");
        File.WriteAllText(Sidecar(h.Track, ".elrc"), "[00:05.00]<00:05.00>my <00:05.50>own<00:06.00>");
        try
        {
            h.Vm.SearchLyricsForTrack(h.Track);
            await PumpUntilAsync(() => h.Vm.LyricLines.Any(l => l.Text == "my own"));
            h.Vm.ApplySearchedLyrics(h.Track, Result(synced: Lrc), "LRCLIB");
            await DrainWriterLaneAsync();

            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            var prompt = Assert.Single(h.Prompts);
            Assert.Contains($"“{Name(h.Track, ".elrc")}”", prompt);
            Assert.Contains($"“{Name(h.Track, ".lrc")}”", prompt);
            Assert.Contains(Name(h.Track, ".lrc"), h.Trashed);
            Assert.Contains(Name(h.Track, ".elrc"), h.Trashed);
            Assert.Equal(Lrc, Read(Sidecar(h.Track, ".lrc")));
            Assert.False(File.Exists(Sidecar(h.Track, ".elrc")));
            await PumpUntilAsync(() => h.Vm.LyricLines.Any(l => l.Text == "line timed"));
            Assert.Contains(h.Vm.LyricLines, l => l.Text == "line timed");
        }
        finally { Cleanup(h.Track); }
    }

    [AvaloniaFact]
    public async Task Save_Declined_LeavesTheSongsFilesAlone()
    {
        var h = Mount();
        h.Answer = false;
        try
        {
            await ShowPickOverOwnTtmlAsync(h, Result(synced: Elrc));

            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            Assert.Single(h.Prompts);
            Assert.Empty(h.Trashed);
            Assert.Equal(Ttml("my ttml line").Replace("\r\n", "\n").Trim(), Read(Sidecar(h.Track, ".ttml")));
            Assert.False(File.Exists(Sidecar(h.Track, ".elrc")));
            Assert.False(File.Exists(Sidecar(h.Track, ".lrc")));
        }
        finally { Cleanup(h.Track); }
    }

    [AvaloniaFact]
    public async Task Save_OfLyricsAlreadyOnDisk_DoesNotAsk()
    {
        // No lyrics of its own: the automatic search found these and already wrote them.
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) };
        var h = Mount(lrcLib);
        try
        {
            h.Vm.SearchLyricsForTrack(h.Track);
            await PumpUntilAsync(() => h.Vm.LyricsSourceName == "LRCLIB");
            await DrainWriterLaneAsync();
            Assert.True(File.Exists(Sidecar(h.Track, ".elrc")));

            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            Assert.Empty(h.Prompts);
            Assert.Empty(h.Trashed);
            Assert.Equal(Elrc, Read(Sidecar(h.Track, ".elrc")));
            Assert.Equal("[00:01.00]word timed", Read(Sidecar(h.Track, ".lrc")));
        }
        finally { Cleanup(h.Track); }
    }

    // ── When Save is offered ──

    [AvaloniaFact]
    public async Task CanSave_OnlyForSyncedOnlineLyrics_OfALocalFile()
    {
        var local = Mount();
        var remote = Mount(source: SourceType.Jellyfin);
        try
        {
            // The song's own lyrics on the page: nothing to save.
            File.WriteAllText(Sidecar(local.Track, ".lrc"), "[00:05.00]my own line");
            local.Vm.SearchLyricsForTrack(local.Track);
            await PumpUntilAsync(() => local.Vm.LyricLines.Any(l => l.Text == "my own line"));
            Assert.False(local.Vm.CanSaveToFile);

            // Plain online lyrics have no timings to put in an .lrc.
            local.Vm.ApplySearchedLyrics(local.Track, Result(plain: "just words"), "Genius");
            Assert.False(local.Vm.CanSaveToFile);

            local.Vm.ApplySearchedLyrics(local.Track, Result(synced: Lrc), "LRCLIB");
            Assert.True(local.Vm.CanSaveToFile);

            // A streamed song has no file of its own to save beside.
            remote.Vm.SearchLyricsForTrack(remote.Track);
            await PumpUntilAsync(() => remote.Vm.ShowSearchButton || remote.Vm.LyricsSourceName.Length > 0);
            remote.Vm.ApplySearchedLyrics(remote.Track, Result(synced: Lrc), "LRCLIB");
            Assert.False(remote.Vm.CanSaveToFile);
            await remote.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            Assert.False(File.Exists(Sidecar(remote.Track, ".lrc")));
            Assert.Empty(remote.Prompts);
            await DrainWriterLaneAsync();
        }
        finally { Cleanup(local.Track); Cleanup(remote.Track); }
    }

    // ── Player-bar Options menu ──

    [AvaloniaFact]
    public void PlayerMenu_SaveLyrics_FollowsTheLyricsPage()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var saved = 0;

        player.SetLyricsPageActions(() => { }, () => { }, () => { }, () => { }, () => { }, true, false, true, true,
            saveLyrics: () => saved++, canSaveLyrics: false);
        Assert.False(player.CanSaveLyricsToFile);

        player.UpdateLyricsPageState(true, false, true, true, canSaveLyrics: true);
        Assert.True(player.CanSaveLyricsToFile);
        player.SaveCurrentTrackLyricsCommand.Execute(null);
        Assert.Equal(1, saved);

        player.ClearLyricsPageActions();
        Assert.False(player.CanSaveLyricsToFile);
        player.SaveCurrentTrackLyricsCommand.Execute(null);
        Assert.Equal(1, saved);
    }
}
