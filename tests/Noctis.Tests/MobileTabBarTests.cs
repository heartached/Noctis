using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The glass tab bar: the Playlists tab, and the bar folding into the compact
/// row (current tab · mini player · Search) on scroll down and back out on scroll up.</summary>
public class MobileTabBarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NavTestPage : MobilePage
    {
        public override string Title => "Pushed";
    }

    private long _now = 10_000;

    private ShellViewModel MakeShell()
    {
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(_root);
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
        shell.TickSource = () => _now;
        return shell;
    }

    /// <summary>Scrolls down in 10 px steps, the way a drag reports.</summary>
    private static void ScrollBy(ShellViewModel shell, ref double offset, double distance)
    {
        var step = Math.Sign(distance) * 10.0;
        for (var moved = 0.0; Math.Abs(moved) < Math.Abs(distance); moved += step)
        {
            offset = Math.Max(0, offset + step);
            shell.ReportContentScroll(offset, step);
        }
    }

    [AvaloniaFact]
    public void PlaylistsTab_IsItsOwnRoot_WithFavouriteSongsFirst()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Loved", favourite: true), MobileFixtures.Song("Plain") });
        var shell = rig.Shell;
        var window = MobileFixtures.Mount(shell, out var view);

        shell.SelectTabCommand.Execute(MobileTab.Playlists);
        window.UpdateLayout();

        Assert.True(shell.IsPlaylistsSelected);
        Assert.True(shell.IsPlaylistsRootVisible);
        Assert.False(shell.IsLibraryRootVisible);
        Assert.True(shell.PlaylistsRoot.IsEmbedded);
        var favourites = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "FavouriteSongsRow" && b.IsEffectivelyVisible);
        favourites.Command!.Execute(null);
        var page = Assert.IsType<SongListPageViewModel>(shell.CurrentPage);
        Assert.Equal(new[] { "Loved" }, page.Songs.Select(t => t.Title));
        window.Close();
    }

    [Fact]
    public void ScrollingDown_PastTheThreshold_CollapsesTheBar_AndJitterDoesNot()
    {
        var shell = MakeShell();
        var offset = 0.0;

        ScrollBy(shell, ref offset, 20);                  // under the threshold
        Assert.False(shell.IsTabBarCollapsed);

        ScrollBy(shell, ref offset, 60);
        Assert.True(shell.IsTabBarCollapsed);
    }

    [Fact]
    public void ScrollingUp_ExpandsTheBar_AndSoDoesReachingTheTop()
    {
        var shell = MakeShell();
        var offset = 0.0;
        ScrollBy(shell, ref offset, 400);
        Assert.True(shell.IsTabBarCollapsed);

        ScrollBy(shell, ref offset, -10);                 // a twitch back up is not enough
        Assert.True(shell.IsTabBarCollapsed);
        ScrollBy(shell, ref offset, -40);
        Assert.False(shell.IsTabBarCollapsed);

        ScrollBy(shell, ref offset, 200);
        Assert.True(shell.IsTabBarCollapsed);
        shell.ReportContentScroll(0, -offset);            // a jump to the top (status-bar tap)
        Assert.False(shell.IsTabBarCollapsed);
    }

    [Fact]
    public void ChangingTabOrPage_ExpandsTheBar()
    {
        var shell = MakeShell();
        var offset = 0.0;
        ScrollBy(shell, ref offset, 400);
        Assert.True(shell.IsTabBarCollapsed);
        shell.SelectTabCommand.Execute(MobileTab.Home);
        Assert.False(shell.IsTabBarCollapsed);

        _now += 5_000;
        ScrollBy(shell, ref offset, 400);
        Assert.True(shell.IsTabBarCollapsed);
        shell.Navigate(new NavTestPage());
        Assert.False(shell.IsTabBarCollapsed);
    }

    [Fact]
    public void AScrollRestoreRightAfterNavigating_DoesNotCollapseTheBar()
    {
        var shell = MakeShell();
        shell.Navigate(new NavTestPage());

        shell.ReportContentScroll(1800, 1800);            // ScrollMemory putting a page back
        Assert.False(shell.IsTabBarCollapsed);

        _now += 1_000;
        var offset = 1800.0;
        ScrollBy(shell, ref offset, 60);                  // the user's own scroll later still folds it
        Assert.True(shell.IsTabBarCollapsed);
    }

    [Fact]
    public void ExpandTabBarCommand_UnfoldsIt()
    {
        var shell = MakeShell();
        var offset = 0.0;
        ScrollBy(shell, ref offset, 400);

        shell.ExpandTabBarCommand.Execute(null);

        Assert.False(shell.IsTabBarCollapsed);
    }

    [AvaloniaFact]
    public void ScrollingAPage_CollapsesTheBar_ThroughTheShell()
    {
        using var rig = MobileFixtures.MakeRig(Enumerable.Range(0, 200).Select(i => MobileFixtures.Song("Song " + i)).ToArray());
        var shell = rig.Shell;
        shell.SelectLibraryChipCommand.Execute(LibraryChip.Songs);
        var window = MobileFixtures.Mount(shell, out var view);

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "SongScroll" && s.IsEffectivelyVisible);
        for (var y = 10; y <= 300; y += 10)
        {
            scroll.Offset = new Avalonia.Vector(0, y);
            window.UpdateLayout();
        }

        Assert.True(shell.IsTabBarCollapsed);
        // Folded: the round current-tab button takes taps, the four tabs no longer do.
        Assert.True(view.FindControl<Button>("CollapsedTabButton")!.IsHitTestVisible);
        Assert.False(view.FindControl<Panel>("TabRow")!.IsHitTestVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TheBar_HasFourTabs_InOrder()
    {
        var shell = MakeShell();
        var window = MobileFixtures.Mount(shell, out var view);

        var names = new[] { "HomeTab", "PlaylistsTab", "LibraryTab", "SearchTab" };
        var xs = names.Select(n => view.FindControl<Button>(n)!).Select(b => b.TranslatePoint(default, view)!.Value.X).ToArray();
        Assert.Equal(xs.OrderBy(x => x), xs);
        window.Close();
    }

    [AvaloniaFact]
    public void PageLists_EndBelowTheBar_SoTheLastRowCanScrollClearOfIt()
    {
        using var rig = MobileFixtures.MakeRig(Enumerable.Range(0, 200).Select(i => MobileFixtures.Song("Song " + i)).ToArray());
        rig.Shell.SelectLibraryChipCommand.Execute(LibraryChip.Songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "SongScroll" && s.IsEffectivelyVisible);
        scroll.Offset = new Avalonia.Vector(0, double.MaxValue);
        window.UpdateLayout();

        var list = view.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "SongList" && i.IsEffectivelyVisible);
        var listBottom = list.TranslatePoint(new Avalonia.Point(0, list.Bounds.Height), view)!.Value.Y;
        var chromeTop = view.FindControl<Panel>("ChromeHost")!.TranslatePoint(default, view)!.Value.Y;
        Assert.True(listBottom <= chromeTop, $"list ends at {listBottom}, bar starts at {chromeTop}");
        window.Close();
    }
}
