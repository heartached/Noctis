using System;
using System.Linq;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord #mobile-beta (Duckyvictus): the phone's sleep timer, the desktop's (PlayerViewModel's):
/// 15/30/45/60 minutes or End of Current Song, a fade over the last 30 seconds, then a pause.
/// The clock and the one-second check are the test's, so nothing here waits.
/// </summary>
public class MobileSleepTimerTests
{
    private sealed class ManualTick : IDisposable
    {
        public Action? Tick;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static (MobileFixtures.Rig Rig, NowPlayingViewModel Player, Func<DateTime> Clock, Action<TimeSpan> Advance, Func<ManualTick?> Ticker)
        Make(int songs = 3)
    {
        var tracks = Enumerable.Range(0, songs).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        foreach (var t in tracks) t.Duration = TimeSpan.FromMinutes(4);
        var rig = MobileFixtures.MakeRig(tracks);
        var player = rig.Shell.Player;
        var now = new DateTime(2026, 11, 7, 23, 0, 0, DateTimeKind.Utc);
        ManualTick? ticker = null;
        player.SleepClock = () => now;
        player.ScheduleSleepTick = tick => ticker = new ManualTick { Tick = tick };
        player.PlayTracks(tracks, 0);
        return (rig, player, () => now, d => now += d, () => ticker);
    }

    [Fact]
    public void Minutes_CountDown_FadeOverTheLast30Seconds_ThenPause()
    {
        var (rig, player, _, advance, ticker) = Make();
        using var lease = rig;

        player.StartSleepTimer(15);
        Assert.True(player.IsSleepTimerActive);
        Assert.True(player.HasSleepTimer);
        Assert.Equal(15, player.SleepTimerMinutes);
        Assert.Equal("15:00", player.SleepTimerRemainingText);
        Assert.Equal("Pauses in 15:00", player.SleepTimerStatus);

        advance(TimeSpan.FromMinutes(14));
        ticker()!.Tick!();
        Assert.Equal("1:00", player.SleepTimerRemainingText);
        Assert.Equal(1.0, rig.Player.FadeGain);

        advance(TimeSpan.FromSeconds(45));       // 15 s left: half the fade's span → (0.5)² = 0.25
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(10));
        Assert.Equal(0.25, rig.Player.FadeGain, 3);
        Assert.Equal(PlaybackState.Playing, rig.Player.State);

        advance(TimeSpan.FromSeconds(15));
        ticker()!.Tick!();
        Assert.Equal(PlaybackState.Paused, rig.Player.State);
        Assert.False(player.IsPlaying);
        Assert.False(player.HasSleepTimer);
        Assert.Equal(string.Empty, player.SleepTimerRemainingText);
        Assert.True(ticker()!.Disposed);
    }

    [Fact]
    public void ADeadlineThatPassesWhilePaused_OnlyEndsTheTimer()
    {
        var (rig, player, _, advance, ticker) = Make();
        using var lease = rig;
        player.StartSleepTimer(30);
        player.TogglePlayPauseCommand.Execute(null);   // paused by the user
        Assert.Equal(PlaybackState.Paused, rig.Player.State);

        advance(TimeSpan.FromMinutes(31));
        ticker()!.Tick!();
        Assert.False(player.HasSleepTimer);

        player.TogglePlayPauseCommand.Execute(null);   // played again later: nothing pauses it
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(30));
        Assert.Equal(PlaybackState.Playing, rig.Player.State);
    }

    [Fact]
    public void TurnOff_MidFade_BringsTheLevelBack()
    {
        var (rig, player, _, advance, ticker) = Make();
        using var lease = rig;
        player.StartSleepTimer(15);
        advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(10));
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(5));
        Assert.True(rig.Player.FadeGain < 0.2);

        player.CancelSleepTimer();

        Assert.Equal(1.0, rig.Player.FadeGain);
        Assert.False(player.HasSleepTimer);
        Assert.True(ticker()!.Disposed);
        Assert.Equal(PlaybackState.Playing, rig.Player.State);
    }

    [Fact]
    public void EndOfCurrentSong_StopsThere_WithTheNextSongReadyPaused()
    {
        var (rig, player, _, _, _) = Make();
        using var lease = rig;
        var first = player.CurrentTrack!;
        var second = player.UpNext[0];
        rig.Player.PreparedPaths.Clear();

        player.StopAfterThisSong();
        Assert.True(player.StopAfterCurrentSong);
        Assert.Equal("Pauses after this song", player.SleepTimerStatus);

        // The song's last seconds fade (the desktop's end-of-track fade).
        player.Duration = TimeSpan.FromMinutes(4);
        rig.Player.RaisePositionChanged(TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(15));
        Assert.Equal(0.25, rig.Player.FadeGain, 3);

        rig.Player.RaiseTrackEnded();

        Assert.Same(second, player.CurrentTrack);
        Assert.False(player.IsPlaying);
        Assert.Equal(PlaybackState.Stopped, rig.Player.State);
        Assert.False(player.HasSleepTimer);
        Assert.Equal(1, rig.Player.PlayedPaths.Count(p => p == first.FilePath));
        Assert.DoesNotContain(second.FilePath, rig.Player.PlayedPaths);

        player.TogglePlayPauseCommand.Execute(null);   // Play goes on from the next song
        Assert.Equal(second.FilePath, rig.Player.PlayedPaths[^1]);
    }

    [Fact]
    public void EndOfCurrentSong_EndsWhenAnotherSongIsPicked()
    {
        var (rig, player, _, _, _) = Make();
        using var lease = rig;
        player.StopAfterThisSong();

        player.NextCommand.Execute(null);

        Assert.False(player.StopAfterCurrentSong);
        rig.Player.RaiseTrackEnded();               // the new song's end plays on
        Assert.Equal(PlaybackState.Playing, rig.Player.State);
    }

    [Fact]
    public void Minutes_And_EndOfSong_AreOneChoice()
    {
        var (rig, player, _, _, _) = Make();
        using var lease = rig;
        player.StopAfterThisSong();
        player.StartSleepTimer(45);
        Assert.False(player.StopAfterCurrentSong);
        Assert.Equal(45, player.SleepTimerMinutes);

        player.StopAfterThisSong();
        Assert.False(player.IsSleepTimerActive);
        Assert.Equal(0, player.SleepTimerMinutes);
    }

    [Fact]
    public void TheMenu_TicksTheRunningChoice_AndOffersTurnOffOnlyWhileSet()
    {
        var (rig, player, _, _, _) = Make();
        using var lease = rig;
        var shell = rig.Shell;

        Assert.True(ContextSheetViewModel.ForTrack(shell, player.CurrentTrack!).CanSleepTimer);
        Assert.False(ContextSheetViewModel.ForTrack(shell, player.UpNext[0]).CanSleepTimer);

        shell.ShowSleepTimerMenuCommand.Execute(null);
        var menu = shell.OptionsMenu!;
        Assert.Equal("Sleep Timer", menu.Title);
        Assert.Equal(new[] { "15 Minutes", "30 Minutes", "45 Minutes", "1 Hour", "End of Current Song" }, menu.Items.Select(i => i.Label));
        Assert.DoesNotContain(menu.Items, i => i.IsChecked);

        menu.Items[1].PickCommand.Execute(null);
        Assert.Null(shell.OptionsMenu);
        Assert.Equal(30, player.SleepTimerMinutes);

        shell.ShowSleepTimerMenuCommand.Execute(null);
        menu = shell.OptionsMenu!;
        Assert.True(menu.Items[1].IsChecked);
        Assert.Equal("Turn Off Timer", menu.Items[^1].Label);
        Assert.True(menu.Items[^1].StartsGroup);
        Assert.Equal("Pauses in 30:00", menu.Detail);

        menu.Items[^1].PickCommand.Execute(null);
        Assert.False(player.HasSleepTimer);
    }

    [Theory]
    [InlineData(40, 30, 1.0)]
    [InlineData(30, 30, 1.0)]
    [InlineData(15, 30, 0.25)]
    [InlineData(0, 30, 0.0)]
    [InlineData(5, 0, 1.0)]
    public void TheFadeCurve_IsTheDesktops(double remaining, int fade, double gain) =>
        Assert.Equal(gain, NowPlayingViewModel.SleepFadeGain(remaining, fade), 6);
}
