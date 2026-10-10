using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-09 "apply it everywhere": every track and album right-click menu on Home, the
/// Artist page, Favorites, More-by-artist, the album page's related tiles and the Albums
/// page's artist songs uses the v2 menu. Each test right-clicks the real view and checks:
/// the v2 class, every command the old menu had (with the row's parameter), the dots
/// button opening the same menu, and a rebuild after a live language switch.
/// </summary>
public class ContextMenuV2EverywhereTests
{
    // ── Shared helpers ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Album MakeAlbum(string name, string artist, int trackCount, bool favorite = false)
    {
        var id = Track.ComputeAlbumId(artist, name);
        var album = new Album { Id = id, Name = name, Artist = artist, Year = 2015, Tracks = new List<Track>() };
        for (var i = 1; i <= trackCount; i++)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = 2015, Duration = TimeSpan.FromMinutes(3),
                PlayCount = 10 - i, IsFavorite = favorite, FilePath = $"C:/m/{name}-{i}.flac",
            });
        album.TrackCount = trackCount;
        return album;
    }

    /// <summary>Every (command, parameter) a user can trigger: visible rows (recursively) and
    /// the visible buttons in v2 panels (tiles, stars).</summary>
    private static List<(ICommand Command, object? Parameter)> Actions(ItemCollection items)
    {
        var found = new List<(ICommand, object?)>();
        foreach (var o in items)
        {
            if (o is not MenuItem { IsVisible: true } item) continue;
            if (item.Command != null) found.Add((item.Command, item.CommandParameter));
            found.AddRange(Actions(item.Items));
            if (item.Header is Control panel)
                foreach (var b in panel.GetLogicalDescendants().OfType<Button>().Where(x => x.IsVisible && x.Command != null))
                    found.Add((b.Command!, b.CommandParameter));
        }
        return found;
    }

    private static HashSet<ICommand> Reachable(ContextMenu menu) => Actions(menu.Items).Select(a => a.Command).ToHashSet();

    private static void AssertV2(ContextMenu menu)
    {
        Assert.Contains(MenuV2.MenuClass, menu.Classes);
        // No title header (owner 10-09): the quick tiles are the first row.
        var first = Assert.IsType<MenuItem>(menu.Items[0]);
        Assert.Equal(4, ((Control)first.Header!).GetLogicalDescendants().OfType<Button>().Count(b => b.Classes.Contains("mv2-tile")));
        var remove = menu.Items.OfType<MenuItem>().Last();
        Assert.Contains("danger", remove.Classes);
        var visible = menu.Items.OfType<Control>().Where(c => c.IsVisible).ToList();
        Assert.IsNotType<Separator>(visible.Last());
        for (var i = 1; i < visible.Count; i++)
            Assert.False(visible[i] is Separator && visible[i - 1] is Separator, $"two separators in a row at {i}");
    }

    private static ContextMenu RightClick(Control owner)
    {
        owner.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = owner });
        Dispatcher.UIThread.RunJobs();
        var menu = owner.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen, "the menu should open on right-click");
        return menu;
    }

    private static ContextMenu ClickDots(Button tile)
    {
        var dots = tile.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tile-more"));
        dots.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var menu = tile.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen, "the dots button should open the tile's menu");
        return menu;
    }

    private static List<Button> Tiles(Control view) =>
        view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();

    /// <summary>Asserts every visible row/tile that acts on the item carries it as parameter
    /// (Don't Scrobble's static toggle, View Artist's name and plugin entries aside).</summary>
    private static void AssertParameters(ContextMenu menu, object expected, params ICommand[] except)
    {
        foreach (var (command, parameter) in Actions(menu.Items))
        {
            if (command == ScrobbleMenu.ToggleCommand || except.Contains(command)) continue;
            if (parameter is RateRequest r) { Assert.Same(expected, r.Track); continue; }
            Assert.Same(expected, parameter);
        }
    }

    /// <summary>Opens a menu in English, switches to Spanish and opens it again: the second
    /// one must be a fresh v2 build in Spanish.</summary>
    private static void AssertRebuildsAfterLanguageSwitch(Func<ContextMenu> open)
    {
        var en = open();
        en.Close();
        Loc.Instance.SetCulture("es");
        try
        {
            var es = open();
            Assert.NotSame(en, es);
            AssertV2(es);
            var labels = es.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile"))
                .Select(b => b.GetLogicalDescendants().OfType<TextBlock>().Single().Text).ToList();
            Assert.Contains(Loc.T("LibraryAlbums.AddQueue"), labels);
            Assert.DoesNotContain("Add to Queue", labels);
            es.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en");
        }
    }

    // ── Home ──

    private static async Task<(HomeViewModel Vm, HomeView View, Window Win, Track Top, Album Album)> MountHome()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Un Verano Sin Ti", "Bad Bunny", 2);
        var top = album.Tracks[0];
        lib.TrackList.AddRange(album.Tracks);
        ((List<Album>)lib.Albums).Add(album);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(top);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        // Awaited, never blocked on: RefreshAsync resumes on the UI thread after its
        // Task.Run (HomeViewModel.RefreshAsync), so .GetResult() here deadlocks the runner.
        await vm.RefreshAsync();
        if (vm.RecentlyPlayedAlbums.Count == 0) vm.RecentlyPlayedAlbums.Add(album);
        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, view, win, top, vm.RecentlyPlayedAlbums[0]);
    }

    private static Button HomeChartRow(HomeView view, Track track) =>
        view.GetVisualDescendants().OfType<Button>()
            .First(r => r.Classes.Contains("home-chart-row") && r.DataContext is TopSongRow { IsLastPlayed: false } row && row.Track == track);

    [AvaloniaFact]
    public async Task Home_ChartRow_OpensV2TrackMenu_WithEveryOldCommand()
    {
        var (vm, view, win, top, _) = await MountHome();
        var menu = RightClick(HomeChartRow(view, top));
        AssertV2(menu);

        // HomeView.axaml.cs OpenTrackMenu: the old classic menu's commands, all still reachable.
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayTopSongCommand, vm.ShuffleTopSongsCommand, vm.PlayNextCommand, vm.AddToQueueCommand,
                     vm.AddTrackToNewPlaylistCommand, vm.ToggleTrackFavoriteCommand, vm.OpenTrackMetadataCommand,
                     vm.SearchLyricsTrackCommand, vm.ShowInExplorerTrackCommand, vm.RemoveTrackFromLibraryCommand,
                     vm.ConvertTrackCommand, vm.ScanTrackReplayGainCommand, vm.StartRadioCommand, vm.SnoozeForMonthCommand,
                     vm.ViewAlbumFromTrackCommand, vm.ViewArtistCommand,
                     SpectrogramLauncher.OpenCommand, LyricsBackgroundOverrides.ChooseForTrackCommand,
                 })
            Assert.Contains(command, reachable);
        // Shuffle never carried a parameter; View Artist carries the name.
        AssertParameters(menu, top, vm.ShuffleTopSongsCommand, vm.ViewArtistCommand);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public async Task Home_AlbumTile_RightClickAndDots_OpenV2AlbumMenu_WithEveryOldCommand()
    {
        var (vm, view, win, _, album) = await MountHome();
        var tile = Tiles(view).First(t => t.DataContext == album);
        var menu = RightClick(tile);
        AssertV2(menu);
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayAlbumCommand, vm.ShuffleAlbumCommand, vm.PlayNextAlbumCommand, vm.AddAlbumToQueueCommand,
                     vm.AddAlbumToNewPlaylistCommand, vm.ToggleAlbumFavoritesCommand, vm.OpenMetadataCommand,
                     vm.ShowInExplorerAlbumCommand, vm.RemoveFromLibraryCommand, vm.ConvertAlbumCommand,
                     vm.ScanAlbumReplayGainCommand, vm.SearchLyricsAlbumCommand, LyricsBackgroundOverrides.ChooseForAlbumCommand,
                     vm.SnoozeAlbumForMonthCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, album);
        menu.Close();

        // Dots: the same menu, bound again, with the (empty) selection pushed.
        vm.CtrlSelectedAlbums = new List<Album> { new() { Name = "stale" } };
        var viaDots = ClickDots(tile);
        Assert.Same(menu, viaDots);
        Assert.Empty(vm.CtrlSelectedAlbums);
        viaDots.Close();
        win.Close();
    }

    [AvaloniaFact]
    public async Task Home_Menus_RebuildAfterLanguageSwitch()
    {
        var (_, view, win, top, album) = await MountHome();
        AssertRebuildsAfterLanguageSwitch(() => RightClick(HomeChartRow(view, top)));
        AssertRebuildsAfterLanguageSwitch(() => RightClick(Tiles(view).First(t => t.DataContext == album)));
        // The dots button follows the switch too (it used to reopen whatever the tile held).
        AssertRebuildsAfterLanguageSwitch(() => ClickDots(Tiles(view).First(t => t.DataContext == album)));
        win.Close();
    }

    // ── Artist page ──

    private static (ArtistDetailViewModel Vm, LibraryAlbumsViewModel AlbumsVm, ArtistDetailView View, Window Win) MountArtist(string tab)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 12); // long enough to file under Albums, not Singles & EPs
        ((List<Album>)lib.Albums).Add(album);
        lib.TrackList.AddRange(album.Tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var albumsVm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player, albumsVm) { SelectedTab = tab };
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, albumsVm, view, win);
    }

    private static Button ArtistSongRow(ArtistDetailView view) =>
        view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("song-row") && b.IsEffectivelyVisible);

    [AvaloniaFact]
    public void Artist_SongRow_RightClickAndDots_OpenV2TrackMenu_WithEveryOldCommand()
    {
        var (vm, albumsVm, view, win) = MountArtist("songs");
        var row = ArtistSongRow(view);
        var track = ((TopSongRow)row.DataContext!).Track;
        var menu = RightClick(row);
        AssertV2(menu);
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     vm.PlaySongCommand, vm.ShufflePopularCommand, albumsVm.PlayNextTrackCommand, albumsVm.AddTrackToQueueCommand,
                     albumsVm.AddTrackToNewPlaylistCommand, albumsVm.ToggleTrackFavoriteCommand, albumsVm.OpenTrackMetadataCommand,
                     vm.SearchLyricsCommand, albumsVm.ShowInExplorerTrackCommand, albumsVm.RemoveTrackFromLibraryCommand,
                     SpectrogramLauncher.OpenCommand, LyricsBackgroundOverrides.ChooseForTrackCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, track, vm.ShufflePopularCommand);
        menu.Close();

        // The row's "…" glyph opens the same menu under itself.
        var glyph = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row-menu-btn"));
        glyph.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(menu, glyph.ContextMenu);
        Assert.True(menu.IsOpen);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void Artist_AlbumTile_RightClickAndDots_OpenV2AlbumMenu_WithEveryOldCommand()
    {
        var (_, albumsVm, view, win) = MountArtist("albums");
        var tile = Tiles(view).First(t => t.IsEffectivelyVisible);
        var album = (Album)tile.DataContext!;
        albumsVm.CtrlSelectedAlbums = new List<Album> { new() { Name = "stale" } };
        var menu = RightClick(tile);
        AssertV2(menu);
        // ArtistDetailView.axaml's old XAML menu: Play, Shuffle, Play Next, Add to Queue, Add to
        // Playlist, Favorites / Remove from Favorites, Metadata, Don't Scrobble, Show Folder, Remove.
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     albumsVm.PlayAlbumCommand, albumsVm.ShuffleAlbumCommand, albumsVm.PlayNextCommand, albumsVm.AddToQueueCommand,
                     albumsVm.AddToNewPlaylistCommand, albumsVm.ToggleAlbumFavoritesCommand, albumsVm.OpenMetadataCommand,
                     albumsVm.ShowInExplorerCommand, albumsVm.RemoveFromLibraryCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, album);
        Assert.Empty(albumsVm.CtrlSelectedAlbums); // single-album menu: stale selection cleared
        menu.Close();

        albumsVm.CtrlSelectedAlbums = new List<Album> { new() { Name = "stale" } };
        Assert.Same(menu, ClickDots(tile));
        Assert.Empty(albumsVm.CtrlSelectedAlbums);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void Artist_Menus_RebuildAfterLanguageSwitch()
    {
        var (_, _, view, win) = MountArtist("songs");
        AssertRebuildsAfterLanguageSwitch(() => RightClick(ArtistSongRow(view)));
        win.Close();
        (_, _, view, win) = MountArtist("albums");
        AssertRebuildsAfterLanguageSwitch(() => RightClick(Tiles(view).First(t => t.IsEffectivelyVisible)));
        win.Close();
    }

    // ── Albums page: the artist Songs pills' track menu ──

    [AvaloniaFact]
    public void AlbumsPage_ArtistSongPill_OpensV2TrackMenu_WithEveryOldCommand()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var track = MakeAlbum("25", "Adele", 1).Tracks[0];
        // The pill template lives in the artist-filtered header; stand in a pill-shaped owner.
        var pill = new Button { DataContext = new TopSongRow { Track = track, Rank = 1 } };
        var root = new DockPanel();
        DockPanel.SetDock(pill, Dock.Top);
        root.Children.Add(pill);
        root.Children.Add(view);
        var win = new Window { Width = 1400, Height = 900, Content = root };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        pill.ContextRequested += (s, e) => typeof(LibraryAlbumsView)
            .GetMethod("OnArtistSongContextRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, new object?[] { s, e });

        Func<ContextMenu> open = () => RightClick(pill);
        var menu = open();
        AssertV2(menu);
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayArtistSongCommand, vm.ShuffleArtistSongsCommand, vm.PlayNextTrackCommand, vm.AddTrackToQueueCommand,
                     vm.AddTrackToNewPlaylistCommand, vm.ToggleTrackFavoriteCommand, vm.OpenTrackMetadataCommand,
                     vm.SearchLyricsTrackCommand, vm.ShowInExplorerTrackCommand, vm.RemoveTrackFromLibraryCommand,
                     SpectrogramLauncher.OpenCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, track, vm.ShuffleArtistSongsCommand);
        menu.Close();
        AssertRebuildsAfterLanguageSwitch(open);
        win.Close();
    }

    // ── Favorites ──

    private static (FavoritesViewModel Vm, FavoritesView View, Window Win, Track Song, Album Album) MountFavorites()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var album = MakeAlbum("Phases", "Chase Atlantic", 2, favorite: true);
        var loose = MakeAlbum("Beauty in Death", "Chase Atlantic", 3);
        var song = loose.Tracks[0];
        song.IsFavorite = true;
        ((List<Album>)lib.Albums).AddRange(new[] { album, loose });
        lib.TrackList.AddRange(album.Tracks.Concat(loose.Tracks));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.Refresh();
        var view = new FavoritesView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, view, win, song, album);
    }

    private static Button FavoriteTile(FavoritesView view, Func<FavoriteItem, bool> match) =>
        Tiles(view).First(t => t.DataContext is FavoriteItem fi && match(fi));

    /// <summary>The FavoritesViewModel command each reachable row ends up running, after
    /// asserting the row's parameter maps back to the item the menu was opened on.</summary>
    private static HashSet<ICommand> FavoriteTargets(ContextMenu menu, FavoriteItem item, object rowParameter)
    {
        var targets = new HashSet<ICommand>();
        foreach (var (command, parameter) in Actions(menu.Items))
        {
            if (command is FavoriteItemCommand fic)
            {
                Assert.Same(item, fic.Resolve(parameter));
                Assert.True(fic.CanExecute(parameter), "a favourites row should be enabled");
                targets.Add(fic.Inner);
            }
            else
            {
                targets.Add(command);
                if (command != ScrobbleMenu.ToggleCommand && parameter is not string)
                    Assert.Same(rowParameter, parameter);
            }
        }
        return targets;
    }

    [AvaloniaFact]
    public void Favorites_Song_OpensV2TrackMenu_ReachingEveryOldItemCommand()
    {
        var (vm, view, win, song, _) = MountFavorites();
        var tile = FavoriteTile(view, fi => fi.Track == song);
        var item = (FavoriteItem)tile.DataContext!;
        vm.CtrlSelectedItems = new List<FavoriteItem> { new() { Track = new Track { Title = "stale" } } };
        var menu = RightClick(tile);
        AssertV2(menu);
        Assert.Empty(vm.CtrlSelectedItems); // the (empty) live selection was pushed

        // FavoritesView.axaml's old XAML menu: Play, Shuffle, Play Next, Add to Queue, View Album,
        // Add to Playlist, Remove from Favorites, Metadata, Tools ▸ (Convert, Spectrogram,
        // ReplayGain), Show Folder, Remove from Library — all on the FavoriteItem commands.
        var targets = FavoriteTargets(menu, item, song);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayItemCommand, vm.ShuffleItemCommand, vm.PlayNextItemCommand, vm.AddItemToQueueCommand,
                     vm.ViewItemAlbumCommand, vm.AddItemToNewPlaylistCommand, vm.RemoveItemFavoriteCommand,
                     vm.OpenItemMetadataCommand, vm.ConvertItemCommand, SpectrogramLauncher.OpenCommand,
                     vm.ScanItemReplayGainCommand, vm.ShowItemInExplorerCommand, vm.RemoveItemFromLibraryCommand,
                 })
            Assert.Contains(command, targets);
        // A favourite song shows "Remove from Favorites", not "Favorites".
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => i.IsVisible && Equals(i.Header, Loc.T("LibraryAlbums.Favorites")));

        // Running a row reaches the view model with the FavoriteItem: View Album opens the song's album.
        var opened = new List<Track>();
        vm.ViewAlbumRequested += (_, t) => opened.Add(t);
        var (viewAlbum, p) = Actions(menu.Items).First(a => a.Command is FavoriteItemCommand f && f.Inner == vm.ViewItemAlbumCommand);
        viewAlbum.Execute(p);
        Assert.Equal(new[] { song }, opened);
        menu.Close();

        Assert.Same(menu, ClickDots(tile));
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void Favorites_Album_OpensV2AlbumMenu_ReachingEveryOldItemCommand()
    {
        var (vm, view, win, _, album) = MountFavorites();
        var tile = FavoriteTile(view, fi => fi.Album == album);
        var item = (FavoriteItem)tile.DataContext!;
        var menu = RightClick(tile);
        AssertV2(menu);
        var targets = FavoriteTargets(menu, item, album);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayItemCommand, vm.ShuffleItemCommand, vm.PlayNextItemCommand, vm.AddItemToQueueCommand,
                     vm.ViewItemAlbumCommand, vm.AddItemToNewPlaylistCommand, vm.RemoveItemFavoriteCommand,
                     vm.OpenItemMetadataCommand, vm.ConvertItemCommand, vm.ScanItemReplayGainCommand,
                     vm.ShowItemInExplorerCommand, vm.RemoveItemFromLibraryCommand,
                 })
            Assert.Contains(command, targets);
        // The old menu's Spectrogram did nothing on an album (SpectrogramLauncher maps a
        // FavoriteItem to its Track, null for an album): the album menu has none.
        Assert.DoesNotContain(SpectrogramLauncher.OpenCommand, targets);

        // View Album opens the album itself, as the old entry did.
        var opened = new List<Album>();
        vm.AlbumOpened += (_, a) => opened.Add(a);
        var (viewAlbum, p) = Actions(menu.Items).First(a => a.Command is FavoriteItemCommand f && f.Inner == vm.ViewItemAlbumCommand);
        viewAlbum.Execute(p);
        Assert.Equal(new[] { album }, opened);
        menu.Close();

        // Switching between a song and an album tile swaps menus without a stale owner.
        var songTile = FavoriteTile(view, fi => !fi.IsAlbum);
        var songMenu = RightClick(songTile);
        Assert.NotSame(menu, songMenu);
        Assert.Null(tile.ContextMenu);
        songMenu.Close();
        Assert.Same(menu, RightClick(tile));
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void Favorites_Menus_RebuildAfterLanguageSwitch()
    {
        var (_, view, win, song, album) = MountFavorites();
        AssertRebuildsAfterLanguageSwitch(() => RightClick(FavoriteTile(view, fi => fi.Track == song)));
        AssertRebuildsAfterLanguageSwitch(() => RightClick(FavoriteTile(view, fi => fi.Album == album)));
        win.Close();
    }

    // ── More by artist ──

    [AvaloniaFact]
    public void MoreByArtist_Tile_RightClickAndDots_OpenV2AlbumMenu_WithEveryOldCommand()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var albumsVm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var albums = new[] { MakeAlbum("25", "Adele", 2), MakeAlbum("21", "Adele", 2) };
        var vm = new MoreByArtistViewModel("Adele", albums, player, albumsVm);
        var view = new MoreByArtistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var tile = Tiles(view).First();
        var album = (Album)tile.DataContext!;
        albumsVm.CtrlSelectedAlbums = new List<Album> { new() { Name = "stale" } };
        var menu = RightClick(tile);
        AssertV2(menu);
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     albumsVm.PlayAlbumCommand, albumsVm.ShuffleAlbumCommand, albumsVm.PlayNextCommand, albumsVm.AddToQueueCommand,
                     albumsVm.AddToNewPlaylistCommand, albumsVm.ToggleAlbumFavoritesCommand, albumsVm.OpenMetadataCommand,
                     albumsVm.ShowInExplorerCommand, albumsVm.RemoveFromLibraryCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, album);
        Assert.Empty(albumsVm.CtrlSelectedAlbums);
        menu.Close();

        // Another tile: the shared menu moves over, bound to that album.
        var other = Tiles(view).First(t => t.DataContext != album);
        Assert.Same(menu, ClickDots(other));
        Assert.Null(tile.ContextMenu);
        AssertParameters(menu, other.DataContext!);
        menu.Close();

        AssertRebuildsAfterLanguageSwitch(() => RightClick(tile));
        win.Close();
    }

    // ── Album page: Other Versions / More By tiles ──

    [AvaloniaFact]
    public void AlbumPage_RelatedTile_RightClickAndDots_OpenV2AlbumMenu_WithEveryOldCommand()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var current = MakeAlbum("25", "Adele", 2);
        var other = MakeAlbum("21", "Adele", 2);
        ((List<Album>)lib.Albums).AddRange(new[] { current, other });
        lib.ArtistAlbums.AddRange(new[] { current, other });
        lib.TrackList.AddRange(current.Tracks.Concat(other.Tracks));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(current, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var tile = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("related-album-tile"));
        Assert.Same(other, tile.DataContext);
        var menu = RightClick(tile);
        AssertV2(menu);
        var reachable = Reachable(menu);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayRelatedAlbumCommand, vm.ShuffleRelatedAlbumCommand, vm.PlayNextRelatedAlbumCommand,
                     vm.AddRelatedAlbumToQueueCommand, vm.AddRelatedAlbumToNewPlaylistCommand, vm.ToggleRelatedAlbumFavoritesCommand,
                     vm.OpenRelatedAlbumMetadataCommand, vm.ConvertRelatedAlbumCommand, vm.ScanRelatedAlbumReplayGainCommand,
                     vm.ShowRelatedAlbumInExplorerCommand, vm.RemoveRelatedAlbumFromLibraryCommand,
                 })
            Assert.Contains(command, reachable);
        AssertParameters(menu, other);
        menu.Close();

        Assert.Same(menu, ClickDots(tile));
        menu.Close();
        AssertRebuildsAfterLanguageSwitch(() => RightClick(tile));
        win.Close();
    }
}
