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
/// GitHub #215 / #216 (11-09): Find Lyrics told lyrics kinds by the wrong thing.
/// #215: LRCLIB sends a Lyricsfile with every answer, plain-only ones too ("lines: []" plus a
/// "plain:" block, checked live 11-09 on /api/search?q=hello). Any Lyricsfile counted as
/// line-synced, so a plain answer was labelled synced, won "best" over a real synced answer
/// from a later source, and was saved as an "upgrade".
/// #216: the dialog's rows read "plain lyrics" for every song whose synced lyrics live in the
/// file's lyrics tag (USLT / LYRICS / ©lyr), where the scanner keeps them in the plain field.
/// </summary>
public class FindLyricsKinds1109Tests : IDisposable
{
    private const string Lrc = "[00:01.00]first line\n[00:02.00]second line";
    private const string Elrc = "[00:01.00]<00:01.00>first <00:01.40>line\n[00:02.00]<00:02.00>second <00:02.50>line";
    private const string Plain = "first line\nsecond line";

    /// <summary>The shape LRCLIB answers a plain-only entry with (live 11-09, id 9868210).</summary>
    private const string LrclibPlainOnlyLyricsfile = """
        version: '1.0'
        metadata:
          title: Test Song
          artist: Test Artist
          album: Test Album
          duration_ms: 200000
          instrumental: false
        lines: []
        plain: |-
          first line
          second line
        """;

    /// <summary>A Lyricsfile that times lines but not words.</summary>
    private const string LineLyricsfile = """
        version: '1.0'
        lines:
        - text: first line
          start_ms: 1000
          end_ms: 2000
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "find1109-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibraryService _library = new();
    private readonly AppSettings _settings = new();
    private readonly LyricsSaveTargetTests.RecordingMetadata _metadata = new();
    private DateTime _clock = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    public FindLyricsKinds1109Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    private static LrcLibResult LrclibPlainOnly() => new()
    {
        TrackName = "Test Song", ArtistName = "Test Artist", Duration = 200,
        PlainLyrics = Plain, Lyricsfile = LrclibPlainOnlyLyricsfile, HasWordSync = false,
    };

    private static LrcLibResult LineSynced() => new()
    {
        TrackName = "Test Song", ArtistName = "Test Artist", Duration = 200,
        PlainLyrics = Plain, SyncedLyrics = Lrc,
    };

    // ── #215: kinds by content ──

    [Fact]
    public void FormatRank_LrclibPlainOnlyAnswer_IsPlain()
    {
        Assert.Equal(1, LyricsSearchSelector.FormatRank(LrclibPlainOnly()));
    }

    [Fact]
    public void FormatRank_PlainTextInTheSyncedSlot_IsPlain()
    {
        Assert.Equal(1, LyricsSearchSelector.FormatRank(new LrcLibResult { SyncedLyrics = Plain }));
    }

    [Fact]
    public void FormatRank_TimedLyricsfile_StaysLineSynced()
    {
        Assert.Equal(2, LyricsSearchSelector.FormatRank(new LrcLibResult { Lyricsfile = LineLyricsfile, HasWordSync = false }));
    }

    [Fact]
    public void SearchPick_PrefersARealSyncedAnswer_OverAPlainOneListedFirst()
    {
        var plain = LrclibPlainOnly();
        var synced = LineSynced();

        var pick = LyricsSearchSelector.PickFromSearchResults(new[] { plain, synced }, "Test Artist", "Test Song", 200);

        Assert.Same(synced, pick);
        Assert.Same(synced, LyricsSearchSelector.PickFromSearchResults(new[] { plain, synced }, "Test Artist", "Test Song", 200, requireSynced: true));
    }

    [AvaloniaFact]
    public void SearchDialogRow_LrclibPlainOnlyAnswer_IsLabelledPlain()
    {
        var row = new LyricsSearchResultRow(new LyricsSourceHit("LRCLIB", LrclibPlainOnly(), false, false), isBest: true);

        Assert.Equal(Noctis.Localization.Loc.T("LyricsSearch.Plain"), row.FormatLabel);
    }

    // ── Bulk run: a plain answer is never a synced one ──

    private sealed class Source
    {
        public readonly string Name;
        public readonly Func<LrcLibResult?> Answer;
        public Source(string name, Func<LrcLibResult?> answer) { Name = name; Answer = answer; }
        public LyricsBulkSource Bulk => new(Name, _ => true, (a, t, d, c) => Task.FromResult(Answer()));
    }

    private LyricsBulkService Service(params Source[] sources) => new(
        null!,
        new LyricsWriter(_metadata, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            SaveTarget = () => LyricsSaveTarget.Sidecar,
        },
        _library,
        () => _settings,
        () => sources.Select(s => s.Bulk).ToList(),
        Path.Combine(_dir, "ledger.json"))
    {
        Clock = () => _clock,
        Delay = (t, _) => { _clock += t; return Task.CompletedTask; },
    };

    private Track NewTrack(string name = "song", string? plain = null)
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        var t = new Track { Title = "Test Song", Artist = "Test Artist", FilePath = audio, SourceType = SourceType.Local, Duration = TimeSpan.FromSeconds(200) };
        if (plain != null) t.Lyrics = plain;
        return t;
    }

    [Fact]
    public async Task Upgrade_LrclibPlainAnswerFirst_DoesNotHideALaterSourcesSyncedAnswer()
    {
        var t = NewTrack(plain: "my own words");
        var outcomes = new List<string>();

        var s = await Service(new Source("LRCLIB", LrclibPlainOnly), new Source("B", LineSynced))
            .FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), new SyncProgress(p => outcomes.Add(p.Outcome)), CancellationToken.None);

        Assert.Equal(1, s.Synced);
        Assert.Equal(Lrc, File.ReadAllText(Path.ChangeExtension(t.FilePath!, ".lrc")).Replace("\r\n", "\n"));
        Assert.Equal(new[] { "upgraded to synced" }, outcomes);
    }

    [Fact]
    public async Task Upgrade_OnlyAPlainAnswer_ForAPlainSong_SavesNothing()
    {
        var t = NewTrack(plain: "my own words");
        var outcomes = new List<string>();

        var s = await Service(new Source("LRCLIB", LrclibPlainOnly))
            .FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), new SyncProgress(p => outcomes.Add(p.Outcome)), CancellationToken.None);

        Assert.Equal((0, 0, 0), (s.Synced, s.PlainOnly, s.Upgraded));
        Assert.False(File.Exists(Path.ChangeExtension(t.FilePath!, ".lrc")));
        Assert.Equal("my own words", t.Lyrics);
        Assert.Equal(new[] { "nothing better" }, outcomes);
    }

    [Fact]
    public async Task Guard_AFindThatWouldBeSavedAsPlain_NeverCountsAsAnUpgrade()
    {
        // Times lines in its Lyricsfile but carries no LRC text: what gets written is its plain text.
        var lyricsfileOnly = new LrcLibResult { PlainLyrics = "someone else's words", Lyricsfile = LineLyricsfile, HasWordSync = false };
        var t = NewTrack(plain: "my own words");

        var s = await Service(new Source("A", () => lyricsfileOnly))
            .FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), null, CancellationToken.None);

        Assert.Equal((0, 0, 0), (s.Synced, s.PlainOnly, s.Upgraded));
        Assert.Equal("my own words", t.Lyrics);
        Assert.False(File.Exists(Path.ChangeExtension(t.FilePath!, ".lrc")));
    }

    // ── #216: embedded synced lyrics, real files ──

    private static TagLib.Ogg.XiphComment Xiph(TagLib.File f) => (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);

    private Track Scan(string ext, Action<TagLib.File> tag)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, ext, tag);
        var track = new MetadataService().ReadTrackMetadata(path)!;
        track.SourceType = SourceType.Local;
        return track;
    }

    public static TheoryData<string, string, string> EmbeddedCases => new()
    {
        { "flac", "word", "LYRICS" },
        { "flac", "line", "LYRICS" },
        { "flac", "line", "SYNCEDLYRICS" },
        { "mp3", "line", "USLT" },
        { "mp3", "word", "USLT" },
        { "mp3", "line", "SYLT" },
        { "m4a", "line", "lyr" },
        { "m4a", "word", "lyr" },
    };

    private Track EmbeddedTrack(string ext, string kind, string field)
    {
        var text = kind == "word" ? Elrc : Lrc;
        return Scan(ext, f =>
        {
            switch (field)
            {
                case "SYNCEDLYRICS": Xiph(f).SetField("SYNCEDLYRICS", text); break;
                case "SYLT":
                    ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true)).AddFrame(
                        new TagLib.Id3v2.SynchronisedLyricsFrame("", "eng", TagLib.Id3v2.SynchedTextType.Lyrics)
                        {
                            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds,
                            Text = new[] { new TagLib.Id3v2.SynchedText(1000, "first line"), new TagLib.Id3v2.SynchedText(2000, "second line") },
                        });
                    break;
                default: f.Tag.Lyrics = text; break;
            }
        });
    }

    [AvaloniaTheory]
    [MemberData(nameof(EmbeddedCases))]
    public async Task DialogRows_ShowEmbeddedSyncedLyricsAsSynced(string ext, string kind, string field)
    {
        var track = EmbeddedTrack(ext, kind, field);
        var vm = new BulkLyricsViewModel(new[] { track }, new NoopService(), remove: false);

        await vm.RowStatusesLoaded;
        Dispatcher.UIThread.RunJobs();

        var expected = Noctis.Localization.Loc.T(kind == "word" ? "BulkLyrics.HasWordSynced" : "BulkLyrics.HasLineSynced");
        Assert.Equal(expected, vm.Rows[0].Status);
    }

    [AvaloniaFact]
    public async Task DialogRows_EmbeddedPlainLyrics_StayPlain()
    {
        var track = Scan("flac", f => f.Tag.Lyrics = Plain);
        var vm = new BulkLyricsViewModel(new[] { track }, new NoopService(), remove: false);

        await vm.RowStatusesLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Noctis.Localization.Loc.T("BulkLyrics.HasPlain"), vm.Rows[0].Status);
    }

    [Theory]
    [MemberData(nameof(EmbeddedCases))]
    public async Task Upgrade_EmbeddedSyncedLyrics_AreNeverReplacedByAPlainAnswer(string ext, string kind, string field)
    {
        var track = EmbeddedTrack(ext, kind, field);
        var before = (track.Lyrics, track.SyncedLyrics);
        var outcomes = new List<string>();

        var s = await Service(new Source("LRCLIB", LrclibPlainOnly))
            .FetchAsync(new[] { track }, new LyricsBulkOptions(Upgrade: true, Recheck: true), new SyncProgress(p => outcomes.Add(p.Outcome)), CancellationToken.None);

        Assert.Equal((0, 0), (s.Synced, s.PlainOnly));
        Assert.Equal(before, (track.Lyrics, track.SyncedLyrics));
        Assert.Empty(_metadata.LyricsWrites);
        Assert.False(File.Exists(Path.ChangeExtension(track.FilePath!, ".lrc")));
        Assert.Single(outcomes);
        Assert.Contains(outcomes[0], new[] { "already word-synced", "nothing better" });
    }

    private sealed class SyncProgress : IProgress<LyricsBulkProgress>
    {
        private readonly Action<LyricsBulkProgress> _report;
        public SyncProgress(Action<LyricsBulkProgress> report) => _report = report;
        public void Report(LyricsBulkProgress value) { lock (this) _report(value); }
    }

    private sealed class NoopService : ILyricsBulkService
    {
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
