using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23 (two users): Settings › Player Island › Always Show Volume Slider puts the volume
/// slider on the island next to the Volume button instead of the pop-up; the button then mutes.
/// The stock-width island grows by the slider's room; the compact shape keeps the pop-up.
/// </summary>
public class IslandInlineVolumeTests
{
    private const double StockWidth = 536 + 36; // + the mini player button, on by default
    private const double InlineWidth = 72;

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static (Window win, PlaybackBarView bar) Show(PlayerViewModel player)
    {
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1400, Height = 200, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return (win, bar);
    }

    private static bool WaitFor(Func<bool> condition, int budgetMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    [AvaloniaFact]
    public void On_TheSliderSitsOnTheIsland_AndTheStockIslandGrowsByItsRoom()
    {
        var player = MakePlayer();
        player.IslandInlineVolume = true;
        var (win, bar) = Show(player);
        try
        {
            var island = bar.FindControl<Border>("IslandBorder")!;
            var host = bar.FindControl<Border>("InlineVolumeHost")!;
            var canvas = bar.FindControl<Canvas>("InlineVolumeCanvas")!;
            var pill = bar.FindControl<Grid>("VolumePill")!;

            Assert.True(bar.IsVolumeInline);
            Assert.Equal(StockWidth + InlineWidth, island.Width);
            Assert.True(host.IsVisible);
            Assert.Equal(InlineWidth, host.Width);
            Assert.Same(canvas, pill.Parent);
            Assert.True(bar.FindControl<Grid>("TrackInfoPanel")!.IsVisible);
            // The pill is the slider alone on the island, and it fits the slot.
            Assert.False(bar.FindControl<TextBlock>("VolumeReadout")!.IsEffectivelyVisible);
            win.UpdateLayout();
            Assert.True(pill.Bounds.Width <= InlineWidth + 0.5, $"pill {pill.Bounds.Width}");
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void On_TheVolumeButtonMutes_InsteadOfOpeningThePopup()
    {
        var player = MakePlayer();
        player.IslandInlineVolume = true;
        var (win, bar) = Show(player);
        try
        {
            var button = bar.FindControl<Button>("VolumeButton")!;
            Assert.False(player.IsMuted);

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(player.IsMuted);
            Assert.False(bar.FindControl<Popup>("VolumeFlyout")!.IsOpen);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Off_TheVolumeButtonStillOpensThePopup()
    {
        var player = MakePlayer();
        var (win, bar) = Show(player);
        try
        {
            Assert.False(bar.IsVolumeInline);
            Assert.False(bar.FindControl<Border>("InlineVolumeHost")!.IsVisible);
            Assert.Equal(StockWidth, bar.FindControl<Border>("IslandBorder")!.Width);

            bar.FindControl<Button>("VolumeButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(bar.FindControl<Popup>("VolumeFlyout")!.IsOpen);
            Assert.False(player.IsMuted);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void CompactShape_FallsBackToThePopup()
    {
        var player = MakePlayer();
        player.IslandInlineVolume = true;
        player.PlaybackBarIslandWidth = 420; // the compact pill: no track box, no room
        var (win, bar) = Show(player);
        try
        {
            Assert.False(bar.IsVolumeInline);
            Assert.Same(bar.FindControl<Avalonia.Controls.Decorator>("VolumeFlyoutContent"), bar.FindControl<Grid>("VolumePill")!.Parent);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void TogglingLive_SlidesInAndOut_AndTheTrackBoxNeverBlinks()
    {
        var player = MakePlayer();
        var (win, bar) = Show(player);
        try
        {
            var island = bar.FindControl<Border>("IslandBorder")!;
            var host = bar.FindControl<Border>("InlineVolumeHost")!;
            var trackInfo = bar.FindControl<Grid>("TrackInfoPanel")!;
            var pill = bar.FindControl<Grid>("VolumePill")!;

            player.IslandInlineVolume = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsVolumeInline);
            // Mid-slide the island is still narrower than its target; the track box stays.
            var sawTrackBoxHidden = false;
            Assert.True(WaitFor(() =>
            {
                sawTrackBoxHidden |= !trackInfo.IsVisible;
                return Math.Abs(island.Bounds.Width - (StockWidth + InlineWidth)) < 0.5
                    && Math.Abs(host.Bounds.Width - InlineWidth) < 0.5;
            }), $"island {island.Bounds.Width}, slot {host.Bounds.Width}");
            Assert.False(sawTrackBoxHidden);

            player.IslandInlineVolume = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsVolumeInline);
            Assert.True(WaitFor(() => !host.IsVisible
                && Math.Abs(island.Bounds.Width - StockWidth) < 0.5
                && ReferenceEquals(pill.Parent, bar.FindControl<Avalonia.Controls.Decorator>("VolumeFlyoutContent"))));
            Assert.False(sawTrackBoxHidden);
        }
        finally { win.Close(); }
    }
}
