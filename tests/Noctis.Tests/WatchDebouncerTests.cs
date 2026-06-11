using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class WatchDebouncerTests
{
    [Fact]
    public void Drain_EmptyByDefault()
    {
        var d = new WatchDebouncer();
        Assert.False(d.HasPending);
        Assert.True(d.Drain().IsEmpty);
    }

    [Fact]
    public void Record_SeparatesImportsAndRemovals()
    {
        var d = new WatchDebouncer();
        d.Record(@"C:\m\a.mp3", FileChangeKind.CreatedOrChanged);
        d.Record(@"C:\m\b.flac", FileChangeKind.Deleted);

        var batch = d.Drain();
        Assert.Equal(new[] { @"C:\m\a.mp3" }, batch.ToImport);
        Assert.Equal(new[] { @"C:\m\b.flac" }, batch.ToRemove);
    }

    [Fact]
    public void Record_LatestKindWinsPerPath()
    {
        var d = new WatchDebouncer();
        // create then delete => net delete
        d.Record(@"C:\m\x.mp3", FileChangeKind.CreatedOrChanged);
        d.Record(@"C:\m\x.mp3", FileChangeKind.Deleted);
        // delete then create => net import
        d.Record(@"C:\m\y.mp3", FileChangeKind.Deleted);
        d.Record(@"C:\m\y.mp3", FileChangeKind.CreatedOrChanged);

        var batch = d.Drain();
        Assert.Equal(new[] { @"C:\m\y.mp3" }, batch.ToImport);
        Assert.Equal(new[] { @"C:\m\x.mp3" }, batch.ToRemove);
    }

    [Fact]
    public void Record_CoalescesDuplicateChanges()
    {
        var d = new WatchDebouncer();
        d.Record(@"C:\m\a.mp3", FileChangeKind.CreatedOrChanged);
        d.Record(@"C:\m\a.mp3", FileChangeKind.CreatedOrChanged);
        d.Record(@"C:\m\a.mp3", FileChangeKind.CreatedOrChanged);

        var batch = d.Drain();
        Assert.Single(batch.ToImport);
    }

    [Fact]
    public void Record_IsCaseInsensitiveOnPath()
    {
        var d = new WatchDebouncer();
        d.Record(@"C:\m\Song.mp3", FileChangeKind.CreatedOrChanged);
        d.Record(@"c:\m\song.MP3", FileChangeKind.Deleted);

        var batch = d.Drain();
        Assert.Empty(batch.ToImport);
        Assert.Single(batch.ToRemove);
    }

    [Fact]
    public void RecordRename_RemovesOldImportsNew()
    {
        var d = new WatchDebouncer();
        d.RecordRename(@"C:\m\old.mp3", @"C:\m\new.mp3");

        var batch = d.Drain();
        Assert.Equal(new[] { @"C:\m\new.mp3" }, batch.ToImport);
        Assert.Equal(new[] { @"C:\m\old.mp3" }, batch.ToRemove);
    }

    [Fact]
    public void RecordRename_AllowsOneSidedNonAudio()
    {
        var d = new WatchDebouncer();
        // download finishing: ".part" (non-audio, passed as null) -> "track.mp3"
        d.RecordRename(null, @"C:\m\track.mp3");
        var batch = d.Drain();
        Assert.Equal(new[] { @"C:\m\track.mp3" }, batch.ToImport);
        Assert.Empty(batch.ToRemove);
    }

    [Fact]
    public void IgnoresNullAndWhitespacePaths()
    {
        var d = new WatchDebouncer();
        d.Record("", FileChangeKind.CreatedOrChanged);
        d.Record("   ", FileChangeKind.Deleted);
        Assert.False(d.HasPending);
    }

    [Fact]
    public void Drain_ClearsPendingState()
    {
        var d = new WatchDebouncer();
        d.Record(@"C:\m\a.mp3", FileChangeKind.CreatedOrChanged);
        d.Drain();
        Assert.False(d.HasPending);
        Assert.True(d.Drain().IsEmpty);
    }
}
