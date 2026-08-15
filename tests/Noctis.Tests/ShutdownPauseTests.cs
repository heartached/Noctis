using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// PauseForShutdown: ShutdownAsync's saves run for seconds after the window is
/// gone, so playback must be silenced first — but with a pause-only call, never
/// the PlayPause toggle (which would resume an already-paused player) and never
/// Stop (which would clear the track/position the queue snapshot needs).
/// </summary>
public class ShutdownPauseTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    [AvaloniaFact]
    public void WhilePlaying_PausesEngineAndKeepsTrack()
    {
        var (vm, player) = CreateVm();
        var track = Trk("playing");
        vm.ReplaceQueueAndPlay(new[] { track }, 0);
        Assert.Equal(PlaybackState.Playing, vm.State);

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(PlaybackState.Paused, vm.State);
        Assert.Equal(track.Id, vm.CurrentTrack?.Id); // snapshot still has the track
    }

    [AvaloniaFact]
    public void AlreadyPaused_DoesNotResume()
    {
        var (vm, player) = CreateVm();
        vm.ReplaceQueueAndPlay(new[] { Trk("paused") }, 0);
        vm.PlayPauseCommand.Execute(null); // user paused before quitting

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(PlaybackState.Paused, vm.State);
    }

    [AvaloniaFact]
    public void NothingLoaded_IsANoOp()
    {
        var (vm, player) = CreateVm();

        vm.PauseForShutdown();

        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, vm.State);
    }
}
