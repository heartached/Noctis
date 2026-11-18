using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The player-bar island carries a 200ms Width transition, and the lyrics page mounts its
/// own copy of the bar in the compact (340px) state while the XAML declares the base width
/// (626px). Avalonia leaves transitions enabled on a control that has never been detached,
/// so the mount-time write used to play as a visible full→340 shrink the first time the
/// lyrics page opened. Establishing writes must land instantly; only live state changes
/// may animate.
/// </summary>
public class PlaybackBarIslandWidthTests
{
    private const double IslandBaseWidth = 536; // 626 with the long track info, 590 before the favorite heart
    private const double IslandLyricsPageWidth = 340;
    // Every optional island button adds one 34px button + 2px spacing; the mini player
    // button (#80) is one of them and is on by default.
    private const double ExtraButtonWidth = 36;

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Border Island(PlaybackBarView bar) =>
        Assert.IsType<Border>(bar.FindControl<Border>("IslandBorder"));

    [AvaloniaFact]
    public void MountingOnTheLyricsPage_StartsCompactWithoutAnimatingDown()
    {
        var player = MakePlayer();
        player.IsLyricsPageActive = true;

        var bar = new PlaybackBarView { DataContext = player };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            // Read before any render tick: a running transition would still be at (or
            // near) the XAML base width here and glide down over the next 200ms.
            Assert.Equal(IslandLyricsPageWidth + ExtraButtonWidth, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void MountingOnTheMainWindow_StaysAtTheBaseWidth()
    {
        var player = MakePlayer();
        player.IsLyricsPageActive = true;

        // The persistent bottom bar opts out of the compact state entirely.
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void DataContextArrivingAfterMount_StillResolvesTheCompactWidth()
    {
        var player = MakePlayer();
        player.IsLyricsPageActive = true;

        var bar = new PlaybackBarView();
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IslandBaseWidth, Island(bar).Width);

            bar.DataContext = player;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(IslandLyricsPageWidth + ExtraButtonWidth, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void StoredUserResize_IsEstablishedInstantlyOnTheMainWindowBar()
    {
        var player = MakePlayer();
        // Simulates SettingsViewModel hydrating a persisted resize at startup.
        player.PlaybackBarIslandWidth = 720;

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            // Same establishing-write rule as the compact mount: the stored width must
            // be in place before any render tick, never animated into.
            Assert.Equal(720, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void NarrowUserResize_HidesTrackInfo_AndWideningRestoresIt()
    {
        var player = MakePlayer();
        player.PlaybackBarIslandWidth = 420; // below the 500px compact-shape threshold

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var trackInfo = bar.FindControl<Grid>("TrackInfoPanel");
            Assert.NotNull(trackInfo);
            Assert.Equal(420, Island(bar).Width);
            Assert.False(trackInfo!.IsVisible);

            // Back to the stock width: the full layout returns.
            player.PlaybackBarIslandWidth = IslandBaseWidth;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, Island(bar).Width);
            Assert.True(trackInfo.IsVisible);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>The "…" lives inside the track box; the right cluster's OptionsButton
    /// (which owns the MenuFlyout) only stands in while the box is hidden — the compact
    /// resize shape and the lyrics page — so the lyrics-only menu entries stay reachable.</summary>
    [AvaloniaFact]
    public void OptionsFallback_ShowsOnlyWhileTheTrackBoxIsHidden()
    {
        var player = MakePlayer();

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var trackInfo = bar.FindControl<Grid>("TrackInfoPanel")!;
            var boxDots = bar.FindControl<Button>("BoxOptionsButton")!;
            var fallbackDots = bar.FindControl<Button>("OptionsButton")!;

            Assert.True(trackInfo.IsVisible);
            Assert.True(boxDots.IsVisible);
            Assert.False(fallbackDots.IsVisible);

            player.PlaybackBarIslandWidth = 420; // compact shape: box gone, fallback in
            Dispatcher.UIThread.RunJobs();

            Assert.False(trackInfo.IsVisible);
            Assert.True(fallbackDots.IsVisible);

            player.PlaybackBarIslandWidth = IslandBaseWidth;
            Dispatcher.UIThread.RunJobs();

            Assert.True(trackInfo.IsVisible);
            Assert.False(fallbackDots.IsVisible);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void RepeatAndFavorite_AreHiddenUntilTheirSettingsTurnThemOn()
    {
        var player = MakePlayer();

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var heart = bar.FindControl<Button>("FavoriteButton")!;
            Assert.False(heart.IsVisible);
            Assert.Equal(IslandBaseWidth + ExtraButtonWidth, Island(bar).Width);

            // Each extra widens the stock pill by one 34px button + 2px spacing,
            // exactly like shuffle / sleep / speed already do.
            player.IslandShowFavorite = true;
            player.IslandShowRepeat = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(heart.IsVisible);
            Assert.Equal(IslandBaseWidth + 3 * ExtraButtonWidth, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>GitHub #80: the mini player button is on by default and widens the pill like
    /// any other extra; Settings → Player can take it off, which hands the 36px back. On the
    /// lyrics page (where the album-art toggle is hidden with the track box) the whole row
    /// still fits inside the compact pill.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiniPlayerButton_ShownByDefault_AndItsSettingRemovesIt(bool lyricsPage)
    {
        var player = MakePlayer();
        player.IsLyricsPageActive = lyricsPage;

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = lyricsPage };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var stock = lyricsPage ? IslandLyricsPageWidth : IslandBaseWidth;
            var button = bar.FindControl<Button>("MiniPlayerButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(stock + ExtraButtonWidth, Island(bar).Width);

            // The content row (transport … volume) fits inside the island's chrome (12px
            // padding each side + the 1.5px ring); wider would overflow the rounded ends.
            var row = Assert.IsType<Grid>(button.Parent!.Parent);
            var inner = Island(bar).Width - 24 - 3;
            Assert.True(row.DesiredSize.Width <= inner + 0.5,
                $"row needs {row.DesiredSize.Width:F1}px, the island has {inner:F1}");

            player.IslandShowMiniPlayer = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(button.IsVisible);
            Assert.Equal(stock, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }

    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Settings › Player › island width: the slider used to stop at 1400 while the
    /// grip drag goes as wide as the window, so a wider bar read "1400 px" and touching the
    /// slider shrank it. The slider now reaches as far as the drag can (the bar's host width)
    /// and is never below the stored width, which it shows as it is.</summary>
    [AvaloniaFact]
    public async Task SettingsSlider_ShowsAWidthPastTheOld1400Cap_AndTouchingItDoesNotShrinkTheBar()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var bar1 = (Window?)null;
        var settingsWindow = (Window?)null;
        try
        {
            var persistence = new Noctis.Services.PersistenceService(root);
            await persistence.SaveSettingsAsync(new Noctis.Models.AppSettings { PlaybackBarWidth = 1800 });
            var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
            var player = MakePlayer();
            vm.SetPlayer(player); // wired both ways, as MainWindowViewModel does
            player.SetSettingsViewModel(vm);
            await vm.LoadAsync();
            Assert.Equal(1800, vm.PlaybackBarIslandWidth);
            Assert.Equal(1800, player.PlaybackBarIslandWidth);

            // The bar in a 2000px window: the drag limit it reports sizes the slider.
            // The main window's bar (MainWindow.axaml: CompactWhenLyricsPageActive="False").
            var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
            bar1 = new Window { Width = 2000, Height = 200, Content = bar };
            bar1.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Island(bar).MaxWidth, player.IslandAvailableWidth);
            Assert.True(player.IslandAvailableWidth > 1900);
            Assert.Equal(player.IslandAvailableWidth, vm.PlaybackBarIslandSliderMaximum);

            // A grip drag wider than the old cap reaches the Settings value unclamped.
            player.CommitPlaybackBarWidth(1900);
            Assert.Equal(1900, vm.PlaybackBarIslandWidth);

            vm.SelectedSettingsTab = SettingsViewModel.TabPlayer;
            var view = new SettingsView { DataContext = vm };
            settingsWindow = new Window { Width = 920, Height = 720, Content = view };
            settingsWindow.Show();
            Dispatcher.UIThread.RunJobs();
            var slider = view.FindControl<Slider>("IslandWidthSlider")!;
            Assert.Equal(1900, slider.Value);
            Assert.Equal(vm.PlaybackBarIslandSliderMaximum, slider.Maximum);
            Assert.Equal(1900, vm.PlaybackBarIslandWidth); // showing it did not clamp it

            // Touching the slider moves the width from where it is, not from 1400.
            slider.Value = 1880;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1880, vm.PlaybackBarIslandWidth);
            Assert.Equal(1880, player.PlaybackBarIslandWidth);

            // A stored width wider than the window (set on a bigger screen) still reads as
            // itself: the slider's end grows to it instead of cutting it down.
            player.CommitPlaybackBarWidth(3000);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3000, vm.PlaybackBarIslandWidth);
            Assert.Equal(3000, vm.PlaybackBarIslandSliderMaximum);
            Assert.Equal(3000, slider.Value);
            Assert.Equal(3000, slider.Maximum);
        }
        finally
        {
            bar1?.Close();
            settingsWindow?.Close();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
