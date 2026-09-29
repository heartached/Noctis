using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio output end to end: what TimedLyricsBuilder writes for LRC and ELRC, what
/// LyricsWriter puts on disk, and that the lyrics page parser, the Studio's own loader and
/// the format detector all read it back with the same line and word timings.
/// </summary>
public class LyricsStudioFormatRoundTripTests : IDisposable
{
    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-rt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AlignedLine Words(params (string Text, double Start, double End)[] words) =>
        new(string.Join(' ', words.Select(w => w.Text)), S(words[0].Start), S(words[^1].End),
            words.Select(w => new AlignedWord(w.Text, S(w.Start), S(w.End))).ToList(), 0.9, false);

    private static AlignedLine LineOnly(string text, double start, double end) =>
        new(text, S(start), S(end), Array.Empty<AlignedWord>(), 1, false);

    /// <summary>A small song: word-timed lines (with punctuation), a line-level line, and one past the hour.</summary>
    private static List<AlignedLine> Song() => new()
    {
        Words(("Hello,", 12.34, 12.80), ("world!", 12.80, 13.40)),
        LineOnly("No word times here", 15.00, 17.50),
        Words(("I'm", 18.05, 18.40), ("mentally", 18.40, 19.00), ("weak,", 19.00, 19.60), ("incomplete", 19.60, 20.25)),
        Words(("Late", 3904.50, 3905.00), ("night", 3905.00, 3906.10)),
    };

    // ── What the files look like ─────────────────────────────────────────────

    [Fact]
    public void BuildElrc_WritesLineStampWordTagsAndTrailingEndTag()
    {
        var elrc = TimedLyricsBuilder.BuildElrc(Song());

        Assert.Equal(
            "[00:12.34]<00:12.34>Hello, <00:12.80>world!<00:13.40>\n" +
            "[00:15.00]No word times here\n" +
            "[00:18.05]<00:18.05>I'm <00:18.40>mentally <00:19.00>weak, <00:19.60>incomplete<00:20.25>\n" +
            "[65:04.50]<65:04.50>Late <65:05.00>night<65:06.10>",
            elrc);
    }

    [Fact]
    public void BuildLrc_FromWordLevelLines_IsPlainLineLevel()
    {
        var lrc = TimedLyricsBuilder.BuildLrc(Song());

        Assert.Equal(
            "[00:12.34]Hello, world!\n[00:15.00]No word times here\n[00:18.05]I'm mentally weak, incomplete\n[65:04.50]Late night",
            lrc);
        Assert.DoesNotContain('<', lrc);
    }

    [Fact]
    public void FormatTimestamp_TruncatesToCentiseconds_ClampsNegative_KeepsMinutesPastTheHour()
    {
        Assert.Equal("00:12.34", TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromMilliseconds(12_349)));
        Assert.Equal("00:00.00", TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromSeconds(-3)));
        Assert.Equal("65:04.50", TimedLyricsBuilder.FormatTimestamp(TimeSpan.FromSeconds(3904.5)));
    }

    [Fact]
    public void Builders_SkipBlankLines_AndSortByStart()
    {
        var lines = new List<AlignedLine> { LineOnly("second", 5, 6), LineOnly("  ", 1, 2), LineOnly("first", 3, 4) };

        Assert.Equal("[00:03.00]first\n[00:05.00]second", TimedLyricsBuilder.BuildLrc(lines));
        Assert.Equal("[00:03.00]first\n[00:05.00]second", TimedLyricsBuilder.BuildElrc(lines));
    }

    // ── Lyrics page parser ───────────────────────────────────────────────────

    [Fact]
    public void LyricsPage_ReadsStudioElrc_WordByWord()
    {
        var song = Song();
        var page = LyricsViewModel.ParseLrcContent(TimedLyricsBuilder.BuildElrc(song));

        Assert.Equal(song.Count, page.Count);
        for (var i = 0; i < song.Count; i++)
        {
            Assert.Equal(song[i].Start, page[i].Timestamp);
            if (song[i].Words.Count == 0)
            {
                Assert.False(page[i].HasWords);
                Assert.Equal(song[i].Text, page[i].Text);
                continue;
            }
            var words = page[i].Words!;
            Assert.Equal(song[i].Words.Count, words.Count);
            for (var w = 0; w < words.Count; w++)
            {
                Assert.Equal(song[i].Words[w].Text, words[w].Text.Trim());
                Assert.Equal(song[i].Words[w].Start, words[w].Start);
                Assert.Equal(song[i].Words[w].End, words[w].End);
            }
            Assert.Equal(song[i].Words[^1].End, page[i].EndTimestamp);
        }
    }

    [Fact]
    public void LyricsPage_ReadsStudioLrc_LineByLine()
    {
        var song = Song();
        var page = LyricsViewModel.ParseLrcContent(TimedLyricsBuilder.BuildLrc(song));

        Assert.Equal(song.Select(l => (TimeSpan?)l.Start), page.Select(l => l.Timestamp));
        Assert.All(page, l => Assert.False(l.HasWords));
    }

    [Fact]
    public void AlternateElrcStyle_FirstWordTimedByLineStamp_OnPageAndInLoader()
    {
        // "[t]word <t>word": the first word has no tag of its own.
        const string alt = "[00:12.34]Hello <00:12.80>big <00:13.10>world<00:13.40>";

        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(null, alt));

        var page = LyricsViewModel.ParseLrcContent(alt).Single();
        Assert.Equal("Hello big world", page.Text);
        Assert.Equal(3, page.Words!.Count);
        Assert.Equal("Hello", page.Words[0].Text.Trim());
        Assert.Equal(S(12.34), page.Words[0].Start);
        Assert.Equal(S(12.80), page.Words[0].End);
        Assert.Equal(S(12.80), page.Words[1].Start);

        var loaded = ExistingLyricsLoader.ParseTimed(alt).Single();
        Assert.Equal(S(12.34), loaded.Start);
        Assert.Equal(new[] { "Hello", "big", "world" }, loaded.Words.Select(w => w.Text));
        Assert.Equal(S(12.34), loaded.Words[0].Start);
        Assert.Equal(S(13.40), loaded.Words[^1].End);
    }

    // ── Detector ─────────────────────────────────────────────────────────────

    [Fact]
    public void Detector_ClassifiesStudioOutput()
    {
        var song = Song();
        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(null, TimedLyricsBuilder.BuildElrc(song)));
        Assert.Equal(LyricsFormat.Lrc, LyricsFormatDetector.Detect(null, TimedLyricsBuilder.BuildLrc(song)));
        // ELRC chosen, but no line carries word times: that text is plain LRC.
        var lineLevel = new[] { LineOnly("One", 1, 2), LineOnly("Two", 3, 4) };
        Assert.Equal(LyricsFormat.Lrc, LyricsFormatDetector.Detect(null, TimedLyricsBuilder.BuildElrc(lineLevel)));
        Assert.Equal(LyricsFormat.Plain, LyricsFormatDetector.Detect(TimedLyricsBuilder.BuildPlain(song), null));
    }

    // ── Studio loader (re-open in the Studio) ────────────────────────────────

    [Fact]
    public void Loader_ParseTimed_ReadsBackTheSameLineAndWordTimings()
    {
        var song = Song();
        var loaded = ExistingLyricsLoader.ParseTimed(TimedLyricsBuilder.BuildElrc(song));

        Assert.Equal(song.Count, loaded.Count);
        for (var i = 0; i < song.Count; i++)
        {
            Assert.Equal(song[i].Text, loaded[i].Text);
            Assert.Equal(song[i].Start, loaded[i].Start);
            Assert.Equal(song[i].Words.Select(w => (w.Text, w.Start, w.End)), loaded[i].Words.Select(w => (w.Text, w.Start, w.End)));
        }
        // And building again from what was loaded is byte-identical.
        Assert.Equal(TimedLyricsBuilder.BuildElrc(song), TimedLyricsBuilder.BuildElrc(loaded));
    }

    // ── Writer → disk → loader / page ────────────────────────────────────────

    private (LyricsWriter Writer, Track Track, string Lrc, string Elrc) NewWriter()
    {
        Directory.CreateDirectory(_dir);
        var audio = Path.Combine(_dir, "song.flac");
        File.WriteAllText(audio, "x");
        var writer = new LyricsWriter(new StubMetadata(), null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            TrashFile = p => { File.Delete(p); return true; },
        };
        var track = new Track { Title = "Song", FilePath = audio, SourceType = SourceType.Local };
        return (writer, track, Path.ChangeExtension(audio, ".lrc"), Path.ChangeExtension(audio, ".elrc"));
    }

    [Fact]
    public void SaveElrc_WritesElrcPlusLineLevelLrc_AndBothReadBack()
    {
        var song = Song();
        var (writer, track, lrc, elrc) = NewWriter();
        var synced = TimedLyricsBuilder.BuildElrc(song);

        writer.SaveDetailed(track, TimedLyricsBuilder.BuildPlain(song), synced, embedInTags: false, replaceForeignSidecar: true);

        Assert.Equal(LyricsFormat.Elrc, ExistingLyricsLoader.DetectFormat(track));
        var loaded = ExistingLyricsLoader.Load(track)!;
        Assert.Equal(".elrc file", loaded.Origin);
        Assert.Equal(synced, TimedLyricsBuilder.BuildElrc(loaded.Lines));

        // The .lrc projection is exactly what the LRC builder writes.
        Assert.Equal(TimedLyricsBuilder.BuildLrc(song), File.ReadAllText(lrc).Replace("\r\n", "\n"));
        Assert.Equal(song.Select(l => (TimeSpan?)l.Start), LyricsViewModel.ParseLrcContent(File.ReadAllText(lrc)).Select(l => l.Timestamp));

        // The lyrics page reads the .elrc (it outranks the .lrc) word by word.
        var page = LyricsViewModel.ParseLrcContent(File.ReadAllText(elrc));
        Assert.Equal(3, page.Count(l => l.HasWords));
    }

    [Fact]
    public void SaveLrc_AfterElrc_RemovesOwnElrc_SoTheNewLrcIsWhatLoads()
    {
        var song = Song();
        var (writer, track, lrc, elrc) = NewWriter();
        writer.SaveDetailed(track, null, TimedLyricsBuilder.BuildElrc(song), false, true);
        Assert.True(File.Exists(elrc));

        writer.SaveDetailed(track, null, TimedLyricsBuilder.BuildLrc(song), false, true);

        Assert.False(File.Exists(elrc));
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormat(track));
        var loaded = ExistingLyricsLoader.Load(track)!;
        Assert.Equal(".lrc file", loaded.Origin);
        Assert.All(loaded.Lines, l => Assert.Empty(l.Words));
        Assert.Equal(song.Select(l => l.Start), loaded.Lines.Select(l => l.Start));
    }

    [Fact]
    public void SaveElrc_WithNoWordTimesAtAll_IsTreatedAsLrc()
    {
        var (writer, track, lrc, elrc) = NewWriter();
        var synced = TimedLyricsBuilder.BuildElrc(new[] { LineOnly("One", 1, 2), LineOnly("Two", 3, 4) });

        writer.SaveDetailed(track, null, synced, false, true);

        Assert.False(File.Exists(elrc));
        Assert.Equal("[00:01.00]One\n[00:03.00]Two", File.ReadAllText(lrc).Replace("\r\n", "\n"));
        Assert.Equal("One" + Environment.NewLine + "Two", track.Lyrics);
    }

    [Fact]
    public void SaveElrc_PlainTextDerivedFromSynced_HasNoTags()
    {
        var (writer, track, _, _) = NewWriter();
        writer.SaveDetailed(track, null, TimedLyricsBuilder.BuildElrc(Song()), false, true);

        Assert.DoesNotContain('<', track.Lyrics);
        Assert.DoesNotContain('[', track.Lyrics);
        Assert.StartsWith("Hello, world!", track.Lyrics);
    }

    // ── Review model → saved ELRC stays valid ────────────────────────────────

    private static void AssertValidElrc(string elrc)
    {
        foreach (var line in ExistingLyricsLoader.ParseTimed(elrc))
        {
            for (var w = 0; w < line.Words.Count; w++)
            {
                Assert.True(line.Words[w].Start >= line.Start, $"word before its line: {line.Text}");
                Assert.True(line.Words[w].End >= line.Words[w].Start, $"word ends before it starts: {line.Text}");
                if (w > 0) Assert.True(line.Words[w].Start >= line.Words[w - 1].Start, $"words out of order: {line.Text}");
            }
            if (line.Words.Count > 0) Assert.Equal(line.Start, line.Words[0].Start);
        }
    }

    [Fact]
    public void Review_NudgeTapShiftAndSetEnd_KeepSavedElrcMonotonic()
    {
        var reviews = Song().Select(l => new ReviewLine(l)).ToList();
        var first = reviews[0];
        first.NudgeWord(first.Words[1], TimeSpan.FromSeconds(-5));   // clamped after word 0
        first.NudgeWord(first.Words[0], TimeSpan.FromSeconds(+5));   // clamped before word 1
        first.SetEnd(S(1));                                           // clamped to the last word start
        reviews[1].TapWord(0, S(15.5));                               // line-level line becomes word-timed
        reviews[1].TapWord(2, S(14));                                 // cannot land before word 1
        reviews[2].TapWord(1, S(30));                                 // pushes later words along
        foreach (var r in reviews) r.Shift(TimeSpan.FromSeconds(-20)); // global nudge past zero

        var lines = reviews.Select(r => r.ToAlignedLine()).ToList();
        var elrc = TimedLyricsBuilder.BuildElrc(lines);

        AssertValidElrc(elrc);
        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(null, elrc));
        Assert.DoesNotContain("-", string.Concat(elrc.Split('\n').Select(l => l[..10])));
        Assert.Equal(4, LyricsViewModel.ParseLrcContent(elrc).Count(l => l.HasWords));
    }

    [Fact]
    public void Review_InterpolatedLine_SavesLineLevel_UntilTheUserTimesIt()
    {
        var spread = new AlignedLine("Nobody heard this", S(40), S(46),
            new[] { new AlignedWord("Nobody", S(40), S(42)), new AlignedWord("heard", S(42), S(44)), new AlignedWord("this", S(44), S(46)) },
            0, Interpolated: true);
        var review = new ReviewLine(spread);

        Assert.False(review.HasWordTimings);
        Assert.Equal("[00:40.00]Nobody heard this", TimedLyricsBuilder.BuildElrc(new[] { review.ToAlignedLine() }));

        review.TapWord(1, S(41.2));
        var timed = review.ToAlignedLine();
        Assert.False(timed.Interpolated);
        Assert.Equal(3, timed.Words.Count);
        // Rebuilt from its own export (reselecting the song / a restored draft) it keeps the words.
        Assert.True(new ReviewLine(timed).HasWordTimings);
    }

    // ── Aligner output is monotonic ──────────────────────────────────────────

    [Fact]
    public void Aligner_GapWordBetweenCloseAnchors_NeverStartsAfterTheNextAnchor()
    {
        // Whisper often reports zero-length words; the aligner pads them to 120 ms, which
        // can run past the next heard word. The unheard word in between must stay in order.
        var heard = new[]
        {
            new RecognizedWord("might", S(42.00), S(42.00), 0.9f),
            new RecognizedWord("her", S(42.05), S(42.05), 0.9f),
        };

        var line = LyricsAligner.Align(new[] { "might keep her" }, heard, S(60)).Single();

        Assert.Equal(3, line.Words.Count);
        Assert.True(line.Words[1].Start <= line.Words[2].Start, $"{line.Words[1].Start} > {line.Words[2].Start}");
        AssertValidElrc(TimedLyricsBuilder.BuildElrc(new[] { line }));
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
}
