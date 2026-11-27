using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta 6 (#mobile-beta): after a song change the lyrics took a noticeable time to show, with
/// online lookup off. Every play searched for sidecars (on the phone: a SAF folder listing) and
/// read the file's tags again. <see cref="LyricsLookupCache"/> remembers per file that no sidecar
/// is beside it and what its tags hold, while the file stays as the scan saw it.
/// </summary>
public class LyricsLookupCacheTests
{
    private static Track Song(string path = "content://x/1") => new()
    {
        Id = Guid.NewGuid(), Title = "Song", FilePath = path, FileSize = 1000, LastModified = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void NoSidecar_AndEmptyTags_AreAskedOnce_ThenSkipped()
    {
        var files = new FakeTrackFiles { Audio = new byte[] { 1, 2, 3 } };   // opens, holds no tags
        var lookups = new LyricsLookupCache();
        var song = Song();

        Assert.Equal(LyricsSource.None, LyricsLoader.Load(song, files, false, lookups).Source);
        Assert.Equal((1, 1), (files.SidecarReads, files.AudioOpens));

        Assert.Equal(LyricsSource.None, LyricsLoader.Load(song, files, false, lookups).Source);
        Assert.Equal((1, 1), (files.SidecarReads, files.AudioOpens));   // no I/O at all
    }

    [Fact]
    public void StoredLyrics_StillShow_WithoutTheSidecarSearch()
    {
        var files = new FakeTrackFiles();
        var lookups = new LyricsLookupCache();
        var song = Song();
        song.SyncedLyrics = "[00:01.00]stored line";

        LyricsLoader.Load(song, files, false, lookups);
        var again = LyricsLoader.Load(song, files, false, lookups);

        Assert.Equal(LyricsSource.EmbeddedSynced, again.Source);
        Assert.Equal("stored line", again.Lines.First(l => l.IsSynced).Text);
        Assert.Equal(1, files.SidecarReads);
    }

    [Fact]
    public void AFoundSidecar_IsReadAgainEachPlay_SoAnEditShows()
    {
        var files = new FakeTrackFiles { Sidecars = { [".lrc"] = "[00:01.00]first take" } };
        var lookups = new LyricsLookupCache();
        var song = Song();

        Assert.Equal("first take", LyricsLoader.Load(song, files, false, lookups).Lines.First(l => l.IsSynced).Text);
        files.Sidecars[".lrc"] = "[00:01.00]edited";
        Assert.Equal("edited", LyricsLoader.Load(song, files, false, lookups).Lines.First(l => l.IsSynced).Text);
        Assert.Equal(2, files.SidecarReads);
        Assert.Equal(0, lookups.Count);
    }

    [Fact]
    public void AChangedFile_OrAFinishedRescan_LooksAgain()
    {
        var files = new FakeTrackFiles { Audio = new byte[] { 1, 2, 3 } };
        var lookups = new LyricsLookupCache();
        var song = Song();
        LyricsLoader.Load(song, files, false, lookups);

        // The scan saw the file change (size and modified time): its entry no longer holds.
        files.Sidecars[".lrc"] = "[00:01.00]added with the new file";
        song.LastModified = song.LastModified.AddMinutes(5);
        Assert.Equal(LyricsSource.SidecarLrc, LyricsLoader.Load(song, files, false, lookups).Source);
        Assert.Equal(2, files.SidecarReads);

        // A sidecar beside an unchanged file: found once the cache is cleared (a finished rescan).
        var other = Song("content://x/2");
        files.Sidecars.Clear();
        LyricsLoader.Load(other, files, false, lookups);
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        Assert.Equal(LyricsSource.None, LyricsLoader.Load(other, files, false, lookups).Source);
        lookups.Clear();
        Assert.Equal(LyricsSource.SidecarTtml, LyricsLoader.Load(other, files, false, lookups).Source);
    }

    [Fact]
    public void AnUnreadableFile_OrAnUnparsableSidecar_IsNotRememberedAsNone()
    {
        var lookups = new LyricsLookupCache();
        var unreadable = new FakeTrackFiles();   // OpenAudio gives nothing: the tags are unknown, not empty
        LyricsLoader.Load(Song(), unreadable, false, lookups);
        LyricsLoader.Load(Song(), unreadable, false, lookups);
        Assert.Equal(2, unreadable.AudioOpens);

        var broken = new FakeTrackFiles { Sidecars = { [".ttml"] = "<tt><body><p>not closed" }, Audio = new byte[] { 1 } };
        LyricsLoader.Load(Song("content://x/3"), broken, false, lookups);
        var before = broken.SidecarReads;
        LyricsLoader.Load(Song("content://x/3"), broken, false, lookups);
        Assert.True(broken.SidecarReads > before, "a sidecar that would not parse is looked for again");
    }
}
