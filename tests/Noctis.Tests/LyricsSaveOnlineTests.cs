using System.Xml.Linq;
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
            Assert.Contains(OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash", prompt);
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
            // Drain first: the automatic sidecar persist runs on the writer lane, so checking
            // before it settled passed or failed on timing alone (it wrote a streamed song's
            // .lrc whenever the lane ran first).
            await DrainWriterLaneAsync();
            Assert.False(File.Exists(Sidecar(remote.Track, ".lrc")));
            Assert.Empty(remote.Prompts);
        }
        finally { Cleanup(local.Track); Cleanup(remote.Track); }
    }

    // ── Save Lyrics to File ▸ TTML (Discord, nutf!xx: "ttml preferred") ──

    private static readonly XNamespace TtNs = "http://www.w3.org/ns/ttml";

    [AvaloniaFact]
    public async Task SaveTtml_WordSynced_WritesWordSpansThatReadBack_AndThePageShowsTheTtml()
    {
        var h = Mount();
        try
        {
            await ShowPickOverOwnTtmlAsync(h, Result(synced: Elrc));
            Assert.True(h.Vm.CanSaveTtmlToFile);

            await h.Vm.SaveLyricsAsTtmlCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            // The song's own, different .ttml is named, then trashed before the new one lands.
            var prompt = Assert.Single(h.Prompts);
            Assert.Contains($"“{Name(h.Track, ".ttml")}”", prompt);
            Assert.Contains(Name(h.Track, ".ttml"), h.Trashed);
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(Sidecar(h.Track, ".ttml")));
            // Same as LRC besides: the .elrc and its line-level .lrc.
            Assert.Equal(Elrc, Read(Sidecar(h.Track, ".elrc")));
            Assert.Equal("[00:01.00]word timed", Read(Sidecar(h.Track, ".lrc")));

            var text = File.ReadAllText(Sidecar(h.Track, ".ttml"));
            var p = Assert.Single(XDocument.Parse(text).Descendants(TtNs + "p"));
            Assert.Equal("00:00:01.000", p.Attribute("begin")!.Value);
            Assert.Equal("00:00:02.000", p.Attribute("end")!.Value);
            Assert.Contains("itunes:timing=\"Word\"", text);

            var (lines, _) = TtmlParser.Parse(text);
            var line = Assert.Single(lines!);
            Assert.Equal(TimeSpan.FromSeconds(1), line.Timestamp);
            Assert.Equal(new[] { "word@1000-1500", "timed@1500-2000" },
                line.Words!.Select(w => $"{w.Text.Trim()}@{w.Start.TotalMilliseconds}-{w.End!.Value.TotalMilliseconds}"));

            // The page re-reads the files and shows the .ttml (it out-ranks .elrc/.lrc); a
            // .ttml on the page offers no further TTML save.
            await PumpUntilAsync(() => h.Vm.LyricLines.Any(l => l.Text.Contains("word") && l.Words is { Count: > 0 }));
            Assert.DoesNotContain(h.Vm.LyricLines, l => l.Text == "my ttml line");
            Assert.False(h.Vm.CanSaveTtmlToFile);
            Assert.False(h.Vm.CanSaveToFile);
        }
        finally { Cleanup(h.Track); }
    }

    [AvaloniaFact]
    public async Task SaveTtml_LineSynced_WritesLineTimes_AndNoElrc()
    {
        const string twoLines = "[00:01.00]line timed\n[00:03.50]second line";
        var h = Mount();
        try
        {
            h.Vm.SearchLyricsForTrack(h.Track);
            await PumpUntilAsync(() => h.Vm.ShowSearchButton || h.Vm.LyricsSourceName.Length > 0);
            h.Vm.ApplySearchedLyrics(h.Track, Result(synced: twoLines), "LRCLIB");
            await DrainWriterLaneAsync();
            Assert.True(h.Vm.CanSaveTtmlToFile);

            await h.Vm.SaveLyricsAsTtmlCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            Assert.Empty(h.Prompts); // the .lrc already holds these lines; the .ttml is new
            Assert.Equal(twoLines, Read(Sidecar(h.Track, ".lrc")));
            Assert.False(File.Exists(Sidecar(h.Track, ".elrc")));
            var text = File.ReadAllText(Sidecar(h.Track, ".ttml"));
            Assert.Contains("itunes:timing=\"Line\"", text);
            var ps = XDocument.Parse(text).Descendants(TtNs + "p").ToList();
            Assert.Equal(new[] { "00:00:01.000", "00:00:03.500" }, ps.Select(p => p.Attribute("begin")!.Value));
            Assert.Equal("00:00:03.500", ps[0].Attribute("end")!.Value);

            var (lines, _) = TtmlParser.Parse(text);
            Assert.Equal(new[] { "line timed", "second line" }, lines!.Select(l => l.Text));
            Assert.Equal(new TimeSpan?[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3.5) }, lines.Select(l => l.Timestamp));
            Assert.All(lines, l => Assert.False(l.Words is { Count: > 0 }));
        }
        finally { Cleanup(h.Track); }
    }

    /// <summary>
    /// The automatic save already put most found lyrics in an .lrc, and LRC Save is only for
    /// online lyrics: the song's own synced lyrics get TTML too. Its .lrc stays as it is — not
    /// rewritten, not sent to the Recycle Bin, not asked about.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveTtml_OfTheSongsOwnLrc_AddsTheTtml_LeavesTheUsersLrcAlone()
    {
        const string own = "[00:05.00]my own line\n[00:07.00]next one";
        var h = Mount();
        File.WriteAllText(Sidecar(h.Track, ".lrc"), own);
        try
        {
            h.Vm.SearchLyricsForTrack(h.Track);
            await PumpUntilAsync(() => h.Vm.CanSaveTtmlToFile);
            Assert.True(h.Vm.CanSaveTtmlToFile);
            Assert.False(h.Vm.CanSaveToFile); // LRC stays an online-lyrics save

            // LRC does nothing for the song's own lyrics.
            await h.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();
            Assert.False(File.Exists(Sidecar(h.Track, ".ttml")));

            await h.Vm.SaveLyricsAsTtmlCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();

            Assert.Empty(h.Prompts);
            Assert.Empty(h.Trashed);
            Assert.Equal(own, Read(Sidecar(h.Track, ".lrc")));
            Assert.False(AppWrittenSidecarRegistry.Default.Contains(Sidecar(h.Track, ".lrc")));
            var (lines, _) = TtmlParser.Parse(File.ReadAllText(Sidecar(h.Track, ".ttml")));
            Assert.Equal(new[] { "my own line", "next one" }, lines!.Select(l => l.Text));
            Assert.Equal(new TimeSpan?[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7) }, lines.Select(l => l.Timestamp));
            await PumpUntilAsync(() => !h.Vm.CanSaveTtmlToFile);
            Assert.False(h.Vm.CanSaveTtmlToFile);
        }
        finally { Cleanup(h.Track); }
    }

    [AvaloniaFact]
    public async Task CanSaveTtml_NotForPlainLyrics_NorLyricsReadFromATtml()
    {
        var plain = Mount();
        var fromTtml = Mount();
        try
        {
            // The song's own plain lyrics: no times to put in a .ttml.
            plain.Track.Lyrics = "just words\nno times";
            plain.Vm.SearchLyricsForTrack(plain.Track);
            await PumpUntilAsync(() => plain.Vm.UnsyncedLines.Any(l => l.Text == "just words"));
            Assert.False(plain.Vm.CanSaveTtmlToFile);
            await plain.Vm.SaveLyricsAsTtmlCommand.ExecuteAsync(null);
            await DrainWriterLaneAsync();
            Assert.False(File.Exists(Sidecar(plain.Track, ".ttml")));

            // Plain online lyrics neither.
            plain.Vm.ApplySearchedLyrics(plain.Track, Result(plain: "just words"), "Genius");
            Assert.False(plain.Vm.CanSaveTtmlToFile);
            Assert.False(plain.Vm.CanSaveToFile);

            // Synced online lyrics: both formats.
            plain.Vm.ApplySearchedLyrics(plain.Track, Result(synced: Lrc), "LRCLIB");
            Assert.True(plain.Vm.CanSaveTtmlToFile);
            Assert.True(plain.Vm.CanSaveToFile);

            // The page already reads a .ttml: nothing to make.
            File.WriteAllText(Sidecar(fromTtml.Track, ".ttml"), Ttml("my ttml line"));
            fromTtml.Vm.SearchLyricsForTrack(fromTtml.Track);
            await PumpUntilAsync(() => fromTtml.Vm.LyricLines.Any(l => l.Text == "my ttml line"));
            Assert.False(fromTtml.Vm.CanSaveTtmlToFile);
        }
        finally { Cleanup(plain.Track); Cleanup(fromTtml.Track); }
    }

    /// <summary>What the lyrics page shows from an .elrc is what it shows from the .ttml made of it: lines, word times, ad-lib rows.</summary>
    [Fact]
    public void BuildTtmlFromSynced_ReadsBackAsTheElrcReads()
    {
        const string elrc = """
            [ar:Someone]
            [00:20.00]<00:20.00>Come <00:20.30>take <00:20.60>it <00:21.00>(Yeah)<00:21.50>
            [00:22.00]<00:22.00>It's <00:22.30>my <00:22.60>year<00:23.00>
            [00:23.10]<00:23.10>(Uh-huh, <00:23.40>uh-huh)<00:23.80>
            [00:25.00]A line with no word times
            [62:03.25]<62:03.25>Late<62:04.00>
            """;
        var ttml = LyricsViewModel.BuildTtmlFromSynced(elrc);
        Assert.NotNull(ttml);
        XDocument.Parse(ttml!);

        var fromElrc = LyricsViewModel.ParseLrcContent(elrc);
        var (fromTtml, _) = TtmlParser.Parse(ttml!);
        static string[] Row(IReadOnlyList<WordTiming>? words) =>
            words?.Select(w => $"{w.Text.Trim()}@{w.Start.TotalMilliseconds}").ToArray() ?? Array.Empty<string>();
        Assert.NotNull(fromTtml);
        // Line text of a line with an ad-lib differs by design (the ELRC keeps "(Yeah)" in it,
        // TTML only in the background row), so lines compare by their timed rows.
        Assert.Equal(fromElrc.Count, fromTtml!.Count);
        Assert.Equal(fromElrc.Select(l => l.Timestamp), fromTtml.Select(l => l.Timestamp));
        Assert.Equal("A line with no word times", fromTtml[2].Text.Trim());
        for (var i = 0; i < fromElrc.Count; i++)
        {
            Assert.Equal(Row(fromElrc[i].Words), Row(fromTtml[i].Words));
            Assert.Equal(Row(fromElrc[i].BackgroundWords), Row(fromTtml[i].BackgroundWords));
        }
        Assert.Equal(new[] { "(Uh-huh,@23100", "uh-huh)@23400" }, Row(fromTtml[1].BackgroundWords));

        Assert.Null(LyricsViewModel.BuildTtmlFromSynced("just words, no times"));
    }

    // ── Player-bar Options menu ──

    [AvaloniaFact]
    public void PlayerMenu_SaveLyricsTtml_FollowsTheLyricsPage()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrc = 0;
        var ttml = 0;

        player.SetLyricsPageActions(() => { }, () => { }, () => { }, () => { }, () => { }, true, false, true, true,
            saveLyrics: () => lrc++, canSaveLyrics: false, saveLyricsTtml: () => ttml++, canSaveLyricsTtml: false);
        Assert.False(player.HasSaveLyricsMenu); // plain lyrics: no Save submenu at all

        // The song's own synced lyrics: the submenu with TTML only.
        player.UpdateLyricsPageState(true, false, true, true, canSaveLyrics: false, canSaveLyricsTtml: true);
        Assert.True(player.HasSaveLyricsMenu);
        Assert.False(player.CanSaveLyricsToFile);
        Assert.True(player.CanSaveLyricsAsTtml);
        player.SaveCurrentTrackLyricsAsTtmlCommand.Execute(null);
        Assert.Equal((0, 1), (lrc, ttml));

        // Synced online lyrics: both.
        player.UpdateLyricsPageState(true, false, true, true, canSaveLyrics: true, canSaveLyricsTtml: true);
        Assert.True(player.CanSaveLyricsToFile);
        player.SaveCurrentTrackLyricsCommand.Execute(null);
        Assert.Equal((1, 1), (lrc, ttml));

        player.ClearLyricsPageActions();
        Assert.False(player.HasSaveLyricsMenu);
        Assert.False(player.CanSaveLyricsAsTtml);
        player.SaveCurrentTrackLyricsAsTtmlCommand.Execute(null);
        Assert.Equal((1, 1), (lrc, ttml));
    }

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
