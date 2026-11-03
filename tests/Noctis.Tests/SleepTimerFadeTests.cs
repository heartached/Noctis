using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The sleep timer's fade-out: a playback fade gain (IAudioPlayer.SetPlaybackFadeGain) over
/// the last N seconds, never the volume setting; held at silence until the pause lands;
/// back at full on the next play; cancelled mid-fade, straight back; and "stop after
/// current track" fading over the end of the track.
/// </summary>
public class SleepTimerFadeTests : IDisposable
{
    private readonly string _dir;
    private DateTime _now = new(2026, 10, 29, 22, 0, 0, DateTimeKind.Utc);

    public SleepTimerFadeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"noctis-sleepfade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Track Trk(string name)
    {
        var path = Path.Combine(_dir, $"{name}.mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new()
        {
            Id = Guid.NewGuid(),
            Title = name,
            Artist = "A",
            FilePath = path,
            Duration = TimeSpan.FromMinutes(3)
        };
    }

    private (PlayerViewModel vm, FakeAudioPlayer player) CreatePlaying(int fadeSeconds = 30)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(
            player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SleepClock = () => _now;
        vm.SleepTimerFadeSeconds = fadeSeconds;
        vm.ReplaceQueueAndPlay(new[] { Trk("a"), Trk("b") }, 0);
        Dispatcher.UIThread.RunJobs();
        var t = typeof(PlayerViewModel);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        t.GetField("_lastSeekTime", flags)!.SetValue(vm, DateTime.MinValue);
        t.GetField("_autoMixCommitGuardUntilUtc", flags)!.SetValue(vm, DateTime.MinValue);
        Assert.Equal(PlaybackState.Playing, vm.State);
        return (vm, player);
    }

    private static double Expected(double remainingSeconds, int fadeSeconds = 30) =>
        PlayerViewModel.SleepFadeGain(remainingSeconds - PlayerViewModel.PlaybackFadeTickMs / 1000.0, fadeSeconds);

    [Fact]
    public void Curve_IsQuadratic_OneBeforeTheWindow_ZeroAtTheEnd()
    {
        Assert.Equal(1.0, PlayerViewModel.SleepFadeGain(60, 30));
        Assert.Equal(1.0, PlayerViewModel.SleepFadeGain(30, 30));
        Assert.Equal(0.25, PlayerViewModel.SleepFadeGain(15, 30), 9);
        Assert.Equal(0.01, PlayerViewModel.SleepFadeGain(3, 30), 9);
        Assert.Equal(0.0, PlayerViewModel.SleepFadeGain(0, 30));
        Assert.Equal(0.0, PlayerViewModel.SleepFadeGain(-4, 30));
        Assert.Equal(1.0, PlayerViewModel.SleepFadeGain(5, 0)); // fade off
        // Monotonic over the window.
        var last = 1.0;
        for (var s = 30.0; s >= 0; s -= 0.25)
        {
            var g = PlayerViewModel.SleepFadeGain(s, 30);
            Assert.True(g <= last + 1e-12);
            last = g;
        }
    }

    [AvaloniaFact]
    public void Timer_FadesOverTheLastSeconds_PausesAtSilence_VolumeUntouched()
    {
        var (vm, player) = CreatePlaying();
        var volumeBefore = vm.Volume;
        var playerVolumeBefore = player.Volume;
        vm.StartSleepTimerCommand.Execute("15");
        var end = _now.AddMinutes(15);

        _now = end.AddSeconds(-45);
        vm.PlaybackFadeTick();
        Assert.Empty(player.FadeGainCalls); // outside the window: nothing sent

        _now = end.AddSeconds(-15);
        vm.PlaybackFadeTick();
        var (gain, ramp) = player.FadeGainCalls[^1];
        Assert.Equal(Expected(15), gain, 6);
        Assert.Equal(PlayerViewModel.PlaybackFadeTickMs, ramp);

        _now = end.AddSeconds(-1);
        vm.PlaybackFadeTick();
        Assert.Equal(Expected(1), player.FadeGain, 6);
        Assert.True(player.FadeGain < 0.002);

        // Every step on the way only went down.
        for (var i = 1; i < player.FadeGainCalls.Count; i++)
            Assert.True(player.FadeGainCalls[i].Gain <= player.FadeGainCalls[i - 1].Gain);

        _now = end;
        vm.PlaybackFadeTick();
        Assert.Equal(PlaybackState.Paused, vm.State);
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.False(vm.IsSleepTimerActive);
        Assert.Equal(0.0, player.FadeGain); // held silent while the pause lands
        Assert.Equal(volumeBefore, vm.Volume);
        Assert.Equal(playerVolumeBefore, player.Volume);
    }

    [AvaloniaFact]
    public void AfterTheTimerPaused_NextPlay_IsAtFullLevel()
    {
        var (vm, player) = CreatePlaying();
        vm.StartSleepTimerCommand.Execute("15");
        _now = _now.AddMinutes(15);
        vm.PlaybackFadeTick();
        Assert.Equal(0.0, player.FadeGain);

        vm.PlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(1.0, player.FadeGain);
        Assert.Equal(ChannelMixProvider.RampMs, player.FadeGainCalls[^1].RampMs);
    }

    [AvaloniaFact]
    public void CancellingMidFade_RestoresTheLevelAtOnce()
    {
        var (vm, player) = CreatePlaying();
        vm.StartSleepTimerCommand.Execute("15");
        _now = _now.AddMinutes(15).AddSeconds(-10);
        vm.PlaybackFadeTick();
        Assert.True(player.FadeGain < 0.2);

        vm.CancelSleepTimerCommand.Execute(null);

        Assert.Equal((1.0, ChannelMixProvider.RampMs), player.FadeGainCalls[^1]);
        Assert.Equal(PlaybackState.Playing, vm.State);
        vm.PlaybackFadeTick(); // the ticker has nothing left to do
        Assert.Equal(1.0, player.FadeGain);
    }

    [AvaloniaFact]
    public void FadeOff_NeverTouchesTheGain_AndStillPauses()
    {
        var (vm, player) = CreatePlaying(fadeSeconds: 0);
        vm.StartSleepTimerCommand.Execute("15");
        _now = _now.AddMinutes(15).AddSeconds(-5);
        vm.PlaybackFadeTick();
        _now = _now.AddSeconds(5);
        vm.PlaybackFadeTick();

        Assert.Equal(PlaybackState.Paused, vm.State);
        Assert.Empty(player.FadeGainCalls);
    }

    [AvaloniaFact]
    public void StopAfterCurrentTrack_FadesOverTheEndOfTheTrack_ThenNextPlayIsFull()
    {
        var (vm, player) = CreatePlaying();
        vm.StopAfterCurrentTrack = true;

        player.RaisePositionChanged(TimeSpan.FromSeconds(120)); // 60 s left
        Dispatcher.UIThread.RunJobs();
        vm.PlaybackFadeTick();
        Assert.Equal(1.0, player.FadeGain);

        player.RaisePositionChanged(TimeSpan.FromSeconds(170)); // 10 s left
        Dispatcher.UIThread.RunJobs();
        vm.PlaybackFadeTick();
        Assert.Equal(Expected(10), player.FadeGain, 3);

        player.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlaybackState.Stopped, vm.State);
        Assert.False(vm.StopAfterCurrentTrack);
        Assert.Equal(0.0, player.FadeGain); // not lifted over the stopped output

        vm.PlayPauseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PlaybackState.Playing, vm.State);
        Assert.Equal(1.0, player.FadeGain);
    }

    [AvaloniaFact]
    public void StopAfterTurnedOffMidFade_RestoresTheLevel()
    {
        var (vm, player) = CreatePlaying();
        vm.StopAfterCurrentTrack = true;
        player.RaisePositionChanged(TimeSpan.FromSeconds(172));
        Dispatcher.UIThread.RunJobs();
        vm.PlaybackFadeTick();
        Assert.True(player.FadeGain < 0.1);

        vm.StopAfterCurrentTrack = false;

        Assert.Equal((1.0, ChannelMixProvider.RampMs), player.FadeGainCalls[^1]);
    }
}
