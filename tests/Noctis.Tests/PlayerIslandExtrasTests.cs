using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

// Podcast/Audiobook island buttons (Discord, Luwi, 08-26): relative skip and
// playback speed on the player view model behind the new bar buttons.
public class PlayerIslandExtrasTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) Make()
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player);
    }

    private static void LoadTrack(PlayerViewModel vm, int seconds)
    {
        vm.CurrentTrack = new Track { Title = "T", FilePath = "t.mp3", Duration = TimeSpan.FromSeconds(seconds) };
        vm.Duration = TimeSpan.FromSeconds(seconds);
        vm.Position = TimeSpan.FromSeconds(60);
    }

    [AvaloniaFact]
    public void SkipForward_AdvancesBySkipSeconds()
    {
        var (vm, _) = Make();
        LoadTrack(vm, 300);
        vm.IslandSkipSeconds = 15;

        vm.SkipForwardCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(75), vm.Position);
    }

    [AvaloniaFact]
    public void SkipBack_ClampsAtTheStart()
    {
        var (vm, _) = Make();
        LoadTrack(vm, 300);
        vm.Position = TimeSpan.FromSeconds(5);
        vm.IslandSkipSeconds = 30;

        vm.SkipBackCommand.Execute(null);

        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public void SkipForward_ClampsAtTheEnd()
    {
        var (vm, _) = Make();
        LoadTrack(vm, 100);
        vm.Position = TimeSpan.FromSeconds(95);
        vm.IslandSkipSeconds = 10;

        vm.SkipForwardCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(100), vm.Position);
    }

    [AvaloniaFact]
    public void Skip_WithoutATrack_IsANoOp()
    {
        var (vm, _) = Make();
        vm.SkipForwardCommand.Execute(null);
        vm.SkipBackCommand.Execute(null);
        Assert.Equal(TimeSpan.Zero, vm.Position);
    }

    [AvaloniaFact]
    public void SkipTooltips_FollowTheSetting()
    {
        var (vm, _) = Make();
        vm.IslandSkipSeconds = 30;
        Assert.Equal("30", vm.IslandSkipLabel);
        Assert.Equal("Back 30 seconds", vm.SkipBackTooltip);
        Assert.Equal("Forward 30 seconds", vm.SkipForwardTooltip);
    }

    [AvaloniaFact]
    public void PlaybackRate_ReachesTheAudioPlayer_AndFormatsItsLabel()
    {
        var (vm, player) = Make();
        Assert.Equal("1×", vm.PlaybackRateText);
        Assert.False(vm.IsPlaybackRateChanged);

        vm.SetPlaybackRateCommand.Execute("150");

        Assert.Equal(150, vm.PlaybackRatePercent);
        Assert.Equal(1.5, player.PlaybackRate);
        Assert.Equal("1.5×", vm.PlaybackRateText);
        Assert.True(vm.IsPlaybackRateChanged);

        vm.SetPlaybackRateCommand.Execute("125");
        Assert.Equal("1.25×", vm.PlaybackRateText);
    }

    [AvaloniaFact]
    public void PlaybackRate_RejectsOutOfRangeAndGarbage()
    {
        var (vm, player) = Make();
        vm.SetPlaybackRateCommand.Execute("300");
        vm.SetPlaybackRateCommand.Execute("fast");
        vm.SetPlaybackRateCommand.Execute(null);
        Assert.Equal(100, vm.PlaybackRatePercent);
        Assert.Equal(1.0, player.PlaybackRate);
    }
}
