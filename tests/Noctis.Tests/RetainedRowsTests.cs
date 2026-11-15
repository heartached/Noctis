using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-14 "lag when I'm scrolling": Avalonia's VirtualizingStackPanel clears a recycled row
/// (its cleared Content drops the item template's controls) and takes it out of the tree
/// (dropping its styles), so every row scrolled in was built and styled from scratch: on the
/// Songs page ~1.3 MB and ~51 controls a 40 px wheel step (PageScrollBench). The lists use
/// <see cref="RetainingVirtualizingStackPanel"/>, which parks rows in the tree and hands them
/// the next item. These pin that rows are reused, and that a reused row shows its own item,
/// stripe and selection.
/// </summary>
public class RetainedRowsTests
{
    private readonly ITestOutputHelper _o;
    public RetainedRowsTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump();
    }

    private static (LibrarySongsViewModel Vm, LibrarySongsView View, Window Window) MountSongs(int count)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < count; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i:0000}", Artist = $"Artist {i % 37}", Album = $"Album {i / 12}",
                FilePath = TestPaths.Primary("t", $"{i:0000}.mp3"), Duration = TimeSpan.FromMinutes(3),
                Rating = i % 3 == 0 ? 4 : 0, IsFavorite = i % 5 == 0, TrackNumber = i % 12 + 1,
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        vm.IsActive = true;
        return (vm, view, window);
    }

    private static ScrollViewer Scroller(ListBox list) => list.FindDescendantOfType<ScrollViewer>()!;

    [AvaloniaFact]
    public async Task Songs_UsesTheRetainingPanel()
    {
        var (vm, view, window) = MountSongs(50);
        await PumpUntil(() => vm.FilteredTracks.Count == 50);
        Assert.IsType<RetainingVirtualizingStackPanel>(view.TrackList.ItemsPanelRoot);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Songs_ScrolledRows_ShowTheirOwnSongStripeAndSelection()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var list = view.TrackList;
        list.Selection.Select(5);
        Pump();
        Assert.True(view.TryFindResource("TrackListStripeBrush", view.ActualThemeVariant, out var stripeObj));
        var stripe = Assert.IsAssignableFrom<IBrush>(stripeObj);
        var sv = Scroller(list);

        foreach (var y in new[] { 0.0, 1200, 1240, 1290, 5000, 5046, 9000, 300, 0, 23 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            var rows = list.GetRealizedContainers()!.ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                Assert.True(row.IsVisible, $"a parked row was reported realized at offset {y}");
                var index = list.IndexFromContainer(row);
                var track = vm.FilteredTracks[index];
                var item = Assert.IsType<ListBoxItem>(row);
                Assert.Same(track, item.Content);
                Assert.Same(track, item.DataContext);
                var body = item.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-body"));
                Assert.Same(track, body.DataContext);
                var title = body.GetVisualDescendants().OfType<HighlightTextBlock>().First(t => t.Classes.Contains("one-line-explicit-title"));
                Assert.Equal(track.TitleDisplay, title.DisplayText);
                var rating = body.GetVisualDescendants().OfType<RatingStars>().First();
                Assert.Equal(track.Rating, rating.Value);
                Assert.Equal(track.Rating > 0, rating.Classes.Contains("rated"));
                // :nth-child(2n) is 1-based: the second, fourth ... rows carry the stripe.
                if (index % 2 == 1)
                    Assert.Equal(stripe, body.Background);
                else
                    Assert.NotEqual(stripe, body.Background);
                Assert.Equal(index == 5, item.IsSelected);
            }
        }

        Assert.Equal(new[] { 5 }, list.Selection.SelectedIndexes.ToArray());
        window.Close();
    }

    /// <summary>A parked row whose next song is rated (or not) snaps to its stars' look, as a
    /// new row would, instead of fading across from the last song's (80 ms Opacity transition).</summary>
    [AvaloniaFact]
    public async Task Songs_ReusedRow_DoesNotFadeFromTheLastSongsLook()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var sv = Scroller(view.TrackList);
        foreach (var y in new[] { 400.0, 800, 1600, 3000 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in view.TrackList.GetRealizedContainers()!)
            {
                var track = vm.FilteredTracks[view.TrackList.IndexFromContainer(row)];
                var stars = row.GetVisualDescendants().OfType<RatingStars>().First();
                Assert.Equal(track.Rating > 0 ? 0.7 : 0.0, stars.Opacity, 3);
                Assert.NotNull(stars.Transitions); // given back after the rebind
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Songs_WheelGlide_ReusesRows_InsteadOfRebuildingThem()
    {
        var (vm, view, window) = MountSongs(3000);
        await PumpUntil(() => vm.FilteredTracks.Count == 3000);
        var list = view.TrackList;
        var sv = Scroller(list);
        sv.Offset = new Vector(0, 40);
        Pump();

        var roots = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var containers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        const int frames = 120;
        for (var i = 2; i <= frames + 1; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            sv.Offset = new Vector(0, i * 40.0);
            Pump(1);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                containers.Add(row);
                if (row.Presenter?.Child is { } child) roots.Add(child);
            }
        }
        var kbPerFrame = bytes / 1024.0 / frames;
        _o.WriteLine($"{containers.Count} containers, {roots.Count} template roots, {kbPerFrame:0} KB/frame");
        // Before (Avalonia's panel): a new template per row scrolled in, ~100 roots here and
        // ~1.3 MB a frame in the app; now every row keeps its template.
        Assert.Equal(containers.Count, roots.Count);
        Assert.True(kbPerFrame < 400, $"{kbPerFrame:0} KB per 40 px wheel step");
        window.Close();
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>A grid row scrolled off is re-used for other albums: its tiles re-bind (TileRow)
    /// rather than being built again, each shows its own album, and the Ctrl-selection plate
    /// follows the album, not the tile.</summary>
    [AvaloniaFact]
    public void AlbumsGrid_ReusedRows_KeepTheirTiles_AndShowTheirOwnAlbums()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Pump();
        var cols = Math.Max(1, vm.GridColumns);
        for (var r = 0; r < 60; r++)
            vm.FilteredAlbumRows.Add(new AlbumRow
            {
                Albums = Enumerable.Range(0, cols).Select(c => new Album
                {
                    Id = Guid.NewGuid(), Name = $"Album {r}-{c}", Artist = "Artist", Year = 2020, Tracks = new List<Track>(),
                }).ToList(),
            });
        Pump();
        var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "AlbumListBox");
        Assert.IsType<RetainingVirtualizingStackPanel>(list.ItemsPanelRoot);

        // One album Ctrl-selected (the view's own set, as a Ctrl+click leaves it).
        var picked = ((AlbumRow)vm.FilteredAlbumRows[0]).Albums[1];
        var selected = (HashSet<Album>)typeof(LibraryAlbumsView)
            .GetField("_selectedAlbums", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        selected.Add(picked);
        foreach (var t in view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")))
            MultiSelectHelper.SyncAlbumTileVisual(t, selected);

        var sv = list.FindDescendantOfType<ScrollViewer>()!;
        var tiles = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var y in new[] { 0.0, 600, 1300, 4000, 9000, 2500, 0 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in list.GetRealizedContainers()!)
            {
                var albumRow = Assert.IsType<AlbumRow>(((ListBoxItem)row).Content);
                var rowTiles = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
                Assert.Equal(albumRow.Albums.Count, rowTiles.Count);
                for (var c = 0; c < rowTiles.Count; c++)
                {
                    var album = albumRow.Albums[c];
                    Assert.Same(album, rowTiles[c].DataContext);
                    var title = rowTiles[c].GetVisualDescendants().OfType<HighlightTextBlock>().First();
                    Assert.Equal(album.Name, title.DisplayText);
                    Assert.Equal(ReferenceEquals(album, picked), rowTiles[c].Classes.Contains("ctrl-selected"));
                    var albumPlay = rowTiles[c].GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tile-play"));
                    Assert.True(albumPlay.Command != null && albumPlay.IsEffectivelyEnabled, $"album tile {c} at {y}: Play has no command");
                    tiles.Add(rowTiles[c]);
                }
            }
        }
        // A few rows' worth of tiles for 60 rows scrolled through: they were re-bound, not rebuilt.
        Assert.True(tiles.Count <= cols * 8, $"{tiles.Count} tiles for {cols} columns");
        window.Close();
    }

    /// <summary>Favorites rows re-used for other favorites: each tile shows its own item and its
    /// hover Play button stays usable.</summary>
    [AvaloniaFact]
    public async Task FavoritesGrid_ReusedRows_ShowTheirOwnItems_WithAWorkingPlayButton()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 400; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = $"Single {i}", AlbumArtist = "Artist",
                FilePath = TestPaths.Primary("f", $"{i}.mp3"), Duration = TimeSpan.FromMinutes(3), IsFavorite = true, AlbumId = Guid.NewGuid(),
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.Refresh();
        var view = new FavoritesView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        var list = view.FindControl<ListBox>("FavoritesList")!;
        await PumpUntil(() => list.GetRealizedContainers()?.Any() == true);
        var sv = list.FindDescendantOfType<ScrollViewer>()!;
        foreach (var y in new[] { 0.0, 700, 2000, 9000, 300, 0 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in list.GetRealizedContainers()!)
            {
                var itemsRow = Assert.IsType<FavoriteItemRow>(((ListBoxItem)row).Content);
                var tiles = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
                Assert.Equal(itemsRow.Items.Count, tiles.Count);
                for (var c = 0; c < tiles.Count; c++)
                {
                    Assert.Same(itemsRow.Items[c], tiles[c].DataContext);
                    var play = tiles[c].GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tile-play"));
                    Assert.True(play.IsEffectivelyEnabled, $"tile {c} of a row at {y}: Play disabled (command {play.Command})");
                }
            }
        }
        window.Close();
    }

    /// <summary>Artists rows re-used for other artists: each portrait shows its own artist and
    /// still opens it.</summary>
    [AvaloniaFact]
    public void ArtistsGrid_ReusedRows_ShowTheirOwnArtists()
    {
        EnsureAppStyles();
        var vm = new LibraryArtistsViewModel(new FakeLibraryService());
        var rows = new List<ArtistRow>();
        for (var r = 0; r < 60; r++)
        {
            var row = new ArtistRow();
            for (var i = 0; i < 7; i++)
                row.Artists.Add(new Artist { Id = Guid.NewGuid(), Name = $"Artist {r * 7 + i}", IsFavorite = i == 3, CountsLine = $"{r} albums" });
            rows.Add(row);
        }
        vm.ArtistRows.ReplaceAll(rows);
        var view = new LibraryArtistsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Pump();
        var list = view.FindControl<ListBox>("ArtistListBox")!;
        Assert.IsType<RetainingVirtualizingStackPanel>(list.ItemsPanelRoot);
        var sv = list.FindDescendantOfType<ScrollViewer>()!;
        var tiles = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var y in new[] { 0.0, 500, 2000, 6000, 1000, 0 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in list.GetRealizedContainers()!)
            {
                var artistRow = Assert.IsType<ArtistRow>(((ListBoxItem)row).Content);
                var rowTiles = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("artist-tile")).ToList();
                Assert.Equal(artistRow.Artists.Count, rowTiles.Count);
                for (var c = 0; c < rowTiles.Count; c++)
                {
                    Assert.Same(artistRow.Artists[c], rowTiles[c].DataContext);
                    Assert.NotNull(rowTiles[c].Command);
                    tiles.Add(rowTiles[c]);
                }
            }
        }
        Assert.True(tiles.Count <= 7 * 8, $"{tiles.Count} portraits built for 60 rows");
        window.Close();
    }

    /// <summary>Checks every realized row of a song list shows its own song and selection mark,
    /// and that rows were re-bound rather than rebuilt while scrolling through it.</summary>
    private static void ScrollAndCheckSongRows(ListBox list, IReadOnlyList<Track> items, int selectedIndex)
    {
        Assert.IsType<RetainingVirtualizingStackPanel>(list.ItemsPanelRoot);
        list.Selection.Select(selectedIndex);
        Pump();
        var sv = list.FindDescendantOfType<ScrollViewer>()!;
        var roots = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var containers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var y in new[] { 0.0, 900, 935, 4000, 12000, 600, 0 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in list.GetRealizedContainers()!)
            {
                var item = Assert.IsType<ListBoxItem>(row);
                var index = list.IndexFromContainer(row);
                Assert.Same(items[index], item.Content);
                Assert.Same(items[index], item.Presenter?.Child?.DataContext);
                Assert.Equal(index == selectedIndex, item.IsSelected);
            }
            foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                containers.Add(row);
                if (row.Presenter?.Child is { } child) roots.Add(child);
            }
        }
        Assert.Equal(new[] { selectedIndex }, list.Selection.SelectedIndexes.ToArray());
        Assert.Equal(containers.Count, roots.Count);
    }

    private static Track Song(int i, string folder) => new()
    {
        Id = Guid.NewGuid(), Title = $"Song {i:0000}", Artist = $"Artist {i % 23}", AlbumArtist = $"Artist {i % 23}",
        Album = $"Album {i / 10}", TrackNumber = i % 10 + 1, Duration = TimeSpan.FromMinutes(3),
        FilePath = TestPaths.Primary(folder, $"{i:0000}.flac"), DateAdded = DateTime.UtcNow.AddDays(-(i % 7)),
    };

    [AvaloniaFact]
    public async Task Inbox_ScrolledRows_AreReused_AndShowTheirOwnSong()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 800; i++)
        {
            var t = Song(i, "inbox");
            t.IsInInbox = true;
            lib.InboxList.Add(t);
        }
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new InboxViewModel(lib, player, new SidebarViewModel(persistence, lib)) { IsActive = true };
        var view = new InboxView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        await PumpUntil(() => vm.Tracks.Count == 800);
        ScrollAndCheckSongRows(view.FindControl<ListBox>("TrackList")!, vm.Tracks, 7);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Folders_ScrolledRows_AreReused_AndShowTheirOwnSong()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 800; i++)
        {
            var t = Song(i, "folder");
            t.AddedIndividually = true; // a folder of songs added on their own is a root (GitHub #108)
            lib.TrackList.Add(t);
        }
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryFoldersViewModel(lib, player, persistence, new SidebarViewModel(persistence, lib));
        var view = new LibraryFoldersView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        vm.IsActive = true;
        vm.Refresh();
        await PumpUntil(() => vm.RootNodes.Count > 0);
        vm.SelectedNode = vm.RootNodes[0];
        await PumpUntil(() => vm.SelectedFolderTracks.Count == 800);
        ScrollAndCheckSongRows(view.FindControl<ListBox>("TrackList")!, vm.SelectedFolderTracks, 11);
        window.Close();
    }

    /// <summary>A row scrolled off while one of its buttons has the keyboard focus is hidden and
    /// parked: the focus must not stay on the invisible button.</summary>
    [AvaloniaFact]
    public async Task Songs_FocusedButtonInARowThatScrollsOff_DoesNotKeepTheFocus()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var list = view.TrackList;
        var row = list.GetRealizedContainers()!.OfType<ListBoxItem>().First(r => list.IndexFromContainer(r) == 2);
        var heart = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row-heart"));
        Assert.True(heart.Focus());
        Pump();
        Assert.Same(heart, window.FocusManager!.GetFocusedElement());

        list.FindDescendantOfType<ScrollViewer>()!.Offset = new Vector(0, 9000);
        Pump();
        var focused = window.FocusManager!.GetFocusedElement() as Visual;
        Assert.True(focused == null || focused.IsEffectivelyVisible,
            $"focus stayed on {focused?.GetType().Name} (visible {focused?.IsEffectivelyVisible})");
        window.Close();
    }

    /// <summary>Artists list view: arrow keys move on from the selected artist even after its row
    /// scrolled off and was parked, and only the new row shows as selected.</summary>
    [AvaloniaFact]
    public async Task ArtistsList_ArrowKeys_ContinueFromTheSelectedArtist_AfterItsRowWasParked()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 300; i++)
        {
            var name = $"Artist {i:000}";
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = name, AlbumArtist = name, Album = $"Album {i}",
                FilePath = TestPaths.Primary("a", $"{i}.flac"), Duration = TimeSpan.FromMinutes(3) });
            lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = name, TrackCount = 1, AlbumCount = 1 });
        }
        var vm = new LibraryArtistsViewModel(lib);
        var view = new LibraryArtistsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        vm.Refresh();
        if (!vm.IsListView) vm.ToggleViewModeCommand.Execute(null);
        await PumpUntil(() => vm.ListRows.Count == 300);
        var table = view.FindControl<ListBox>("ArtistTable")!;
        Assert.IsType<RetainingVirtualizingStackPanel>(table.ItemsPanelRoot);
        table.Focus();
        table.SelectedIndex = 2;
        Pump();

        table.FindDescendantOfType<ScrollViewer>()!.Offset = new Vector(0, 9000);
        Pump();
        table.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Down, Source = table });
        await PumpUntil(() => table.ContainerFromIndex(3) is { IsVisible: true });

        Assert.Equal(3, table.SelectedIndex);
        var selectedRows = table.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsSelected).ToList();
        var only = Assert.Single(selectedRows);
        Assert.True(only.IsVisible);
        Assert.Equal(3, table.IndexFromContainer(only));
        Assert.Same(vm.ListRows[3], only.Content);
        Assert.True(table.IsKeyboardFocusWithin);
        window.Close();
    }

    /// <summary>Screen readers walk the automation tree: parked (hidden) rows must not be in it.</summary>
    [AvaloniaFact]
    public async Task Songs_AutomationTree_ShowsOnlyTheRealizedRows()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var list = view.TrackList;
        var sv = list.FindDescendantOfType<ScrollViewer>()!;
        foreach (var y in new[] { 3000.0, 9000, 600 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
        }
        Assert.Contains(list.GetVisualDescendants().OfType<ListBoxItem>(), r => !r.IsVisible); // some rows are parked

        var peers = new List<Avalonia.Automation.Peers.AutomationPeer>();
        void Walk(Avalonia.Automation.Peers.AutomationPeer p)
        {
            foreach (var c in p.GetChildren()) { peers.Add(c); Walk(c); }
        }
        Walk(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(list));
        var rowPeers = peers.OfType<Avalonia.Automation.Peers.ControlAutomationPeer>().Where(p => p.Owner is ListBoxItem).ToList();
        Assert.NotEmpty(rowPeers);
        Assert.All(rowPeers, p => Assert.True(p.Owner.IsVisible, "a parked row is in the automation tree"));
        Assert.Equal(list.GetRealizedContainers()!.Count(), rowPeers.Count);
        window.Close();
    }
}
