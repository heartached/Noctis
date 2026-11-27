using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #135: lyrics inside the audio file. Real files per container (tiny silent fixtures made
/// with ffmpeg), tagged here through TagLib the way other taggers tag them, then read by the
/// scanner and the lyrics page's probe. Before #135 the scanner read only TagLib's Tag.Lyrics:
/// Vorbis UNSYNCEDLYRICS (Mp3tag's default) / "UNSYNCED LYRICS" (foobar2000) / SYNCEDLYRICS and an
/// MP3's SYLT frame or TXXX:LYRICS all showed "No lyrics" on the desktop.
/// </summary>
public class EmbeddedLyricsTagTests : IDisposable
{
    private const string Lrc = "[00:01.00]first line\n[00:02.00]second line";
    private const string Elrc = "[00:01.00]<00:01.00>first <00:01.40>line\n[00:02.00]<00:02.00>second <00:02.50>line";
    private const string Plain = "first line\nsecond line";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics135-" + Guid.NewGuid().ToString("N"));

    public EmbeddedLyricsTagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    /// <summary>A temp copy of the silent fixture for <paramref name="ext"/>, tagged by <paramref name="tag"/>.</summary>
    internal static string Copy(string dir, string ext, Action<TagLib.File>? tag = null, string? name = null)
    {
        var path = Path.Combine(dir, (name ?? Guid.NewGuid().ToString("N")) + "." + ext);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + ext), path, true);
        if (tag != null)
        {
            using var f = TagLib.File.Create(path);
            tag(f);
            f.Save();
        }
        return path;
    }

    private static TagLib.Ogg.XiphComment Xiph(TagLib.File f) => (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
    private static TagLib.Id3v2.Tag Id3(TagLib.File f) => (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);

    private static void AddSylt(TagLib.File f, params (long Ms, string Text)[] entries)
    {
        Id3(f).AddFrame(new TagLib.Id3v2.SynchronisedLyricsFrame("", "eng", TagLib.Id3v2.SynchedTextType.Lyrics)
        {
            Format = TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds,
            Text = entries.Select(e => new TagLib.Id3v2.SynchedText(e.Ms, e.Text)).ToArray(),
        });
    }

    private static Track Scan(string path) => new MetadataService().ReadTrackMetadata(path)!;

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    // ── Reading: the scanner ──

    [Theory]
    [InlineData("flac", "UNSYNCEDLYRICS")]
    [InlineData("flac", "UNSYNCED LYRICS")]
    [InlineData("ogg", "UNSYNCEDLYRICS")]
    [InlineData("opus", "UNSYNCEDLYRICS")]
    [InlineData("opus", "UNSYNCED LYRICS")]
    public void Scan_ReadsTheUnsyncedLyricsAliasesOtherTaggersWrite(string ext, string field)
    {
        var path = Copy(_dir, ext, f => Xiph(f).SetField(field, Plain));

        Assert.Equal(Plain, Lf(Scan(path).Lyrics));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    public void Scan_ReadsVorbisSyncedLyrics_AsSyncedLyrics(string ext)
    {
        var path = Copy(_dir, ext, f => { Xiph(f).SetField("SYNCEDLYRICS", Lrc); Xiph(f).SetField("UNSYNCEDLYRICS", Plain); });

        var track = Scan(path);

        Assert.Equal(Lrc, Lf(track.SyncedLyrics));
        Assert.Equal(Plain, Lf(track.Lyrics));
    }

    [Fact]
    public void Scan_ReadsAnMp3Sylt_AsLrc_AndKeepsTheUsltAsPlain()
    {
        var path = Copy(_dir, "mp3", f => { f.Tag.Lyrics = Plain; AddSylt(f, (2000, "second line"), (1000, "first line")); });

        var track = Scan(path);

        Assert.Equal(Lrc, Lf(track.SyncedLyrics));
        Assert.Equal(Plain, Lf(track.Lyrics));
    }

    [Fact]
    public void Scan_SyltTimingWords_BecomesWordSyncedLines()
    {
        // The spec's word/syllable style: a line break starts each new line.
        var path = Copy(_dir, "mp3", f => AddSylt(f, (1000, "first"), (1400, " line"), (2000, "\nsecond"), (2500, " line")));

        var synced = Scan(path).SyncedLyrics;

        Assert.Equal(Elrc, Lf(synced));
        var lines = LrcParser.Parse(synced);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2, lines[0].Words!.Count);
    }

    [Fact]
    public void Scan_ReadsAnMp3TxxxLyrics()
    {
        var path = Copy(_dir, "mp3", f => TagLib.Id3v2.UserTextInformationFrame.Get(Id3(f), "LYRICS", true).Text = [Lrc]);

        Assert.Equal(Lrc, Lf(Scan(path).Lyrics));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("m4a")]
    public void Scan_MainLyricsField_StillReadAsBefore(string ext)
    {
        var path = Copy(_dir, ext, f => f.Tag.Lyrics = Lrc);

        var track = Scan(path);

        Assert.Equal(Lrc, Lf(track.Lyrics));
        Assert.Equal(string.Empty, track.SyncedLyrics);
    }

    // ── Reading: the lyrics page ──

    [Fact]
    public void Probe_RowScannedBeforeAliasesWereRead_ShowsTheFilesUnsyncedLyrics()
    {
        // A library is not rescanned until its files change: the row is empty, the file is not.
        var path = Copy(_dir, "flac", f => Xiph(f).SetField("UNSYNCEDLYRICS", Plain));
        var row = new Track { FilePath = path, Title = "t" };

        var probe = LyricsViewModel.ProbeLocalLyricSources(row, false, "en");

        Assert.Equal("Embedded:Tags", probe.Source);
        Assert.Equal(new[] { "first line", "second line" }, probe.Lines!.Select(l => l.Text));
        Assert.All(probe.Lines!, l => Assert.False(l.IsSynced));
    }

    [Fact]
    public void Probe_RowWithPlainUslt_PicksUpTheFilesSylt_AndKeepsThePlainForThePlainTab()
    {
        var path = Copy(_dir, "mp3", f => { f.Tag.Lyrics = "the plain words"; AddSylt(f, (1000, "first line"), (2000, "second line")); });
        var row = new Track { FilePath = path, Title = "t", Lyrics = "the plain words" };

        var probe = LyricsViewModel.ProbeLocalLyricSources(row, false, "en");

        Assert.Equal("Embedded:Tags", probe.Source);
        Assert.True(probe.Lines!.All(l => l.IsSynced));
        Assert.Equal("the plain words", probe.UnsyncedPlain);
        Assert.Equal(Lrc, Lf(probe.SyncedText!));
    }

    [Fact]
    public void Probe_SidecarStillBeatsTheTags()
    {
        var path = Copy(_dir, "flac", f => Xiph(f).SetField("SYNCEDLYRICS", Lrc), name: "sidecar");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:03.00]from the sidecar");
        var row = new Track { FilePath = path, Title = "t" };

        var probe = LyricsViewModel.ProbeLocalLyricSources(row, false, "en");

        Assert.Equal("Sidecar:Lrc", probe.Source);
    }

    [Fact]
    public void Probe_TheFilesTags_BeatTheOnlineCache()
    {
        var path = Copy(_dir, "opus", f => Xiph(f).SetField("SYNCEDLYRICS", Lrc));
        var row = new Track { FilePath = path, Title = "t" };
        var cache = Path.Combine(LyricsViewModel.LyricsCacheDir, row.Id + ".lrc");
        Directory.CreateDirectory(LyricsViewModel.LyricsCacheDir);
        File.WriteAllText(cache, "[00:05.00]an old online result");
        try
        {
            var probe = LyricsViewModel.ProbeLocalLyricSources(row, false, "en");

            Assert.Equal("Embedded:Tags", probe.Source);
            Assert.Equal("first line", probe.Lines!.First(l => l.IsSynced).Text);
        }
        finally { File.Delete(cache); }
    }

    [Fact]
    public void PhoneLoader_ReadsTheFilesSyncedLyricsAndAliases()
    {
        var synced = Copy(_dir, "flac", f => Xiph(f).SetField("SYNCEDLYRICS", Lrc), name: "phone-synced");
        var plain = Copy(_dir, "ogg", f => Xiph(f).SetField("UNSYNCEDLYRICS", Plain), name: "phone-plain");

        var a = LyricsLoader.Load(new Track { FilePath = synced }, new FakeTrackFiles { Audio = File.ReadAllBytes(synced) }, false);
        var b = LyricsLoader.Load(new Track { FilePath = plain }, new FakeTrackFiles { Audio = File.ReadAllBytes(plain) }, false);

        Assert.Equal(LyricsSource.EmbeddedSynced, a.Source);
        Assert.True(a.IsSynced);
        Assert.Equal(LyricsSource.EmbeddedPlain, b.Source);
        Assert.Equal(2, b.Lines.Count);
    }

    /// <summary>Beta 6 (lyrics slow after a song change): with the lookup cache, the file's tags
    /// are read on the first play only; later plays reuse their text, with no file open.</summary>
    [Fact]
    public void PhoneLoader_WithTheLookupCache_ReadsTheTagsOnce()
    {
        var path = Copy(_dir, "flac", f => Xiph(f).SetField("SYNCEDLYRICS", Lrc), name: "phone-cached");
        var files = new FakeTrackFiles { Audio = File.ReadAllBytes(path) };
        var track = new Track { FilePath = path };
        var lookups = new LyricsLookupCache();

        var first = LyricsLoader.Load(track, files, false, lookups);
        var second = LyricsLoader.Load(track, files, false, lookups);

        Assert.Equal(LyricsSource.EmbeddedSynced, first.Source);
        Assert.Equal(LyricsSource.EmbeddedSynced, second.Source);
        Assert.Equal(first.Lines.Select(l => l.Text), second.Lines.Select(l => l.Text));
        Assert.Equal(1, files.AudioOpens);
        Assert.Equal(1, files.SidecarReads);
    }

    // ── Writing ──

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("m4a")]
    public void WriteLyrics_SyncedKeepsWordTimings_AndReadsBackAsWordSynced(string ext)
    {
        var path = Copy(_dir, ext);

        Assert.True(new MetadataService().WriteLyrics(path, Plain, Elrc));

        var track = Scan(path);
        var probe = LyricsViewModel.ProbeLocalLyricSources(track, false, "en");
        Assert.Equal(Elrc, Lf(EmbeddedLyrics.ReadFile(path).TimedText));
        // The text field carries the word timings (an MP3's SYLT only has the lines).
        Assert.Contains(LrcParser.Parse(track.Lyrics), l => l.Words is { Count: > 1 });
        Assert.Equal("Embedded", probe.Source);
    }

    [Fact]
    public void WriteLyrics_Mp3_GetsOneUsltAndALineLevelSylt()
    {
        var path = Copy(_dir, "mp3", f =>
        {
            // Another tagger's frames: a described USLT in another language and an old SYLT.
            TagLib.Id3v2.UnsynchronisedLyricsFrame.Get(Id3(f), "Lyrics", "deu", true).Text = "alte Worte";
            AddSylt(f, (500, "old"));
        });

        Assert.True(new MetadataService().WriteLyrics(path, Plain, Elrc));

        using var f = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false);
        var uslt = Assert.Single(id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>());
        Assert.Equal(Elrc, Lf(uslt.Text));
        var sylt = Assert.Single(id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>());
        Assert.Equal(TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds, sylt.Format);
        Assert.Equal(new[] { (1000L, "first line"), (2000L, "second line") }, sylt.Text.Select(t => (t.Time, t.Text)));
    }

    [Fact]
    public void WriteLyrics_Flac_StaysVorbisOnly_AndReplacesTheAliases()
    {
        var path = Copy(_dir, "flac", f => { Xiph(f).SetField("UNSYNCEDLYRICS", "old words"); Xiph(f).SetField("SYNCEDLYRICS", "[00:09.00]old"); });

        Assert.True(new MetadataService().WriteLyrics(path, "new words", null));

        using var f = TagLib.File.Create(path);
        Assert.False(f.TagTypesOnDisk.HasFlag(TagLib.TagTypes.Id3v2));
        var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false);
        Assert.Equal("new words", xiph.GetFirstField("LYRICS"));
        Assert.Empty(xiph.GetField("UNSYNCEDLYRICS"));
        Assert.Empty(xiph.GetField("SYNCEDLYRICS"));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public void WriteLyrics_BothEmpty_ClearsEveryLyricsField(string ext)
    {
        var path = Copy(_dir, ext, f =>
        {
            f.Tag.Lyrics = Plain;
            if (ext == "mp3") AddSylt(f, (1000, "first line"));
            else { Xiph(f).SetField("UNSYNCEDLYRICS", Plain); Xiph(f).SetField("SYNCEDLYRICS", Lrc); }
        });

        Assert.True(new MetadataService().WriteLyrics(path, null, null));

        Assert.True(EmbeddedLyrics.ReadFile(path).IsEmpty);
    }

    [Fact]
    public void WriteLyrics_OnlyIfRefuses_LeavesTheFileUntouched()
    {
        var path = Copy(_dir, "flac", f => f.Tag.Lyrics = "the user's own");
        var before = File.ReadAllBytes(path);

        var wrote = new MetadataService().WriteLyrics(path, "online", null, current => current.IsEmpty);

        Assert.False(wrote);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteLyrics_ReadOnlyFile_IsLeftAlone()
    {
        var path = Copy(_dir, "mp3");
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.False(new MetadataService().WriteLyrics(path, Plain, Lrc));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, ".noctis-*"));
    }

    [Fact]
    public void WriteLyrics_SameLyricsAgain_DoesNotRewriteTheFile()
    {
        var path = Copy(_dir, "opus");
        Assert.True(new MetadataService().WriteLyrics(path, Plain, Lrc));
        var stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.True(new MetadataService().WriteLyrics(path, Plain, Lrc));

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    // ── Unrelated saves (bug found while building #135) ──

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public void UnrelatedTagSave_KeepsTimedLyricsInTheTag(string ext)
    {
        // Another tagger put LRC in the lyrics tag. Edit Info shows it on the Synced tab and keeps
        // the stamped-off words as the row's plain lyrics, so a title fix saved the plain words
        // over the timed tag: the song lost its synced lyrics for every player.
        var path = Copy(_dir, ext, f => f.Tag.Lyrics = Lrc);
        var row = Scan(path);
        row.Lyrics = LyricsTextHelper.StripTimestamps(row.Lyrics);
        row.SyncedLyrics = Lrc;
        row.Title = "A title fix";

        Assert.True(new MetadataService().WriteTrackMetadata(row));

        Assert.Equal(Lrc, Lf(EmbeddedLyrics.ReadFile(path).Text));
        Assert.Equal("A title fix", Scan(path).Title);
    }

    [Fact]
    public void UnrelatedTagSave_KeepsSyncedLyricsSavedInsideTheFile_OverADifferentPlainText()
    {
        var path = Copy(_dir, "flac");
        Assert.True(new MetadataService().WriteLyrics(path, "plain kept apart", Elrc));
        var row = Scan(path);
        row.Lyrics = "plain kept apart";
        row.SyncedLyrics = Elrc;

        Assert.True(new MetadataService().WriteTrackMetadata(row));

        Assert.Equal(Elrc, Lf(EmbeddedLyrics.ReadFile(path).Text));
    }

    [Fact]
    public void EditedPlainLyrics_StillReachTheFile_AndReplaceTheAlias()
    {
        var path = Copy(_dir, "ogg", f => Xiph(f).SetField("UNSYNCEDLYRICS", "old words"));
        var row = Scan(path);
        row.Lyrics = "new words";

        Assert.True(new MetadataService().WriteTrackMetadata(row));

        Assert.Equal("new words", EmbeddedLyrics.ReadFile(path).Text);
        Assert.Equal("new words", Scan(path).Lyrics);
    }

    [Fact]
    public void UnrelatedTagSave_OfARowScannedBeforeAliasesWereRead_KeepsTheAlias()
    {
        var path = Copy(_dir, "flac", f => Xiph(f).SetField("UNSYNCEDLYRICS", Plain));
        var row = Scan(path);
        row.Lyrics = string.Empty; // what a pre-#135 scan stored
        row.Title = "Retitled";

        Assert.True(new MetadataService().WriteTrackMetadata(row));

        Assert.Equal(Plain, Lf(EmbeddedLyrics.ReadFile(path).Text));
    }
}
