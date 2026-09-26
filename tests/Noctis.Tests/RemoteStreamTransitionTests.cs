using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A media-server track is an http(s) URL, so it never passes File.Exists. The
/// early gapless / AutoMix advance used to bail on exactly that check, leaving every
/// remote boundary on the EndReached grace plus a cold network open. When the player
/// stages remote streams (the splice engine) the advance must hand off early like a
/// local file; when it does not, the advance must still wait for TrackEnded.
/// </summary>
public class RemoteStreamTransitionTests
{
    private static Track Remote(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"https://media.example/Audio/{name}/stream?static=true&api_key=SECRET",
        Duration = TimeSpan.FromMinutes(3)
    };

    private static (FakeAudioPlayer Player, PlayerViewModel Vm, Track A, Track B) Start(
        bool playerStagesRemote, AutoMixTransitionMode mode)
    {
        var player = new FakeAudioPlayer { PreparesRemoteStreams = playerStagesRemote };
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var a = Remote("a");
        var b = Remote("b");
        vm.AutoMixTransitionMode = mode;
        vm.ReplaceQueueAndPlay(new[] { a, b }, 0);
        Dispatcher.UIThread.RunJobs();
        ClearCommitGuard(vm);
        return (player, vm, a, b);
    }

    [AvaloniaFact]
    public void Gapless_RemoteNext_HandsOffEarly_WhenPlayerStagesStreams()
    {
        var (player, vm, _, b) = Start(playerStagesRemote: true, AutoMixTransitionMode.Off);
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(6), duration));
        Assert.Contains(b.FilePath, player.PreparedPaths);

        var advanced = vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(0.3), duration);
        Dispatcher.UIThread.RunJobs();

        Assert.True(advanced);
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
        Assert.Equal(b.FilePath, player.PlayedPaths[^1]);
    }

    [AvaloniaFact]
    public void Gapless_RemoteNext_WaitsForTrackEnded_WhenPlayerCannotStageStreams()
    {
        var (_, vm, a, _) = Start(playerStagesRemote: false, AutoMixTransitionMode.Off);
        var duration = TimeSpan.FromMinutes(3);

        vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(6), duration);
        var advanced = vm.TryAdvanceForGapless(duration - TimeSpan.FromSeconds(0.3), duration);
        Dispatcher.UIThread.RunJobs();

        Assert.False(advanced);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void AutoMix_RemoteNext_CommitsTransition_WhenPlayerStagesStreams()
    {
        var (player, vm, _, b) = Start(playerStagesRemote: true, AutoMixTransitionMode.AutoMix);
        // 3-minute tracks without BPM plan a 5s fallback crossfade (window opens at 175s).
        var position = TimeSpan.FromSeconds(176.5);
        var duration = TimeSpan.FromMinutes(3);

        Assert.False(vm.TryAdvanceForAutoMix(position, duration));
        Assert.Contains(b.FilePath, player.PreparedPaths);

        var committed = vm.TryAdvanceForAutoMix(position, duration);
        Dispatcher.UIThread.RunJobs();

        Assert.True(committed);
        Assert.Equal(b.Id, vm.CurrentTrack?.Id);
    }

    [AvaloniaFact]
    public void AutoMix_RemoteNext_DoesNotCommit_WhenPlayerCannotStageStreams()
    {
        var (_, vm, a, _) = Start(playerStagesRemote: false, AutoMixTransitionMode.AutoMix);
        var position = TimeSpan.FromSeconds(176.5);
        var duration = TimeSpan.FromMinutes(3);

        vm.TryAdvanceForAutoMix(position, duration);
        var committed = vm.TryAdvanceForAutoMix(position, duration);
        Dispatcher.UIThread.RunJobs();

        Assert.False(committed);
        Assert.Equal(a.Id, vm.CurrentTrack?.Id);
    }

    // PlayTrack arms a 2s wall-clock commit guard against stale positions from the
    // outgoing song; clear it so the test doesn't have to sleep through it.
    private static void ClearCommitGuard(PlayerViewModel vm) =>
        typeof(PlayerViewModel)
            .GetField("_autoMixCommitGuardUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, DateTime.MinValue);
}
