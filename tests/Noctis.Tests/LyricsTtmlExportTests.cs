using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (nutf!xx): Light Cone reads LRC and TTML but not ELRC, so Lyrics Studio's word
/// timings never reached it. "Also save as TTML" writes a .ttml beside the song. Along the way:
/// the lyrics page reads .lyricsfile and .ttml before .elrc and .lrc, so a save under one of
/// those (Studio, or synced lyrics changed in Edit Info) stayed invisible.
/// </summary>
public class LyricsTtmlExportTests : IDisposable
{
    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-ttml-" + Guid.NewGuid().ToString("N"));

    public LyricsTtmlExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static AlignedLine Words(params (string Text, double Start, double End)[] words) =>
        new(string.Join(' ', words.Select(w => w.Text)), S(words[0].Start), S(words[^1].End),
            words.Select(w => new AlignedWord(w.Text, S(w.Start), S(w.End))).ToList(), 0.9, false);

    private static AlignedLine LineOnly(string text, double start, double end) =>
        new(text, S(start), S(end), Array.Empty<AlignedWord>(), 1, false);

    /// <summary>Word-timed lines (punctuation, an ampersand, angle brackets), a line-level line, one past the hour.</summary>
    private static List<AlignedLine> Song() => new()
    {
        Words(("Hello,", 12.345, 12.8), ("world!", 12.8, 13.4)),
        LineOnly("No word times here", 15.0, 17.5),
        Words(("Rock", 18.05, 18.4), ("&", 18.4, 18.6), ("<roll>", 18.6, 19.25)),
        Words(("Late", 3904.5, 3905.0), ("night", 3905.0, 3906.1)),
    };

    // ── The file ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtml_IsWellFormedTtml_WithWordSpans()
    {
        var ttml = TimedLyricsBuilder.BuildTtml(Song());

        var doc = XDocument.Parse(ttml);
        XNamespace tt = "http://www.w3.org/ns/ttml";
        Assert.Equal(tt + "tt", doc.Root!.Name);
        Assert.Equal("Word", doc.Root.Attribute(XNamespace.Get("http://music.apple.com/lyric-ttml-internal") + "timing")!.Value);
        var ps = doc.Descendants(tt + "p").ToList();
        Assert.Equal(4, ps.Count);
        Assert.Equal("00:00:12.345", ps[0].Attribute("begin")!.Value);
        Assert.Equal(new[] { "Hello,", "world!" }, ps[0].Elements(tt + "span").Select(s => s.Value));
        Assert.Empty(ps[1].Elements(tt + "span"));
        Assert.Equal("No word times here", ps[1].Value);
        Assert.Equal(new[] { "Rock", "&", "<roll>" }, ps[2].Elements(tt + "span").Select(s => s.Value));
        Assert.Equal("01:05:04.500", ps[3].Attribute("begin")!.Value);
    }

    [Fact]
    public void BuildTtml_ReadsBackThroughTheLyricsPageParser_WithTheSameTimings()
    {
        var song = Song();
        var (lines, _) = TtmlParser.Parse(TimedLyricsBuilder.BuildTtml(song));

        Assert.NotNull(lines);
        Assert.Equal(song.Select(l => l.Text), lines!.Select(l => l.Text));
        Assert.Equal(song.Select(l => (TimeSpan?)l.Start), lines.Select(l => l.Timestamp));
        for (var i = 0; i < song.Count; i++)
        {
            if (song[i].Words.Count == 0)
            {
                Assert.False(lines[i].Words is { Count: > 0 }, $"line {i} has no word timings");
                continue;
            }
            // The parser keeps the space between words on the word ("Hello, "), as for Apple's files.
            Assert.Equal(song[i].Words.Select(w => w.Text), lines[i].Words!.Select(w => w.Text.Trim()));
            Assert.Equal(song[i].Words.Select(w => w.Start), lines[i].Words!.Select(w => w.Start));
            Assert.Equal(song[i].Words.Select(w => (TimeSpan?)w.End), lines[i].Words!.Select(w => w.End));
        }
    }

    /// <summary>
    /// Live check (The Bees Knees): the .elrc showed "(Yeah)" as the small background row under
    /// its line, the first .ttml inline at full size. Both files now read back the same rows.
    /// </summary>
    [Fact]
    public void BuildTtml_AdLibs_ReadBackAsTheSameBackgroundRowsAsTheElrc()
    {
        var song = new List<AlignedLine>
        {
            Words(("Come", 20.0, 20.3), ("take", 20.3, 20.6), ("it", 20.6, 20.9), ("(Yeah)", 21.0, 21.5)),
            Words(("It's", 22.0, 22.3), ("my", 22.3, 22.6), ("year", 22.6, 23.0)),
            Words(("(Uh-huh,", 23.1, 23.4), ("uh-huh)", 23.4, 23.8)),
            Words(("Late", 24.0, 24.4), ("(I", 24.4, 24.6), ("will)", 24.6, 24.9), ("night", 24.9, 25.4)),
            Words(("Open", 26.0, 26.4), ("(paren", 26.4, 26.8), ("never", 26.8, 27.2)),
        };

        var fromElrc = LyricsViewModel.ParseLrcContent(TimedLyricsBuilder.BuildElrc(song));
        var (fromTtml, _) = TtmlParser.Parse(TimedLyricsBuilder.BuildTtml(song));

        static string[] Row(IReadOnlyList<WordTiming>? words) =>
            words?.Select(w => $"{w.Text.Trim()}@{w.Start.TotalMilliseconds}").ToArray() ?? Array.Empty<string>();
        Assert.NotNull(fromTtml);
        Assert.Equal(fromElrc.Count, fromTtml!.Count);
        for (var i = 0; i < fromElrc.Count; i++)
        {
            Assert.Equal(Row(fromElrc[i].Words), Row(fromTtml[i].Words));
            Assert.Equal(Row(fromElrc[i].BackgroundWords), Row(fromTtml[i].BackgroundWords));
        }
        // And the rows are what the rules say: the fully parenthesised line joined the one before.
        Assert.Equal(new[] { "(Yeah)@21000" }, Row(fromTtml[0].BackgroundWords));
        Assert.Equal(new[] { "(Uh-huh,@23100", "uh-huh)@23400" }, Row(fromTtml[1].BackgroundWords));
        Assert.Equal(new[] { "Late@24000", "night@24900" }, Row(fromTtml[2].Words));
        Assert.Empty(Row(fromTtml[3].BackgroundWords)); // unmatched "(" leaves the line alone
    }

    [Fact]
    public void BuildTtml_LineLevelOnly_SaysLine_AndEmptyIsStillValid()
    {
        var line = TimedLyricsBuilder.BuildTtml(new[] { LineOnly("one", 1, 2), LineOnly("two", 3, 4) });
        Assert.Contains("itunes:timing=\"Line\"", line);
        Assert.Equal(new TimeSpan?[] { S(1), S(3) }, TtmlParser.Parse(line).Lines!.Select(l => l.Timestamp));

        XDocument.Parse(TimedLyricsBuilder.BuildTtml(Array.Empty<AlignedLine>()));
    }

    // ── LyricsWriter ─────────────────────────────────────────────────────────

    private (LyricsWriter Writer, Track Track, List<string> Trashed) NewWriter()
    {
        var audio = Path.Combine(_dir, "song.flac");
        File.WriteAllText(audio, "x");
        var trashed = new List<string>();
        var writer = new LyricsWriter(new NoTags(), null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            TrashFile = p => { trashed.Add(Path.GetFileName(p)); File.Delete(p); return true; },
        };
        return (writer, new Track { Title = "Song", FilePath = audio, SourceType = SourceType.Local }, trashed);
    }

    private string Side(string ext) => Path.Combine(_dir, "song" + ext);

    [Fact]
    public void Writer_WithTtml_WritesItBesideElrcAndLrc_AndRemoveTakesItAway()
    {
        var song = Song();
        var (writer, track, _) = NewWriter();
        var ttml = TimedLyricsBuilder.BuildTtml(song);

        writer.SaveDetailed(track, null, TimedLyricsBuilder.BuildElrc(song), embedInTags: false, replaceForeignSidecar: true, ttml);

        Assert.True(File.Exists(Side(".elrc")));
        Assert.True(File.Exists(Side(".lrc")));
        Assert.Equal(TtmlParser.Parse(ttml).Lines!.Count, TtmlParser.Parse(File.ReadAllText(Side(".ttml"))).Lines!.Count);

        writer.Remove(track, clearTags: false);
        Assert.False(File.Exists(Side(".ttml")), "the app's own .ttml goes with Remove");
    }

    [Fact]
    public void Writer_StudioSave_ReplacesALeftoverTtmlAndLyricsfile_ThatWouldHideIt()
    {
        var (writer, track, trashed) = NewWriter();
        File.WriteAllText(Side(".ttml"), "<tt/>");
        File.WriteAllText(Side(".lyricsfile"), "lines: []");

        var outcome = writer.SaveDetailed(track, null, "[00:01.00]new", embedInTags: false, replaceForeignSidecar: true);

        Assert.False(File.Exists(Side(".ttml")));
        Assert.False(File.Exists(Side(".lyricsfile")));
        Assert.Contains("song.ttml", trashed);
        Assert.True(outcome.ReplacedForeignSidecar);
    }

    [Fact]
    public void Writer_BulkSave_LeavesTheUsersTtmlAndLyricsfileAlone()
    {
        var (writer, track, trashed) = NewWriter();
        File.WriteAllText(Side(".ttml"), "<tt/>");
        File.WriteAllText(Side(".lyricsfile"), "lines: []");

        writer.SaveDetailed(track, null, "[00:01.00]new", embedInTags: false, replaceForeignSidecar: false);

        Assert.True(File.Exists(Side(".ttml")));
        Assert.True(File.Exists(Side(".lyricsfile")));
        Assert.Empty(trashed);
    }

    // ── Lyrics Studio ────────────────────────────────────────────────────────

    [Fact]
    public void FormatDetection_CountsATtmlOnlySong_AsTheLyricsPageReadsIt()
    {
        var words = new Track { FilePath = Path.Combine(_dir, "words.flac") };
        var lines = new Track { FilePath = Path.Combine(_dir, "lines.flac") };
        var none = new Track { FilePath = Path.Combine(_dir, "none.flac") };
        File.WriteAllText(Path.Combine(_dir, "words.ttml"), TimedLyricsBuilder.BuildTtml(Song()));
        File.WriteAllText(Path.Combine(_dir, "lines.ttml"), TimedLyricsBuilder.BuildTtml(new[] { LineOnly("one", 1, 2) }));
        File.WriteAllText(Path.Combine(_dir, "none.ttml"), "not xml at all");

        Assert.Equal(LyricsFormat.Elrc, ExistingLyricsLoader.DetectFormat(words));
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormat(lines));
        Assert.Equal(LyricsFormat.None, ExistingLyricsLoader.DetectFormat(none));
        // The library-wide pass (Studio stats, picker) agrees with the per-song check.
        Assert.Equal(new[] { LyricsFormat.Elrc, LyricsFormat.Lrc, LyricsFormat.None },
            ExistingLyricsLoader.DetectFormats(new[] { words, lines, none }));
    }

    [Fact]
    public void ExistingLyricsFiles_NamesTheTtmlAndLyricsfileASaveReplaces()
    {
        var track = new Track { FilePath = Path.Combine(_dir, "song.flac") };
        File.WriteAllText(Side(".ttml"), "<tt/>");
        File.WriteAllText(Side(".lyricsfile"), "x");

        var found = LyricsStudioViewModel.ExistingLyricsFiles(track);

        Assert.Contains("“song.ttml”", found);
        Assert.Contains("“song.lyricsfile”", found);
    }

    // ── Edit Info (MetadataViewModel) ────────────────────────────────────────

    private (MetadataViewModel Vm, Track Track, List<string> Trashed) NewMetadata(string? synced)
    {
        var audio = Path.Combine(_dir, "song.flac");
        File.WriteAllText(audio, "x");
        var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "A", Album = "B", FilePath = audio, SyncedLyrics = synced ?? string.Empty };
        var trashed = new List<string>();
        var vm = new MetadataViewModel(track, new NoTags(), LibraryWith(track),
            new TestPersistenceService(), new FakeAnimatedCoverService(), albumScoped: false, albumTracks: null)
        {
            TrashFile = p => { trashed.Add(Path.GetFileName(p)); File.Delete(p); return true; },
        };
        return (vm, track, trashed);
    }

    private static FakeLibraryService LibraryWith(Track track)
    {
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        return lib;
    }

    [AvaloniaFact]
    public async Task EditInfo_ChangedSyncedLyrics_ClearTheElrcAndTtmlThatWouldHideThem()
    {
        var (vm, _, trashed) = NewMetadata("[00:01.00]<00:01.00>old <00:01.50>words<00:02.00>");
        File.WriteAllText(Side(".elrc"), "[00:01.00]<00:01.00>old <00:01.50>words<00:02.00>");
        File.WriteAllText(Side(".ttml"), "<tt/>");

        vm.ImportLyricsText("[00:03.00]new line", "new.lrc");
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("[00:03.00]new line", File.ReadAllText(Side(".lrc")));
        Assert.False(File.Exists(Side(".elrc")));
        Assert.False(File.Exists(Side(".ttml")));
        Assert.Equal(new[] { "song.ttml", "song.elrc" }, trashed);
    }

    [AvaloniaFact]
    public async Task EditInfo_UnrelatedEdit_KeepsTheWordTimingFiles()
    {
        const string Elrc = "[00:01.00]<00:01.00>old <00:01.50>words<00:02.00>";
        var (vm, _, trashed) = NewMetadata(Elrc);
        File.WriteAllText(Side(".elrc"), Elrc);
        File.WriteAllText(Side(".ttml"), "<tt/>");

        vm.Year = "2001";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Side(".elrc")));
        Assert.True(File.Exists(Side(".ttml")));
        Assert.Empty(trashed);
    }

    [AvaloniaFact]
    public async Task EditInfo_RemovedSyncedLyrics_AlsoClearTheElrc()
    {
        const string Elrc = "[00:01.00]<00:01.00>old <00:01.50>words<00:02.00>";
        var (vm, _, trashed) = NewMetadata(Elrc);
        File.WriteAllText(Side(".elrc"), Elrc);
        File.WriteAllText(Side(".lrc"), "[00:01.00]old words");

        vm.RemoveSyncedLyricsCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(File.Exists(Side(".lrc")));
        Assert.False(File.Exists(Side(".elrc")));
        Assert.Contains("song.elrc", trashed);
    }

    /// <summary>Tag I/O that touches nothing: these tests are about the sidecars.</summary>
    private sealed class NoTags : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        public bool ClearYear(string filePath) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath,
            Noctis.Services.AdvancedTagIO.AdvancedFields fields,
            Noctis.Services.AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
