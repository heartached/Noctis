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
        var tabs = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("page-tab")).ToList();
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
        // Tile captions read "Album · 2019" (title case), the kicker form stays upper-case.
        Assert.Contains("Album · 2019", texts);
        Assert.Contains("Single · 2022", texts);

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
}
