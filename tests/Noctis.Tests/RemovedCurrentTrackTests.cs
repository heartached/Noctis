using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A28: the current track leaving the library (a scan dropping a moved file, an album
/// removed from its page) advanced the queue as if the track had ended naturally. A
/// paused or restored (stopped) player then started the next song by itself, and a
/// playing one replayed the removed file under Repeat One.
/// </summary>
public class RemovedCurrentTrackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
    };

    private static void Remove(FakeLibraryService library, Track track)
    {
        library.TrackList.Remove(track);
        library.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RemovingThePausedTrack_LoadsTheNextOne_WithoutPlayingIt()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        var b = Trk("b");
        var c = Trk("c");
        library.TrackList.AddRange(new[] { a, b, c });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b, c }, 0);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(PlaybackState.Paused, vm.State);

        Remove(library, a);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(new[] { a.FilePath }, player.PlayedPaths);
        Assert.Equal(new[] { "c" }, vm.UpNext.Select(t => t.Title));

        // Play then starts the staged track from its beginning.
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(b.FilePath, player.PlayedPaths[^1]);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public async Task RemovingTheRestoredTrack_OnAStartupScan_DoesNotStartPlayback()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var a = Trk("a");
        var b = Trk("b");
        var library = new FakeLibraryService();
        library.TrackList.AddRange(new[] { a, b });

        var before = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
        await Task.Delay(300); // let the track-change background snapshot land first
        await before.SaveQueueStateAsync();

        var player = new FakeAudioPlayer();
        var after = new PlayerViewModel(player, library, persistence, new FakeAnimatedCoverService());
        await after.RestoreQueueStateAsync();
        Assert.Same(a, after.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, after.State);

        // The scan's authoritative publish drops "a" (its file moved while the app was closed).
        Remove(library, a);

        Assert.Same(b, after.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, after.State);
        Assert.Empty(player.PlayedPaths);
    }

    [AvaloniaFact]
    public void RemovingThePlayingTrack_UnderRepeatOne_MovesOnToTheNextTrack()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, b });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
        vm.RepeatMode = RepeatMode.One;

        Remove(library, a);

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(b.FilePath, player.PlayedPaths[^1]);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public void RemovingThePlayingTrack_WithStopAfterArmed_PlaysTheNextAndStopsAfterIt()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        var b = Trk("b");
        var c = Trk("c");
        library.TrackList.AddRange(new[] { a, b, c });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b, c }, 0);
        vm.StopAfterCurrentTrack = true;

        Remove(library, a);

        // The UI no longer claims Stopped over a removed track the engine keeps playing.
        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(vm.StopAfterCurrentTrack);

        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, vm.CurrentTrack);
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.DoesNotContain(c.FilePath, player.PlayedPaths);
    }
}
