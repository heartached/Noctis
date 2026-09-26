using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit A31: the error circuit breaker (five failures in a row, typically the music
/// drive asleep or unplugged) and an error on the last queued track stopped through
/// StopAndClear, which emptied the current track, Up Next and History — and the next
/// queue snapshot saved that, so the next launch restored nothing.
/// </summary>
public class PlaybackErrorStopTests : IDisposable
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

    private static void Fail(FakeAudioPlayer player)
    {
        player.RaisePlaybackError("File not found");
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ErrorCascade_StopsOnTheFirstFailedTrack_WithTheQueueAndHistoryKept()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var earlier = Trk("earlier");
        var t = Enumerable.Range(0, 7).Select(i => Trk("t" + i)).ToList();
        library.TrackList.Add(earlier);
        library.TrackList.AddRange(t);
        vm.ReplaceQueueAndPlay(new List<Track> { earlier }, 0);
        vm.ReplaceQueueAndPlay(t, 0); // "earlier" goes to History

        for (int i = 0; i < 5; i++) Fail(player);

        // t0 + four skips, then stop
        Assert.Equal(t.Take(5).Select(x => x.FilePath), player.PlayedPaths.Skip(1));
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(t.Skip(1), vm.UpNext);
        Assert.Equal(new[] { earlier }, vm.History);

        // Once the drive is back, Play retries from where the run began.
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(t[0].FilePath, player.PlayedPaths[^1]);
        Assert.Equal(PlaybackState.Playing, vm.State);
    }

    [AvaloniaFact]
    public void ErrorOnTheLastTrack_StopsOnIt_WithHistoryKept()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Trk("a");
        var b = Trk("b");
        library.TrackList.AddRange(new[] { a, b });
        vm.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);
        player.RaisePositionChanged(TimeSpan.FromSeconds(1)); // "a" really plays
        Dispatcher.UIThread.RunJobs();
        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(b, vm.CurrentTrack);

        Fail(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Same(b, vm.CurrentTrack);
        Assert.Empty(vm.UpNext);
        Assert.Equal(new[] { a }, vm.History);
    }

    [AvaloniaFact]
    public async Task ErrorCascade_OnARestoredSession_KeepsTheSavedQueueAndPosition()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var t = Enumerable.Range(0, 6).Select(i => Trk("t" + i)).ToList();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(t);
        await persistence.SaveQueueStateAsync(new QueueState
        {
            CurrentTrackId = t[0].Id,
            PositionSeconds = 83,
            UpNextIds = t.Skip(1).Select(x => x.Id).ToList(),
        });

        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, library, persistence, new FakeAnimatedCoverService());
        await vm.RestoreQueueStateAsync();
        vm.PlayPauseCommand.Execute(null);                  // the drive is offline
        for (int i = 0; i < 5; i++) Fail(player);

        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(83, vm.Position.TotalSeconds, 3);
        await Task.Delay(300); // let the background snapshots land first
        await vm.SaveQueueStateAsync();

        var saved = await persistence.LoadQueueStateAsync();
        Assert.NotNull(saved);
        Assert.Equal(t[0].Id, saved!.CurrentTrackId);
        Assert.Equal(t.Skip(1).Select(x => x.Id), saved.UpNextIds);
        Assert.Equal(83, saved.PositionSeconds, 3);

        // Play resumes the restored position, not the start of the track.
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(t[0].FilePath, player.PlayedPaths[^1]);
        Assert.Equal(83_000, player.PendingSeekMs);
    }
}
