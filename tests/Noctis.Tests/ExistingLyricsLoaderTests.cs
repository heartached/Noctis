using Noctis.Models;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

public class ExistingLyricsLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-ell-" + Guid.NewGuid().ToString("N"));
    private readonly string _audio;

    private const string Lrc = "[ar:Someone]\n[00:08.99]It's fire\n[00:13.05]Huh, as we go on\n[00:20.00]";
    private const string Elrc = "[00:10.98]<00:10.98>Yo <00:11.24>la <00:11.39>conocí<00:11.75>\n[00:13.71]<00:13.71>Ella <00:14.07>sabe<00:14.51>";

    public ExistingLyricsLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        _audio = Path.Combine(_dir, "song.flac");
        // A real (silent) FLAC: lyrics count as "embedded tags" only when its tags carry them.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence.flac"), _audio);
    }

    /// <summary>Puts <paramref name="lyrics"/> in the FLAC's LYRICS tag, as another tagger would.</summary>
    private void TagLyrics(string lyrics)
    {
        using var f = TagLib.File.Create(_audio);
        f.Tag.Lyrics = lyrics;
        f.Save();
    }

    private Track NewTrack() => new() { Title = "Song", FilePath = _audio, SourceType = SourceType.Local };
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    [Fact]
    public void Detect_TimedTextInThePlainLyricsTag_CountsAsLrc()
    {
        // The scanner stores the file's USLT / LYRICS frame in Track.Lyrics; embedded LRC
        // therefore arrives in the PLAIN field and used to be labelled "plain only".
        Assert.Equal(LyricsFormat.Lrc, LyricsFormatDetector.Detect(Lrc, null));
        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(Elrc, null));
        Assert.Equal(LyricsFormat.Plain, LyricsFormatDetector.Detect("just words\nno times", null));
    }

    [Fact]
    public void Load_EmbeddedLrcInThePlainField_IsExistingLyrics()
    {
        TagLyrics(Lrc);
        var track = NewTrack();
        track.Lyrics = Lrc;

        var existing = ExistingLyricsLoader.Load(track);

        Assert.NotNull(existing);
        Assert.Equal(LyricsFormat.Lrc, existing!.Format);
        Assert.Equal("embedded tags", existing.Origin);
        Assert.Equal(2, existing.Lines.Count);
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormat(track));
        Assert.Equal(new[] { LyricsFormat.Lrc }, ExistingLyricsLoader.DetectFormats(new[] { track }));
    }

    [Fact]
    public void ParseTimed_LineLevel_KeepsLinesWithoutWords_EndIsNextStart()
    {
        var lines = ExistingLyricsLoader.ParseTimed(Lrc);

        Assert.Equal(2, lines.Count); // header tag and empty end marker dropped
        Assert.Equal("It's fire", lines[0].Text);
        Assert.Equal(S(8.99), lines[0].Start);
        Assert.Equal(S(13.05), lines[0].End);
        Assert.Empty(lines[0].Words);
        Assert.False(ExistingLyricsLoader.HasWordTimings(lines));
    }

    [Fact]
    public void ParseTimed_WordLevel_ProducesWordsWithEnds()
    {
        var lines = ExistingLyricsLoader.ParseTimed(Elrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Yo la conocí", lines[0].Text);
        Assert.Equal(3, lines[0].Words.Count);
        Assert.Equal(S(11.24), lines[0].Words[1].Start);
        Assert.Equal(S(11.39), lines[0].Words[1].End);
        Assert.Equal(S(11.75), lines[0].Words[2].End); // trailing tag
        Assert.Equal(S(11.75), lines[0].End);
        Assert.True(ExistingLyricsLoader.HasWordTimings(lines));

        // Round trip through the Studio's own builder is lossless.
        Assert.Equal(Elrc, TimedLyricsBuilder.BuildElrc(lines));
    }

    [Fact]
    public void ParseTimed_CompressedLine_OneEntryPerStamp_SortedByTime()
    {
        var lines = ExistingLyricsLoader.ParseTimed("[00:30.00][00:10.00]Chorus\n[00:20.00]Verse");

        Assert.Equal(new[] { "Chorus", "Verse", "Chorus" }, lines.Select(l => l.Text));
        Assert.Equal(S(10), lines[0].Start);
        Assert.Equal(S(30), lines[2].Start);
    }

    [Fact]
    public void ParseTimed_TextStartingWithAByteOrderMark_KeepsItsFirstLine()
    {
        // Lyrics a tagger copied from a file with its BOM: the lyrics page shows the first line,
        // and the Studio dropped it (its stamp no longer started the line), so a save lost it.
        const string text = "﻿[00:01.00]First line\n[00:02.00]Second line";
        var track = new Track { Title = "Song", SyncedLyrics = text };

        var lines = ExistingLyricsLoader.Load(track)!.Lines;

        Assert.Equal(new[] { "First line", "Second line" }, lines.Select(l => l.Text));
        Assert.Equal(S(1), lines[0].Start);
        Assert.Equal(Noctis.ViewModels.LyricsViewModel.ParseLrcContent(text).Where(l => !l.IsIntroPlaceholder).Select(l => l.Text), lines.Select(l => l.Text));
    }

    [Fact]
    public void Load_PrefersElrcSidecar_ThenLrc_ThenEmbedded()
    {
        TagLyrics("[00:01.00]embedded");
        var track = NewTrack();
        track.SyncedLyrics = "[00:01.00]embedded";
        Assert.Equal("embedded tags", ExistingLyricsLoader.Load(track)!.Origin);
        Assert.Equal(LyricsFormat.Lrc, ExistingLyricsLoader.DetectFormat(track));

        File.WriteAllText(Path.Combine(_dir, "song.lrc"), Lrc);
        var fromLrc = ExistingLyricsLoader.Load(track)!;
        Assert.Equal(".lrc file", fromLrc.Origin);
        Assert.Equal(LyricsFormat.Lrc, fromLrc.Format);

        File.WriteAllText(Path.Combine(_dir, "song.elrc"), Elrc);
        var fromElrc = ExistingLyricsLoader.Load(track)!;
        Assert.Equal(".elrc file", fromElrc.Origin);
        Assert.Equal(LyricsFormat.Elrc, fromElrc.Format);
        Assert.Equal(LyricsFormat.Elrc, ExistingLyricsLoader.DetectFormat(track));
        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(track));
    }

    [Fact]
    public void Load_PlainOnly_ReturnsNull()
    {
        var track = NewTrack();
        track.Lyrics = "just words";
        Assert.Null(ExistingLyricsLoader.Load(track));
        Assert.Equal(LyricsFormat.Plain, ExistingLyricsLoader.DetectFormat(track));
    }

    [Fact]
    public void DetectFormats_BatchMatchesPerTrack_AcrossFoldersAndCasings()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        Track Make(string dir, string stem) { var p = Path.Combine(dir, stem + ".flac"); File.WriteAllText(p, "x"); return new Track { Title = stem, FilePath = p, SourceType = SourceType.Local }; }

        var elrc = Make(_dir, "a"); File.WriteAllText(Path.Combine(_dir, "a.elrc"), Elrc);
        var lrc = Make(_dir, "b"); File.WriteAllText(Path.Combine(_dir, "b.LRC"), Lrc);
        var plain = Make(sub, "c"); plain.Lyrics = "words";
        var none = Make(sub, "d");
        var embedded = Make(sub, "e"); embedded.SyncedLyrics = "[00:01.00]embedded";
        var remote = new Track { Title = "r", FilePath = "http://x/y", SourceType = SourceType.Navidrome };
        var tracks = new[] { elrc, lrc, plain, none, embedded, remote };

        var batch = ExistingLyricsLoader.DetectFormats(tracks);

        Assert.Equal(tracks.Length, batch.Count);
        for (var i = 0; i < tracks.Length; i++)
            Assert.Equal(ExistingLyricsLoader.DetectFormat(tracks[i]), batch[i]);
        Assert.Equal(LyricsFormat.Elrc, batch[0]);
        Assert.Equal(LyricsFormat.Lrc, batch[1]);
        Assert.Equal(LyricsFormat.Plain, batch[2]);
        Assert.Equal(LyricsFormat.None, batch[3]);
        Assert.Equal(LyricsFormat.Lrc, batch[4]);
    }

    [Fact]
    public void DetectFormats_Cancels()
    {
        var tracks = Enumerable.Range(0, 50).Select(i => new Track { FilePath = Path.Combine(_dir, $"t{i}.flac"), SourceType = SourceType.Local }).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExistingLyricsLoader.DetectFormats(tracks, cts.Token));
    }

    /// <summary>
    /// Live 10-19: lyrics picked online in Search Lyrics are kept by Noctis for the song (the
    /// lyrics page fills the library's lyrics fields beside the .elrc/.lrc it writes). With those
    /// files deleted, Lyrics Studio said "From embedded tags · word timings" for an MP3 with no
    /// lyrics tag at all.
    /// </summary>
    [Fact]
    public void Load_LyricsNoctisKeeps_ThatTheFilesTagsDoNotCarry_AreNotEmbeddedTags()
    {
        var mp3 = EmbeddedLyricsTagTests.Copy(_dir, "mp3", name: "untagged");
        Assert.True(Noctis.Services.EmbeddedLyrics.ReadFile(mp3).IsEmpty);
        var track = new Track { Title = "Song", FilePath = mp3, SourceType = SourceType.Local };
        // As the lyrics page leaves them after an online pick.
        track.Lyrics = Noctis.Helpers.LyricsTextHelper.StripTimestamps(Elrc);
        track.SyncedLyrics = Elrc;

        var existing = ExistingLyricsLoader.Load(track)!;

        Assert.Equal(LyricsFormat.Elrc, existing.Format);
        Assert.Equal("Noctis's lyrics cache", existing.Origin);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public void Load_LyricsTheFilesTagsCarry_AreEmbeddedTags(string ext)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, ext, f => f.Tag.Lyrics = Elrc, name: "tagged");
        var track = new Noctis.Services.MetadataService().ReadTrackMetadata(path)!;

        var existing = ExistingLyricsLoader.Load(track)!;

        Assert.Equal(LyricsFormat.Elrc, existing.Format);
        Assert.Equal("embedded tags", existing.Origin);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
