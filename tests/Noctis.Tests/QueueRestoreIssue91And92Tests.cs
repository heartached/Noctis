using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #91: a queue played from outside the library comes back after a restart like a
/// library queue does. GitHub #92: with nothing loaded the queue can still be opened and
/// filled, and Play then plays what the user put there.
/// </summary>
public class QueueRestoreIssue91And92Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => new()
        {
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "Dropped",
            FilePath = filePath,
            AlbumId = Guid.NewGuid(),
            Duration = TimeSpan.FromMinutes(3),
        };
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return ReadTrackMetadata(filePath); }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private Track External(string name, bool create = true)
    {
        var dir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".mp3");
        if (create) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "Dropped",
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3),
            IsExternal = true,
        };
    }

    private static Track LibraryTrack(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        FilePath = Path.Combine("C:", "Music", title + ".mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    [AvaloniaFact]
    public async Task Restore_ExternalQueue_ComesBackPausedAtItsPosition_WithHistoryAndModes()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var a = External("a");
        var b = External("b");
        var c = External("c");

        var before = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(new List<Track> { a, b, c }, 0);
        before.NextCommand.Execute(null);                // a → History, b playing
        await Task.Delay(300); // let the track-change background snapshot land first
        before.Position = TimeSpan.FromSeconds(83);
        before.RepeatMode = RepeatMode.All;
        await before.SaveQueueStateAsync();

        var after = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService(), new StubMetadata());
        await after.RestoreQueueStateAsync();

        Assert.Equal(b.Id, after.CurrentTrack?.Id);
        Assert.Equal(PlaybackState.Stopped, after.State);   // loaded, waiting for Play
        Assert.Equal(83, after.Position.TotalSeconds, 3);
        Assert.Equal(new[] { c.Id }, after.UpNext.Select(t => t.Id));
        Assert.Equal(new[] { a.Id }, after.History.Select(t => t.Id));
        Assert.Equal(RepeatMode.All, after.RepeatMode);
        Assert.True(after.HasContent);

        // Play resumes the restored external file itself.
        var audio = new FakeAudioPlayer();
        var resumed = new PlayerViewModel(audio, new FakeLibraryService(), persistence, new FakeAnimatedCoverService(), new StubMetadata());
        await resumed.RestoreQueueStateAsync();
        resumed.PlayPauseCommand.Execute(null);
        Assert.Equal(new[] { b.FilePath }, audio.PlayedPaths);
    }

    [AvaloniaFact]
    public async Task Restore_WhenTheSavedCurrentFileIsGone_LoadsTheNextQueuedTrackInstead()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var gone = External("gone");
        var next = External("next");
        var last = External("last");

        var before = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(new List<Track> { gone, next, last }, 0);
        await Task.Delay(300);
        before.Position = TimeSpan.FromSeconds(40);
        await before.SaveQueueStateAsync();
        File.Delete(gone.FilePath);   // moved / deleted between sessions

        var after = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService(), new StubMetadata());
        await after.RestoreQueueStateAsync();

        Assert.Equal(next.Id, after.CurrentTrack?.Id);
        Assert.Equal(TimeSpan.Zero, after.Position);     // the saved position was the gone file's
        Assert.Equal(new[] { last.Id }, after.UpNext.Select(t => t.Id));
        Assert.Equal(PlaybackState.Stopped, after.State);
    }

    [AvaloniaFact]
    public void PlayPause_WithAQueueButNothingLoaded_PlaysTheQueue_NotALibraryShuffle()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(LibraryTrack("library 1"));
        library.TrackList.Add(LibraryTrack("library 2"));
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());

        // #92: queue opened on an empty player, songs dragged / "Add to queue"d into it.
        player.ShowQueueCommand.Execute(null);
        Assert.True(player.IsQueuePopupOpen);
        var x = LibraryTrack("x");
        var y = LibraryTrack("y");
        player.AddRangeToQueue(new List<Track> { x, y });
        Assert.Null(player.CurrentTrack);

        player.PlayPauseCommand.Execute(null);

        Assert.Equal(x.Id, player.CurrentTrack?.Id);
        Assert.Equal(new[] { y.Id }, player.UpNext.Select(t => t.Id));
        Assert.Equal(new[] { x.FilePath }, audio.PlayedPaths);
        Assert.False(player.IsShuffleEnabled);
    }

    [AvaloniaFact]
    public void StopAndClear_DropsTheCoverPath_SoAQueueOnTheEmptyPlayerShowsNoStaleCover()
    {
        // 09-24 harness: the queue ran out (StopAndClear), songs were queued again (#92), and the
        // island came back with the previous song's cover over a blank title.
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var played = LibraryTrack("played");
        played.AlbumId = Guid.NewGuid();
        File.WriteAllBytes(persistence.GetArtworkPath(played.AlbumId), new byte[] { 1, 2, 3 });
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        player.ReplaceQueueAndPlay(new List<Track> { played }, 0);
        Assert.NotNull(player.CurrentArtPath);

        player.StopAndClear();
        player.AddRangeToQueue(new List<Track> { LibraryTrack("queued") });

        Assert.Null(player.CurrentTrack);
        Assert.True(player.HasContent);
        Assert.Null(player.CurrentArtPath);
    }
}
