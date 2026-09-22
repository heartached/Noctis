using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone lyrics source priority: sidecar TTML → ELRC → LRC → embedded → SYLT → plain.</summary>
public class LyricsLoaderTests
{
    private const string Ttml = """<tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="0.5" end="1.5"><span begin="0.5" end="1.5">ttml</span></p></div></body></tt>""";

    private static Track NewTrack(string lyrics = "", string synced = "") =>
        new() { Id = Guid.NewGuid(), FilePath = "content://x/tree/t/document/primary%3AMusic%2Fsong.mp3", Lyrics = lyrics, SyncedLyrics = synced };

    [Fact]
    public void TtmlSidecar_BeatsEveryOtherSource()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = Ttml;
        files.Sidecars[".lrc"] = "[00:01.00]lrc";

        var result = LyricsLoader.Load(NewTrack(synced: "[00:01.00]embedded"), files, joinSplitWords: false);

        Assert.Equal(LyricsSource.SidecarTtml, result.Source);
        Assert.True(result.IsSynced);
        Assert.Equal("ttml", result.Lines[0].Text);
        Assert.Equal(1, files.SidecarReads);            // one lookup when the first format hits
    }

    [Fact]
    public void MalformedTtml_FallsThroughToElrc_ThenLrc()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = "<tt><p>unclosed";
        files.Sidecars[".elrc"] = "[00:01.00]<00:01.00>e <00:01.50>lrc";
        files.Sidecars[".lrc"] = "[00:01.00]lrc";

        var result = LyricsLoader.Load(NewTrack(), files, joinSplitWords: false);

        Assert.Equal(LyricsSource.SidecarElrc, result.Source);
        Assert.True(result.Lines[0].HasWords);
    }

    [Fact]
    public void LrcSidecar_UsedWhenNoTtmlOrElrc()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = "[00:01.00]one\n[00:02.00]two";

        var result = LyricsLoader.Load(NewTrack(), files, joinSplitWords: false);

        Assert.Equal(LyricsSource.SidecarLrc, result.Source);
        Assert.Equal(new[] { "one", "two" }, result.Lines.Select(l => l.Text));
    }

    [Fact]
    public void StoredSyncedLyrics_ThenLrcInPlainField_AreSynced()
    {
        var files = new FakeTrackFiles();

        var stored = LyricsLoader.Load(NewTrack(synced: "[00:01.00]stored"), files, false);
        Assert.Equal(LyricsSource.EmbeddedSynced, stored.Source);
        Assert.True(stored.IsSynced);

        var legacy = LyricsLoader.Load(NewTrack(lyrics: "[00:01.00]legacy"), files, false);
        Assert.Equal(LyricsSource.EmbeddedSynced, legacy.Source);
        Assert.Equal("legacy", legacy.Lines[0].Text);
    }

    [Fact]
    public void PlainLyrics_AreStaticAndActive()
    {
        var result = LyricsLoader.Load(NewTrack(lyrics: "first\nsecond"), new FakeTrackFiles(), false);

        Assert.Equal(LyricsSource.EmbeddedPlain, result.Source);
        Assert.False(result.IsSynced);
        Assert.All(result.Lines, l => Assert.True(l.IsActive));
        Assert.Equal(new[] { "first", "second" }, result.Lines.Select(l => l.Text));
    }

    [Fact]
    public void LrcWithoutStamps_IsUnsyncedText_NotNone()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = "just words\nno stamps";

        var result = LyricsLoader.Load(NewTrack(), files, false);

        Assert.Equal(LyricsSource.SidecarLrc, result.Source);
        Assert.False(result.IsSynced);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void NothingAnywhere_IsNone()
    {
        var result = LyricsLoader.Load(NewTrack(), new FakeTrackFiles(), false);
        Assert.Same(LoadedLyrics.None, result);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void LateFirstLine_GetsTheIntroPlaceholder()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = "[00:05.00]late";

        var result = LyricsLoader.Load(NewTrack(), files, false);

        Assert.True(result.Lines[0].IsIntroPlaceholder);
        Assert.Equal("late", result.Lines[1].Text);
    }

    [Fact]
    public void SidecarNames_MatchStemCaseInsensitively_InRequestedOrder()
    {
        var siblings = new[] { "01. Intro.v2.flac", "01. Intro.v2.LRC", "01. Intro.v2.TTML", "01. Intro.ttml" };

        var hit = SidecarNames.Match("01. Intro.v2.flac", siblings, new[] { ".ttml", ".lrc" });

        Assert.Equal(("01. Intro.v2.TTML", ".ttml"), hit);
    }

    [Fact]
    public void SidecarNames_NoSiblingWithTheStem_IsNull()
    {
        Assert.Null(SidecarNames.Match("song.mp3", new[] { "song2.lrc", "other.ttml" }, new[] { ".ttml", ".lrc" }));
        Assert.Null(SidecarNames.Match("", new[] { ".lrc" }, new[] { ".lrc" }));
    }
}
