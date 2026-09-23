using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone navigation: tabs, the page stack, the Back order, safe-area padding and
/// the scroll offset a pushed page keeps across Back.</summary>
public class MobileNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NavTestPage : MobilePage
    {
        private readonly string _title;
        public NavTestPage(string title) => _title = title;
        public override string Title => _title;
        public int Closed { get; private set; }
        public override void OnClosed() => Closed++;
    }

    private ShellViewModel MakeShell(params Track[] tracks)
    {
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        var persistence = new PersistenceService(_root);
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        return new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
    }

    private static (ShellView View, Window Window) Mount(ShellViewModel shell)
    {
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static Track Song(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Tester", FilePath = "content://x/" + title,
        Duration = TimeSpan.FromSeconds(90), AlbumArtworkPath = "/art/" + title + ".jpg",
    };

    [Fact]
    public void Navigate_PushesOverTheTab_AndBackPopsInOrder()
    {
        var shell = MakeShell();
        var first = new NavTestPage("First");
        var second = new NavTestPage("Second");

        shell.Navigate(first);
        shell.Navigate(second);
        Assert.Same(second, shell.CurrentPage);
        Assert.False(shell.IsLibraryRootVisible);

        Assert.True(shell.GoBack());
        Assert.Same(first, shell.CurrentPage);
        Assert.Equal(1, second.Closed);

        Assert.True(shell.GoBack());
        Assert.Null(shell.CurrentPage);
        Assert.True(shell.IsLibraryRootVisible);
        Assert.False(shell.GoBack());
    }

    [Fact]
    public void TryHandleBack_ClosesOverlays_ThenPopsPages_ThenReturnsToLibrary_ThenLetsTheSystemHaveIt()
    {
        var shell = MakeShell();
        shell.SelectTabCommand.Execute(MobileTab.Search);
        shell.Navigate(new NavTestPage("Pushed"));
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleQueueCommand.Execute(null);

        Assert.True(shell.TryHandleBack());
        Assert.False(shell.IsQueueOpen);
        Assert.True(shell.IsNowPlayingOpen);

        Assert.True(shell.TryHandleBack());
        Assert.False(shell.IsNowPlayingOpen);
        Assert.NotNull(shell.CurrentPage);

        Assert.True(shell.TryHandleBack());
        Assert.Null(shell.CurrentPage);
        Assert.Equal(MobileTab.Search, shell.SelectedTab);

        Assert.True(shell.TryHandleBack());                // a non-start tab goes back to Library
        Assert.Equal(MobileTab.Library, shell.SelectedTab);

        Assert.False(shell.TryHandleBack());               // Library root: the system finishes the activity
    }

    [Fact]
    public void TryHandleBack_OnTheLibraryTab_ResetsANonAllMusicChip_BeforeLeavingTheApp()
    {
        var shell = MakeShell();
        shell.SelectLibraryChipCommand.Execute(LibraryChip.Songs);
        shell.Navigate(new NavTestPage("Pushed"));

        Assert.True(shell.TryHandleBack());                // the pushed page first
        Assert.Null(shell.CurrentPage);
        Assert.Equal(LibraryChip.Songs, shell.LibraryChip);

        Assert.True(shell.TryHandleBack());                // then the chip returns to All Music
        Assert.Equal(LibraryChip.AllMusic, shell.LibraryChip);
        Assert.Null(shell.LibraryChipPage);

        Assert.False(shell.TryHandleBack());               // All Music root: the system finishes the activity
    }

    [Fact]
    public void SelectTab_ClearsTheStack_AndClosesEveryPage()
    {
        var shell = MakeShell();
        var a = new NavTestPage("A");
        var b = new NavTestPage("B");
        shell.Navigate(a);
        shell.Navigate(b);

        shell.SelectTabCommand.Execute(MobileTab.Library);   // re-tapping the current tab pops to its root
        Assert.Empty(shell.Pages);
        Assert.Null(shell.CurrentPage);
        Assert.Equal(1, a.Closed);
        Assert.Equal(1, b.Closed);

        shell.Navigate(new NavTestPage("C"));
        shell.SelectTabCommand.Execute(MobileTab.Home);
        Assert.Empty(shell.Pages);
        Assert.True(shell.IsHomeSelected);
        Assert.True(shell.IsHomeRootVisible);
        Assert.False(shell.IsLibraryRootVisible);
    }

    [Fact]
    public void Navigate_FromNowPlaying_ClosesThePlayerAndItsOverlays()
    {
        var shell = MakeShell();
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);

        shell.Navigate(new NavTestPage("Artist"));

        Assert.False(shell.IsNowPlayingOpen);
        Assert.False(shell.IsLyricsOpen);
        Assert.NotNull(shell.CurrentPage);
    }

    [AvaloniaFact]
    public void PageHost_ShowsTheTopPage_AndHidesTheLibraryRoot()
    {
        var shell = MakeShell();
        var (view, window) = Mount(shell);
        var library = view.GetVisualDescendants().OfType<LibraryPage>().Single();
        var host = view.FindControl<ContentControl>("PageHost")!;
        Assert.True(library.IsVisible);
        Assert.False(host.IsVisible);

        shell.Navigate(new NavTestPage("Pushed Page"));
        window.UpdateLayout();

        Assert.False(library.IsVisible);
        Assert.True(host.IsVisible);
        // No DataTemplate matches the test page, so Avalonia's default template shows ToString().
        Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Pushed Page");

        shell.NavigateBackCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(library.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void SafeArea_PadsTheTabContentAndTheBottomChrome()
    {
        var shell = MakeShell();
        var (view, window) = Mount(shell);

        view.ApplySafeArea(new Thickness(0, 24, 0, 48));
        window.UpdateLayout();

        Assert.Equal(24, shell.TopSafePadding.Top);
        Assert.Equal(24, view.FindControl<Panel>("TabContent")!.Margin.Top);
        Assert.Equal(48, view.FindControl<StackPanel>("BottomChrome")!.Margin.Bottom);
        window.Close();
    }

    /// <summary>Landscape with 3-button navigation or a side cutout: the bar sits on the left
    /// or right, and without the side insets the tab content drew under it.</summary>
    [AvaloniaFact]
    public void SafeArea_SideInsets_ReachTheTabContentAndTheBottomChrome()
    {
        var shell = MakeShell();
        var (view, window) = Mount(shell);

        view.ApplySafeArea(new Thickness(30, 24, 48, 16));
        window.UpdateLayout();

        Assert.Equal(new Thickness(30, 24, 48, 0), shell.TopSafePadding);
        Assert.Equal(new Thickness(30, 0, 48, 16), shell.BottomSafePadding);
        var content = view.FindControl<Panel>("TabContent")!.Margin;
        Assert.Equal((30.0, 48.0), (content.Left, content.Right));
        var chrome = view.FindControl<StackPanel>("BottomChrome")!.Margin;
        Assert.Equal((30.0, 48.0), (chrome.Left, chrome.Right));
        window.Close();
    }

    /// <summary>
    /// Noctis.UI's Styles.axaml pins every ScrollViewer and ScrollBar to AllowAutoHide=False
    /// (persistent desktop bars), and a non-auto-hide bar reserves its width, so every phone
    /// page stopped ~16 dp short of the right edge. The phone styles turn auto-hide back on,
    /// where Fluent's bar overlays the content. The headless app has no Noctis.UI styles, so
    /// the desktop rule is recreated on the window, below the shell as it is on the device.
    /// </summary>
    [AvaloniaFact]
    public void PhonePages_AutoHideTheirScrollBars_OverTheDesktopsPersistentBars()
    {
        var shell = MakeShell(Song("Tone"));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Styles.Add(new Style(x => x.OfType<ScrollViewer>())
        {
            Setters = { new Setter(ScrollViewer.AllowAutoHideProperty, false) },
        });
        window.Styles.Add(new Style(x => x.OfType<Avalonia.Controls.Primitives.ScrollBar>())
        {
            Setters = { new Setter(Avalonia.Controls.Primitives.ScrollBar.AllowAutoHideProperty, false) },
        });
        window.Show();
        window.UpdateLayout();

        var scroll = MobileFixtures.Named<ScrollViewer>(view, "LibraryScroll");
        Assert.True(scroll.AllowAutoHide);
        foreach (var bar in view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>())
            Assert.True(bar.AllowAutoHide);
        window.Close();
    }

    [AvaloniaFact]
    public void MiniBar_ShowsTheTracksArtwork_AndTheLibraryTabIsShown()
    {
        var track = Song("Tone A");
        var shell = MakeShell(track);
        shell.Library.InitializeAsync().GetAwaiter().GetResult();
        var (view, window) = Mount(shell);

        shell.PlaySongCommand.Execute(track);
        window.UpdateLayout();

        Assert.True(shell.IsMiniBarVisible);
        Assert.Equal(track.AlbumArtworkPath, view.FindControl<CachedImage>("MiniArtwork")!.SourcePath);
        Assert.True(view.FindControl<Button>("LibraryTab")!.IsVisible);

        shell.OpenNowPlayingCommand.Execute(null);
        Assert.False(shell.IsMiniBarVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ScrollMemory_RestoresThePageOffset_WhenItsViewIsRecreated()
    {
        var page = new NavTestPage("Long list");
        ScrollViewer MakeViewer()
        {
            var viewer = new ScrollViewer { DataContext = page, Content = new Border { Height = 3000 } };
            ScrollMemory.SetIsEnabled(viewer, true);
            return viewer;
        }

        var window = new Window { Width = 400, Height = 300, Content = MakeViewer() };
        window.Show();
        window.UpdateLayout();
        ((ScrollViewer)window.Content!).Offset = new Vector(0, 700);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(700, page.ScrollOffset.Y, 1);

        window.Content = MakeViewer();                    // what Back does: a fresh view over the same page
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(700, ((ScrollViewer)window.Content!).Offset.Y, 1);
        window.Close();
    }
}
