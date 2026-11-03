using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #149: "my lyrics are not identified even though they are FLAC 24-bit files and already
/// have lyrics inside metadata", plus Malayalam. FLAC fixtures tagged here through TagLib the way
/// other taggers tag them, read by the scanner and by the lyrics page's probe.
/// </summary>
public class EmbeddedLyricsVariantsTests : IDisposable
{
    private const string Plain = "first line\nsecond line";
    private const string Lrc = "[00:01.00]first line\n[00:02.00]second line";
    // Malayalam (GitHub #149): consonant + virama + consonant conjuncts and vowel signs.
    private const string Malayalam = "എന്റെ മലയാളം പാട്ട്\nകണ്ണീർ പൂവിന്റെ";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics149-" + Guid.NewGuid().ToString("N"));

    public EmbeddedLyricsVariantsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static TagLib.Ogg.XiphComment Xiph(TagLib.File f) => (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);

    private static Track Scan(string path) => new MetadataService().ReadTrackMetadata(path)!;

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static LyricsViewModel.LocalLyricsProbe Probe(string path) =>
        LyricsViewModel.ProbeLocalLyricSources(new Track { FilePath = path, Title = "t" }, false, "en");

    [Theory]
    [InlineData("LYRICS")]
    [InlineData("lyrics")]
    [InlineData("Lyrics")]
    [InlineData("UNSYNCEDLYRICS")]
    [InlineData("unsyncedlyrics")]
    [InlineData("UNSYNCED LYRICS")]
    [InlineData("UNSYNCED_LYRICS")]
    [InlineData("UNSYNCHRONISED LYRICS")]
    [InlineData("UNSYNCHRONIZED LYRICS")]
    [InlineData("LYRICS:ENG")]
    [InlineData("LYRICS:DESCRIPTION")]
    public void Flac_PlainLyricsField_IsRead(string field)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField(field, Plain));

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
        var probe = Probe(path);
        Assert.Equal(new[] { "first line", "second line" }, probe.Lines!.Select(l => l.Text));
    }

    [Theory]
    [InlineData("SYNCEDLYRICS")]
    [InlineData("SYNCED LYRICS")]
    [InlineData("SYNCED_LYRICS")]
    [InlineData("SYNCHRONISED LYRICS")]
    [InlineData("LYRICS")]
    public void Flac_TimedLyricsField_IsReadSynced(string field)
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField(field, Lrc));

        var probe = Probe(path);
        Assert.NotNull(probe.Lines);
        Assert.Contains(probe.Lines!, l => l.IsSynced && l.Text == "first line");
    }

    [Fact]
    public void Flac_Id3v2UsltInsideFlac_IsRead()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f =>
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame("", "eng") { Text = Plain });
        });

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
    }

    [Fact]
    public void Flac_LyricsSplitOverRepeatedFields_KeepsEveryLine()
    {
        // Some taggers write one LYRICS comment per line rather than one with line breaks.
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField("LYRICS", "first line", "second line"));

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
    }

    [Fact]
    public void Flac_EmptyLyricsField_FallsBackToTheAlias()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => { Xiph(f).SetField("LYRICS", " "); Xiph(f).SetField("UNSYNCEDLYRICS", Plain); });

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
    }

    [Fact]
    public void WriteLyrics_ReplacesTheNewlyReadAliasesToo()
    {
        // Read now, so a lyrics write must remove them, or the old words would come back.
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f =>
        {
            Xiph(f).SetField("UNSYNCED_LYRICS", "old words");
            Xiph(f).SetField("LYRICS:ENG", "old words");
            Xiph(f).SetField("SYNCHRONISED LYRICS", "[00:09.00]old");
        });

        Assert.True(new MetadataService().WriteLyrics(path, "new words", null));

        using (var f = TagLib.File.Create(path))
        {
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false);
            Assert.Equal(new[] { "LYRICS" }, xiph.Where(n => n.Contains("LYRICS")).ToArray());
        }
        Assert.Equal("new words", Lf(Scan(path).Lyrics));
        Assert.Equal(string.Empty, Scan(path).SyncedLyrics);
    }

    [Fact]
    public void Flac_MalayalamLyrics_RoundTripIntact()
    {
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField("LYRICS", Malayalam));

        Assert.Equal(Malayalam, Lf(Scan(path).Lyrics));
        var probe = Probe(path);
        Assert.Equal(Malayalam.Split('\n'), probe.Lines!.Select(l => l.Text));
    }

    [Fact]
    public void Flac_MalayalamLrc_KeepsTheText()
    {
        var lrc = "[00:01.00]എന്റെ മലയാളം പാട്ട്\n[00:02.00]കണ്ണീർ പൂവിന്റെ";
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField("LYRICS", lrc));

        var probe = Probe(path);
        Assert.Equal(Malayalam.Split('\n'), probe.Lines!.Where(l => l.IsSynced && l.Text.Length > 0 && l.Text != "...").Select(l => l.Text));
    }

    [Fact]
    public void UnrelatedTagSave_OfARowHoldingTheFirstLyricsComment_KeepsEveryComment()
    {
        // A song scanned before every LYRICS comment was read holds the first one alone (what
        // TagLib's Tag.Lyrics returns). A title fix must not save that one line over the rest.
        var path = EmbeddedLyricsTagTests.Copy(_dir, "flac", f => Xiph(f).SetField("LYRICS", "first line", "second line"));
        var row = Scan(path);
        row.Lyrics = "first line";
        row.Title = "Retitled";

        Assert.True(new MetadataService().WriteTrackMetadata(row));

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
        Assert.Equal("Retitled", Scan(path).Title);
    }
}
