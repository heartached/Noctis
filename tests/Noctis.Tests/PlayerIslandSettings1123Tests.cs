using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23 settings: Keep Shuffle On and the island time's total/remaining choice reach the
/// player and are saved; Player Island Width gets a Reset beside its slider (shown only for a
/// width of the user's own); the island's double-click reset keeps the extra buttons' room.
/// </summary>
public class PlayerIslandSettings1123Tests
{
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static async Task<(SettingsViewModel settings, PlayerViewModel player)> Loaded()
    {
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService());
        var player = MakePlayer();
        settings.SetPlayer(player);
        player.SetSettingsViewModel(settings); // as MainWindowViewModel wires them
        await settings.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return (settings, player);
    }

    [AvaloniaFact]
    public async Task KeepShuffleOn_ReachesThePlayer_AndIsOffByDefault()
    {
        var (settings, player) = await Loaded();
        Assert.False(settings.KeepShuffleOn);
        Assert.False(player.KeepShuffleOn);

        settings.KeepShuffleOn = true;
        Assert.True(player.KeepShuffleOn);
    }

    [AvaloniaFact]
    public async Task ClickingTheIslandTime_GoesThroughSettings()
    {
        var (settings, player) = await Loaded();
        Assert.False(player.IslandTimeShowsTotal);

        player.ToggleIslandTimeDisplayCommand.Execute(null);

        Assert.True(settings.PlaybackBarTimeShowsTotal);
        Assert.True(player.IslandTimeShowsTotal);
    }

    [AvaloniaFact]
    public async Task IslandWidthReset_ShowsOnlyForACustomWidth_AndRestoresTheStockWidth()
    {
        var (settings, player) = await Loaded();
        Assert.False(settings.IsPlaybackBarWidthCustom);

        settings.PlaybackBarIslandWidth = 720;
        Assert.True(settings.IsPlaybackBarWidthCustom);
        Assert.Equal(720, player.PlaybackBarIslandWidth);

        settings.ResetPlaybackBarWidthCommand.Execute(null);

        Assert.False(settings.IsPlaybackBarWidthCustom);
        Assert.Equal(SettingsViewModel.PlaybackBarDefaultWidth, settings.PlaybackBarIslandWidth);
        Assert.Equal(SettingsViewModel.PlaybackBarDefaultWidth, player.PlaybackBarIslandWidth);
    }

    [AvaloniaFact]
    public void DoubleClickingTheEdgeGrip_KeepsTheExtraButtonsRoom()
    {
        var player = MakePlayer();
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1200, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var island = bar.FindControl<Border>("IslandBorder")!;
            var stock = island.Width; // 536 + the stock extras (the mini player button)
            Assert.True(stock > SettingsViewModel.PlaybackBarDefaultWidth);

            var grip = bar.FindControl<Border>("RightResizeGrip")!;
            win.UpdateLayout();
            var at = grip.TranslatePoint(new Avalonia.Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), win)!.Value;
            win.MouseDown(at, MouseButton.Left);
            win.MouseUp(at, MouseButton.Left);
            win.MouseDown(at, MouseButton.Left);
            win.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SettingsViewModel.PlaybackBarDefaultWidth, player.PlaybackBarIslandWidth);
            Assert.Equal(stock, island.Width);
        }
        finally { win.Close(); }
    }
}
