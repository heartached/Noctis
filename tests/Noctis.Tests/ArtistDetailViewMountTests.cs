using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Headless mount of the new artist page and of the lyrics page's video backdrop
/// layer: pins that the XAML resolves its resources/bindings at runtime (compile-time
/// XAML checks don't catch a missing StaticResource) and that the sections realize.
/// </summary>
public class ArtistDetailViewMountTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static Album MakeAlbum(string name, string artist, int year, int trackCount)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        for (var i = 1; i <= trackCount; i++)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
                PlayCount = trackCount - i, Genre = "Alternative",
            });
        }
        album.TrackCount = trackCount;
        return album;
    }

    /// <summary>GitHub #122: a live album and a compilation get their own tab and Overview
    /// row ("Live Albums" / "Compilations"), the tile caption reads "Live Album · 2003",
    /// and each tab's grid realizes its tiles.</summary>
    [AvaloniaFact]
    public void ArtistPage_LiveAlbumsAndCompilations_GetTheirOwnTabsAndRows()
    {
        EnsureAppStyles();
        var live = MakeAlbum("Alive", "Chase Atlantic", 2003, 12);
        foreach (var t in live.Tracks) { t.ReleaseType = ReleaseType.Live; t.ReleaseTypeFromTag = true; }
        var hits = MakeAlbum("Hits", "Chase Atlantic", 2004, 15);
        foreach (var t in hits.Tracks) { t.ReleaseType = ReleaseType.Compilation; t.ReleaseTypeFromTag = true; }
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[] { MakeAlbum("Phases", "Chase Atlantic", 2019, 12), live, hits });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("page-tab") && b.IsVisible)
            .Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).ToList();
        Assert.Equal(new[] { "Overview", "Albums", "Singles & EPs", "Live Albums", "Compilations", "Songs", "Similar Artists" }, tabs);

        var overview = view.FindControl<StackPanel>("OverviewPanel")!;
        var overviewTexts = overview.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Live Albums", overviewTexts);
        Assert.Contains("Compilations", overviewTexts);
        Assert.Contains("Live Album · 2003 · 12 songs", overviewTexts);

        vm.SelectTabCommand.Execute("live");
        Dispatcher.UIThread.RunJobs();
        var livePanel = view.FindControl<StackPanel>("LivePanel")!;
        Assert.True(livePanel.IsVisible);
        Assert.Contains(livePanel.GetVisualDescendants().OfType<Control>().Select(c => c.DataContext), d => d is Album { Name: "Alive" });

        vm.SelectTabCommand.Execute("compilations");
        Dispatcher.UIThread.RunJobs();
        var compPanel = view.FindControl<StackPanel>("CompilationsPanel")!;
        Assert.True(compPanel.IsVisible);
        Assert.Contains(compPanel.GetVisualDescendants().OfType<Control>().Select(c => c.DataContext), d => d is Album { Name: "Hits" });
        win.Close();
    }

    /// <summary>GitHub #149: the Albums tab's grid / carousel toggle swaps the tile grid for
    /// the album carousel, which browses that artist's albums.</summary>
    [AvaloniaFact]
    public void ArtistPage_AlbumsTab_SwitchesToTheCarousel()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            MakeAlbum("Bad", "Michael Jackson", 1987, 10),
            MakeAlbum("Thriller", "Michael Jackson", 1982, 9),
            MakeAlbum("Invincible", "Michael Jackson", 2001, 16),
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var vm = new ArtistDetailViewModel("Michael Jackson", lib, player, settings: settings);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        vm.SelectTabCommand.Execute("albums");
        Dispatcher.UIThread.RunJobs();

        var panel = view.FindControl<StackPanel>("AlbumsPanel")!;
        var carousel = panel.GetVisualDescendants().OfType<AlbumCarousel>().Single();
        Assert.False(carousel.IsVisible);

        var toggle = panel.GetVisualDescendants().OfType<Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Show as Carousel");
        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.True(carousel.IsVisible);
        Assert.Equal("carousel", settings.ArtistAlbumsViewMode);
        Assert.Equal("Show as Grid", Avalonia.Automation.AutomationProperties.GetName(toggle));
        Assert.Contains(carousel.RealizedCards.Values, c => c.Album?.Name == "Invincible");
        Assert.Contains(carousel.RealizedCards.Values, c => c.Album?.Name == "Thriller");
        // Newest first, like the grid: the carousel starts on Invincible.
        Assert.Equal("Invincible", carousel.CenterAlbum?.Name);
        win.Close();
    }

    /// <summary>Discord 11-24: Singles &amp; EPs and Live Albums get the Albums tab's grid /
    /// carousel toggle, each tab keeping its own choice (the Albums tab stays a grid).</summary>
    [AvaloniaTheory]
    [InlineData("singles", "SinglesPanel")]
    [InlineData("live", "LivePanel")]
    public void ArtistPage_SinglesAndLiveTabs_SwitchToTheCarousel(string tab, string panelName)
    {
        EnsureAppStyles();
        Album Release(string name, int year, ReleaseType type)
        {
            var a = MakeAlbum(name, "Death Cab", year, type == ReleaseType.Single ? 1 : 10);
            foreach (var t in a.Tracks) { t.ReleaseType = type; t.ReleaseTypeFromTag = true; }
            return a;
        }
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            Release("Codes and Keys", 2011, ReleaseType.Album),
            Release("Old Single", 2003, ReleaseType.Single),
            Release("New Single", 2015, ReleaseType.Single),
            Release("Old Live", 2004, ReleaseType.Live),
            Release("New Live", 2016, ReleaseType.Live),
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var vm = new ArtistDetailViewModel("Death Cab", lib, player, settings: settings);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        vm.SelectTabCommand.Execute(tab);
        Dispatcher.UIThread.RunJobs();

        var panel = view.FindControl<StackPanel>(panelName)!;
        var carousel = panel.GetVisualDescendants().OfType<AlbumCarousel>().Single();
        object releases = tab == "singles" ? vm.SingleReleases : vm.LiveReleases;
        var grid = panel.GetVisualDescendants().OfType<ItemsControl>().First(i => ReferenceEquals(i.ItemsSource, releases));
        Assert.False(carousel.IsVisible);
        Assert.True(grid.IsVisible);

        var toggle = panel.GetVisualDescendants().OfType<Button>()
            .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Show as Carousel");
        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.True(carousel.IsVisible);
        Assert.False(grid.IsVisible);
        Assert.Equal("Show as Grid", Avalonia.Automation.AutomationProperties.GetName(toggle));
        var (newest, oldest) = tab == "singles" ? ("New Single", "Old Single") : ("New Live", "Old Live");
        Assert.Contains(carousel.RealizedCards.Values, c => c.Album?.Name == newest);
        Assert.Contains(carousel.RealizedCards.Values, c => c.Album?.Name == oldest);
        Assert.Equal(newest, carousel.CenterAlbum?.Name);
        Assert.Equal(tab == "singles" ? "carousel" : "grid", settings.ArtistSinglesViewMode);
        Assert.Equal(tab == "live" ? "carousel" : "grid", settings.ArtistLiveViewMode);
        Assert.Equal("grid", settings.ArtistAlbumsViewMode);

        // Every artist page shares the choice; the other tabs keep theirs.
        var other = new ArtistDetailViewModel("Death Cab", lib, player, settings: settings);
        Assert.Equal(tab == "singles", other.IsSinglesCarousel);
        Assert.Equal(tab == "live", other.IsLiveCarousel);
        Assert.False(other.IsAlbumsCarousel);

        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(carousel.IsVisible);
        Assert.True(grid.IsVisible);
        win.Close();
    }

    [AvaloniaFact]
    public void ArtistPage_MountsWithHeroPopularAndReleases()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            MakeAlbum("Phases", "Chase Atlantic", 2019, 12),
            MakeAlbum("Beauty in Death", "Chase Atlantic", 2021, 10),
            MakeAlbum("Single", "Chase Atlantic", 2022, 1),
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Chase Atlantic", texts);
        Assert.DoesNotContain("ARTIST", texts); // the old release-kind kicker stays gone (09-03)
        Assert.Contains("ALTERNATIVE", texts);  // genre kicker (09-13 redesign): dominant library genre
        Assert.Contains("Top Songs", texts);
        Assert.DoesNotContain("Popular", texts);
        Assert.Contains("Latest Release", texts);
        // GENRE fact from the library tag even before any web lookup.
        Assert.Contains("GENRE", texts);
        Assert.Contains("Alternative", texts);
        // Tab strip (09-13 redesign, design 4): Overview selected, the rest plain.
        foreach (var tab in new[] { "Overview", "Albums", "Singles & EPs", "Songs", "Similar Artists" })
            Assert.Contains(tab, texts);
        // Live Albums / Compilations tabs (GitHub #122) stay hidden for an artist with none.
        var tabs = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("page-tab") && b.IsVisible).ToList();
        Assert.Equal(5, tabs.Count);
        Assert.Single(tabs.Where(t => t.Classes.Contains("selected")));

        // Overview: two album tiles beside one single tile, five top-song rows with "…" only
        // (the "+" queue button is gone, 09-13).
        var tiles = view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("album-tile"));
        Assert.Equal(3, tiles);
        var rows = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("song-row")).ToList();
        Assert.Equal(ArtistDetailViewModel.MaxPopular, rows.Count);
        var menuButtons = rows.Sum(r => r.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("row-menu-btn")));
        Assert.Equal(ArtistDetailViewModel.MaxPopular, menuButtons);
        // Inner buttons per row: the "…" menu glyph and (09-14) the hover Play over the art.
        var playOverlays = rows.Sum(r => r.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("art-play-overlay")));
        Assert.Equal(ArtistDetailViewModel.MaxPopular, playOverlays);
        Assert.Equal(ArtistDetailViewModel.MaxPopular * 2, rows.Sum(r => r.GetVisualDescendants().OfType<Button>().Count()) ); // no other inner buttons
        // Year under the title (the reference's "2017"), not the album name.
        Assert.Contains("2019", texts);
        // Tile captions read "Album · 2019" (title case), the kicker form stays upper-case,
        // and end with the song count (GitHub #258).
        Assert.Contains("Album · 2019 · 12 songs", texts);
        Assert.Contains("Single · 2022 · 1 song", texts);

        // The playing song's row takes the accent fill: play the #1 song through the
        // player, the shared Track's IsNowPlaying flips, the row's class follows.
        Assert.DoesNotContain(rows, r => r.Classes.Contains("playing"));
        var top = vm.PopularSongs[0].Track;
        player.ReplaceQueueAndPlay(new List<Track> { top }, 0);
        Dispatcher.UIThread.RunJobs();
        var playingRows = rows.Where(r => r.Classes.Contains("playing")).ToList();
        Assert.Single(playingRows);
        Assert.Same(top, ((TopSongRow)playingRows[0].DataContext!).Track);

        // Rank numerals: plain theme text at full opacity, no podium tints (09-13 ask).
        var ranks = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("rank-num")).ToList();
        Assert.Equal(ArtistDetailViewModel.MaxPopular, ranks.Count);
        Assert.All(ranks, r =>
        {
            Assert.Equal(1.0, r.Opacity);
            Assert.False(r.Classes.Contains("gold") || r.Classes.Contains("silver") || r.Classes.Contains("bronze"));
        });

        // Singles & EPs tab: the Overview folds away, one tile in the full grid.
        vm.SelectTabCommand.Execute("singles");
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<StackPanel>("OverviewPanel")!.IsVisible);
        Assert.True(view.FindControl<StackPanel>("SinglesPanel")!.IsVisible);
        tiles = view.GetVisualDescendants().OfType<Button>()
            .Count(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible);
        Assert.Equal(1, tiles);

        // Songs tab: every song, ranked; the list is virtualized (09-24), so only the rows
        // inside the viewport are realized.
        vm.SelectTabCommand.Execute("songs");
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Assert.Equal(23, vm.AllSongs.Count);
        var songRows = view.FindControl<StackPanel>("SongsPanel")!
            .GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("song-row") && b.IsEffectivelyVisible);
        Assert.InRange(songRows, 1, 22);
    }

    // -- Song-row hover (09-14): the rows used to set Button.Background on :pointerover,
    //    which Fluent's template masks (HomeChartRowBehaviourTests pins that), so the
    //    hover showed Fluent's own untransitioned grey. Mirror Home's chart rows: the
    //    hover fill goes on PART_ContentPresenter with an 80 ms brush transition. --
    [AvaloniaFact]
    public void ArtistPage_SongRowHover_PaintsHomeCardHoverBackgroundOnPresenter()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).Add(MakeAlbum("Phases", "Chase Atlantic", 2019, 6));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        // The card brushes live in Styles.axaml's Dark/Light theme dictionaries: pin a variant.
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("song-row"));
        var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        var hover = (Avalonia.Media.IBrush)view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "HomeCardHoverBackground")!;
        var rest = (Avalonia.Media.IBrush)view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "HomeCardBackground")!;

        Assert.Same(rest, presenter.Background);
        var brushTransition = Assert.IsType<Avalonia.Animation.BrushTransition>(Assert.Single(presenter.Transitions!));
        Assert.Equal(ContentPresenter.BackgroundProperty, brushTransition.Property);
        Assert.Equal(TimeSpan.FromMilliseconds(80), brushTransition.Duration);

        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", true);
        // Transitions are frame-driven; the target value is what the style resolved to.
        presenter.Transitions = null;
        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", false);
        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", true);
        Assert.Same(hover, presenter.Background);

        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", false);
        Assert.Same(rest, presenter.Background);
    }

    // -- Track menu after "Remove from Favorites" (09-14 user report: "freezes the UI,
    //    can't right-click anymore, menu won't open"). Real click order: the item's
    //    command runs, the menu closes, FavoritesChanged posts ApplyLists, which
    //    rebuilds Top Favorites (the track left it) and tears down the owner row. --
    [AvaloniaFact]
    public void ArtistPage_TrackMenu_ReopensAfterUnfavoriteFromMenu()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 6);
        album.Tracks[0].IsFavorite = true;
        album.Tracks[1].IsFavorite = true;
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryStub());
        var albumsVm = new LibraryAlbumsViewModel(lib, player, sidebar, settings);
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player, albumsVm) { SelectedTab = "songs" };
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        static List<Button> Rows(ArtistDetailView v) =>
            v.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("song-row")).ToList();

        var favRow = Rows(view).First(r => r.DataContext is TopSongRow { Track.IsFavorite: true });
        var favTrack = ((TopSongRow)favRow.DataContext!).Track;
        favRow.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = favRow });
        var menu = favRow.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen, "menu should open on the first right-click");
        var unfavorite = menu.Items.OfType<MenuItem>().First(m => Equals(m.Header, "Remove from Favorites"));
        Assert.True(unfavorite.IsVisible);

        // Click: command first (DefaultMenuInteractionHandler.Click -> RaiseClick, then CloseMenu).
        unfavorite.Command!.Execute(unfavorite.CommandParameter);
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(favTrack.IsFavorite);
        Assert.DoesNotContain(vm.FavoriteSongs, r => ReferenceEquals(r.Track, favTrack));

        // Right-click a row that is still on the page: the menu must open again.
        var next = Rows(view).First(r => r.IsVisible && TopLevel.GetTopLevel(r) != null && !ReferenceEquals(r, favRow));
        next.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = next });
        Dispatcher.UIThread.RunJobs();
        Assert.Same(menu, next.ContextMenu);
        Assert.True(menu.IsOpen, "menu should re-open on a later right-click after an unfavorite from the menu");
    }

    // -- Songs tab: a heart that does not change the ranking must not re-stream the
    //    whole Songs list (FillAllSongs ran unguarded on every FavoritesChanged and
    //    LibraryUpdated, resetting every row: a visible stall and torn-down rows). --
    [AvaloniaFact]
    public void ArtistPage_SongsTab_FavoriteWithUnchangedRanking_DoesNotResetSongsList()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 40);
        album.Tracks[0].IsFavorite = true;
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player) { SelectedTab = "songs" };
        Dispatcher.UIThread.RunJobs(); // streaming slices
        Assert.Equal(40, vm.AllSongs.Count);
        var before = vm.AllSongs.ToList();
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.AllSongs.CollectionChanged += (_, e) => events.Add(e.Action);

        // A heart on a track that is not in the ranking's top: order unchanged.
        album.Tracks[5].IsFavorite = true;
        lib.NotifyFavoritesChanged(new[] { album.Tracks[5] });
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(events);
        Assert.Equal(before, vm.AllSongs);
        // ...while Top Favorites did pick the new heart up.
        Assert.Contains(vm.FavoriteSongs, r => ReferenceEquals(r.Track, album.Tracks[5]));
    }

    // -- A pending favorite save lands while the track menu is open on a Top Favorites
    //    row (toggle, right-click again quickly, save completes): ApplyLists rebuilds
    //    that list, the owner row leaves the tree, Avalonia closes the menu. The next
    //    right-click must still open it. --
    [AvaloniaFact]
    public void ArtistPage_TrackMenu_ReopensAfterOwnerRowWasRebuiltWhileOpen()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 6);
        album.Tracks[0].IsFavorite = true;
        album.Tracks[1].IsFavorite = true;
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistoryStub());
        var albumsVm = new LibraryAlbumsViewModel(lib, player, sidebar, settings);
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player, albumsVm) { SelectedTab = "songs" };
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        static List<Button> Rows(ArtistDetailView v) =>
            v.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("song-row")).ToList();

        var favRow = Rows(view).First(r => r.DataContext is TopSongRow { Track.IsFavorite: true });
        favRow.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = favRow });
        var menu = favRow.ContextMenu!;
        Assert.True(menu.IsOpen);

        // The other favourite's pending un-favorite lands now: Top Favorites is rebuilt.
        var other = album.Tracks.First(t => t.IsFavorite && !ReferenceEquals(t, ((TopSongRow)favRow.DataContext!).Track));
        other.IsFavorite = false;
        lib.NotifyFavoritesChanged(new[] { other });
        Dispatcher.UIThread.RunJobs();
        Assert.Null(TopLevel.GetTopLevel(favRow)); // the owner row was torn down
        Assert.False(menu.IsOpen);            // and Avalonia closed the menu with it

        var next = Rows(view).First(r => r.IsVisible && TopLevel.GetTopLevel(r) != null);
        next.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = next });
        Dispatcher.UIThread.RunJobs();
        Assert.Same(menu, next.ContextMenu);
        Assert.True(menu.IsOpen, "menu should re-open after its owner row was rebuilt while it was open");
    }

    // -- The playing row is filled in the accent colour, and the favourite heart paints
    //    itself in the app red by default: with a red accent the heart vanished into the
    //    fill (user, 09-14: "you can't see the heart icon when the accent color is red"). --
    [AvaloniaFact]
    public void ArtistPage_PlayingRow_HeartTakesTheRowForeground_NotItsOwnRed()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 4);
        album.Tracks[0].IsNowPlaying = true;
        album.Tracks[0].IsFavorite = true;
        album.Tracks[1].IsFavorite = true;
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var rowForeground = view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AccentForegroundBrush");
        var rows = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("song-row")).ToList();
        var playing = rows.First(r => r.Classes.Contains("playing"));
        var heart = playing.GetVisualDescendants().OfType<Noctis.Controls.HeartIcon>().First();

        Assert.Same(rowForeground, heart.OnBrush);
        Assert.Same(rowForeground, heart.OffBrush);
        Assert.NotSame(playing.Background, heart.OnBrush); // it was accent-on-accent

        // Rows that are not playing keep the app's red heart.
        var normal = rows.First(r => !r.Classes.Contains("playing"));
        Assert.NotSame(rowForeground, normal.GetVisualDescendants().OfType<Noctis.Controls.HeartIcon>().First().OnBrush);
    }

    // -- The playing row kept its accent fill on hover but not while held down: Fluent paints
    //    its own pressed chrome on the presenter, so double-clicking a track flashed the row
    //    white between the two presses (user, 09-14). Home never did, because its fill sits on
    //    an opaque body Border in front of the presenter. --
    [AvaloniaFact]
    public void ArtistPage_PlayingRow_KeepsItsAccentFill_WhileHoveredAndPressed()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 4);
        album.Tracks[0].IsNowPlaying = true;
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var accent = (Avalonia.Media.IBrush)view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AccentButtonBackground")!;
        var row = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("song-row") && b.Classes.Contains("playing"));
        var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        // Transitions are frame-driven; read the value each state resolves to, not a tween.
        presenter.Transitions = null;

        Assert.Same(accent, presenter.Background);

        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(accent, presenter.Background);

        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(accent, presenter.Background); // was Fluent's #66FFFFFF wash: the flash

        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pressed", false);
        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", false);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(accent, presenter.Background);
    }

    [AvaloniaFact]
    public void ArtistPage_RowSnapsToAccent_WhenItStartsPlaying_NoTweenThroughWhite()
    {
        // Double-clicking a track: the row under the pointer flips to .playing, and its
        // grey fill (#28FFFFFF hover / #18FFFFFF rest) TWEENED to the opaque accent - the
        // global Button brush transition and the presenter's hover tween both lerp ARGB, so
        // the mid values are a bright pale pink: the "white flicker" (user, 09-14). Home
        // snaps because its fill sits on a Border with no transition. Read live values, no
        // Transitions=null here: a tween start value would be the grey, not the accent.
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2019, 4);
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var accent = (Avalonia.Media.IBrush)view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AccentButtonBackground")!;
        var row = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("song-row"));
        var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        var pseudo = (Avalonia.Controls.IPseudoClasses)row.Classes;
        pseudo.Set(":pointerover", true);
        pseudo.Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        player.ReplaceQueueAndPlay(new List<Track> { ((TopSongRow)row.DataContext!).Track }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("playing", row.Classes);
        Assert.Same(accent, presenter.Background); // a tween start value here = the flicker
        Assert.Same(accent, row.Background);

        pseudo.Set(":pressed", false);
        pseudo.Set(":pointerover", false);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(accent, presenter.Background);
        Assert.Same(accent, row.Background);
    }

    private sealed class NoOpPlayHistoryStub : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void ArtistPage_RestoresScrollPositionAfterBackNavigation()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        // Enough releases to make the page taller than the window.
        ((List<Album>)lib.Albums).AddRange(Enumerable.Range(0, 30)
            .Select(i => MakeAlbum($"Album {i:00}", "Tall Artist", 1990 + i, 8)));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Tall Artist", lib, player);

        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 700, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "page must be scrollable for this test");
        scroll.Offset = new Vector(0, 900);
        Dispatcher.UIThread.RunJobs();
        var left = scroll.Offset.Y;
        Assert.True(left > 0);

        // Navigate away (view detaches, VM lives on in history) and come back to a
        // freshly built view for the same VM — the way the ContentControl does it.
        win.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(left, vm.SavedScrollOffset);

        var view2 = new ArtistDetailView { DataContext = vm };
        win.Content = view2;
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
        var scroll2 = view2.FindControl<ScrollViewer>("PageScrollViewer")!;
        Assert.Equal(left, scroll2.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void LyricsPage_MediaBackdropFollowsThePlayerSetting()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), persistence, lib);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var layer = view.FindControl<Grid>("LyricsMediaBackdrop");
        var backdrop = view.FindControl<VideoBackdrop>("MediaBackdrop");
        Assert.NotNull(layer);
        Assert.NotNull(backdrop);
        Assert.False(layer!.IsVisible);           // no clip chosen → layer hidden, decoder idle

        // A path that doesn't exist must show the layer (scrim) but never start a decoder.
        player.LyricsBackgroundMediaPath = Path.Combine(Path.GetTempPath(), "missing-clip.mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(layer.IsVisible);
        Assert.Equal(player.LyricsBackgroundMediaPath, backdrop!.Source);

        player.LyricsBackgroundMediaPath = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.False(layer.IsVisible);
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>A narrow page (wide sidebar, smaller window) squeezed Top Songs to a sliver
    /// beside the fixed Latest Release and About columns; About now stacks underneath, and
    /// a wide page keeps the three columns.</summary>
    [AvaloniaTheory]
    [InlineData(900, true)]
    [InlineData(1700, false)]
    public void Overview_StacksTheAboutCard_OnlyWhenNarrow(double width, bool stacked)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).Add(MakeAlbum("Phases", "Chase Atlantic", 2019, 12));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var view = new ArtistDetailView { DataContext = new ArtistDetailViewModel("Chase Atlantic", lib, player) };
        var win = new Window { Width = width, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.FindControl<Grid>("OverviewTopRow")!;
        var about = row.Children.OfType<Border>().Single(b => b.Classes.Contains("about-card"));
        Assert.Equal(stacked, row.Classes.Contains("stacked"));
        Assert.Equal(stacked ? 1 : 0, Grid.GetRow(about));
        Assert.Equal(stacked ? 0 : 4, Grid.GetColumn(about));
        // Top Songs keeps a usable width either way.
        var popular = row.Children.OfType<StackPanel>().First(p => Grid.GetColumn(p) == 0);
        Assert.True(popular.Bounds.Width >= 300, $"Top Songs column is {popular.Bounds.Width}px");
        win.Close();
    }

    /// <summary>
    /// GitHub #223: scrolled far down an artist's Albums tab (or the Overview), Edit Info or Find
    /// Lyrics on a tile, save, close: the page was back at the top. The save rebuilds the
    /// library's indexes, which hands the page new Album objects for the same releases.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("albums")]
    [InlineData("overview")]
    public void ARebuildWithTheSameReleases_KeepsTheScrollPosition(string tab)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < 40; i++)
            albums.Add(MakeAlbum($"Album {i:00}", "Chase Atlantic", 1980 + i, 10));
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player) { SelectedTab = tab };
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        for (var i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs(); // the tab grid streams in

        var sv = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        var bottom = sv.Extent.Height - sv.Viewport.Height;
        Assert.True(bottom > (tab == "albums" ? 1200 : 300), $"page only scrolls {bottom}px");
        var target = bottom - 50;
        sv.Offset = new Vector(0, target);
        Dispatcher.UIThread.RunJobs();
        var before = sv.Offset.Y;

        // What the metadata save publishes: every release again, as new objects.
        for (var i = 0; i < albums.Count; i++)
        {
            var a = albums[i];
            albums[i] = new Album { Id = a.Id, Name = a.Name, Artist = a.Artist, Year = a.Year, Tracks = a.Tracks, TrackCount = a.TrackCount };
        }
        lib.RaiseLibraryUpdated();
        for (var i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, sv.Offset.Y, 1);
        win.Close();
    }
}
