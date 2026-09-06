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
        File.WriteAllText(_audio, "x");
    }

    private Track NewTrack() => new() { Title = "Song", FilePath = _audio, SourceType = SourceType.Local };
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

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
    public void Load_PrefersElrcSidecar_ThenLrc_ThenEmbedded()
    {
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
