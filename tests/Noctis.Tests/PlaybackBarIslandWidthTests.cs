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
/// (590px). Avalonia leaves transitions enabled on a control that has never been detached,
/// so the mount-time write used to play as a visible 590→340 shrink the first time the
/// lyrics page opened. Establishing writes must land instantly; only live state changes
/// may animate.
/// </summary>
public class PlaybackBarIslandWidthTests
{
    private const double IslandBaseWidth = 590;
    private const double IslandLyricsPageWidth = 340;

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
            Assert.Equal(IslandLyricsPageWidth, Island(bar).Width);
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

            Assert.Equal(IslandBaseWidth, Island(bar).Width);
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

            Assert.Equal(IslandLyricsPageWidth, Island(bar).Width);
        }
        finally
        {
            win.Close();
        }
    }
}
