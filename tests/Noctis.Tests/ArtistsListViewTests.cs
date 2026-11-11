using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Artists page list view (Discord, vari: "List/column view for artists, like how the songs
/// tab is organized"): a grid ↔ list toggle that persists, Songs-style rows with Albums /
/// Songs / Time / Genre columns, sortable headers, search, keyboard and mouse, and Inbox
/// songs left out of every total.
/// </summary>
public class ArtistsListViewTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ArtistsListViewTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Song(string artist, string album, int seconds, string genre = "") => new()
    {
        Id = Guid.NewGuid(),
        Title = $"{artist} {album} {seconds}",
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        AlbumId = Track.ComputeAlbumId(artist, album),
        Duration = TimeSpan.FromSeconds(seconds),
        Genre = genre,
    };

    private static Artist A(string name, int tracks, int albums, bool favorite = false) => new()
    {
        Id = LibraryService.ComputeArtistId(name), Name = name, TrackCount = tracks, AlbumCount = albums, IsFavorite = favorite,
    };

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ── Totals ──

    [Fact]
    public void ComputeStats_SumsEachArtistsLength_AndPicksTheDominantGenre()
    {
        var tracks = new List<Track>
        {
            Song("Caribou", "Swim", 200, "electronic"), Song("Caribou", "Swim", 100, "Electronic; Dance"),
            Song("Caribou", "Our Love", 60, "Dance"), Song("caribou", "Our Love", 40, ""),
            Song("Adele", "25", 300),
        };

        var stats = LibraryArtistsViewModel.ComputeStats(tracks, ArtistGroupMode.Artist);

        // Same case-insensitive buckets as LibraryService's Artist counts.
        Assert.Equal(TimeSpan.FromSeconds(400), stats["Caribou"].Duration);
        // electronic 2, dance 2: the tie breaks alphabetically, title-cased like the artist page.
        Assert.Equal("Dance", stats["Caribou"].Genre);
        Assert.Equal(TimeSpan.FromSeconds(300), stats["Adele"].Duration);
        Assert.Equal(string.Empty, stats["Adele"].Genre);
    }

    /// <summary>The owner's library size (~7,200 songs): prints what the totals pass costs on
    /// the rebuild's worker thread. A report, not a timing bound (CI noise).</summary>
    [Fact]
    public void ComputeStats_OverAnOwnerSizedLibrary_Report()
    {
        var tracks = Enumerable.Range(0, 7200)
            .Select(i => Song($"Artist {i % 1500}", $"Album {i % 900}", 200 + i % 60, i % 4 == 0 ? "Rock; Indie" : "Pop"))
            .ToList();
        LibraryArtistsViewModel.ComputeStats(tracks, ArtistGroupMode.Artist); // JIT
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = LibraryArtistsViewModel.ComputeStats(tracks, ArtistGroupMode.Artist);
        _out.WriteLine($"PROBE artists list totals: {tracks.Count} songs, {stats.Count} artists, {sw.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Equal(1500, stats.Count);
    }

    [Fact]
    public void ListRows_CarryTheCountsTimeAndGenre_InTheGridsOrder()
    {
        var artists = new List<Artist> { A("Bad Bunny", 40, 5), A("Aphex Twin", 12, 2, favorite: true), A("Caribou", 25, 3) };
        var stats = new Dictionary<string, LibraryArtistsViewModel.ArtistStats>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bad Bunny"] = new(TimeSpan.FromMinutes(130), "Reggaeton"),
            ["caribou"] = new(TimeSpan.FromSeconds(3351), "Electronic"),
        };

        var rows = LibraryArtistsViewModel.BuildListRows(artists, string.Empty, "name", ascending: true, stats);
        var grid = LibraryArtistsViewModel.BuildRows(artists, string.Empty, "name", ascending: true, stats: stats);

        Assert.Equal(grid.SelectMany(r => r.Artists).Select(a => a.Name), rows.Select(r => r.Name));
        Assert.Equal(new[] { "Aphex Twin", "Bad Bunny", "Caribou" }, rows.Select(r => r.Name)); // favorite first
        var bunny = rows[1];
        Assert.Equal(5, bunny.AlbumCount);
        Assert.Equal(40, bunny.TrackCount);
        Assert.Equal("2h 10m", bunny.DurationFormatted);
        Assert.Equal("Reggaeton", bunny.Genre);
        Assert.Equal("55m 51s", rows[2].DurationFormatted);
        // No songs seen for the artist: zero length, no genre (not a crash).
        Assert.Equal("0m 00s", rows[0].DurationFormatted);
        Assert.Equal(string.Empty, rows[0].Genre);
        Assert.Same(artists[1], rows[0].Artist);
        Assert.Equal("Aphex Twin", rows[0].ToString()); // the screen-reader name of the row
    }

    // ── Sorting ──

    [Fact]
    public void TimeSort_RanksByLength_GenreSort_PutsUntaggedArtistsLastBothWays()
    {
        var artists = new List<Artist> { A("Adele", 1, 1), A("Bjork", 1, 1), A("Caribou", 1, 1), A("Drake", 1, 1) };
        var stats = new Dictionary<string, LibraryArtistsViewModel.ArtistStats>(StringComparer.OrdinalIgnoreCase)
        {
            ["Adele"] = new(TimeSpan.FromMinutes(10), "Pop"),
            ["Bjork"] = new(TimeSpan.FromMinutes(30), ""),
            ["Caribou"] = new(TimeSpan.FromMinutes(20), "Electronic"),
            ["Drake"] = new(TimeSpan.FromMinutes(5), "Hip Hop"),
        };
        List<string> Order(string mode, bool asc) =>
            LibraryArtistsViewModel.BuildListRows(artists, string.Empty, mode, asc, stats).Select(r => r.Name).ToList();

        Assert.Equal(new[] { "Bjork", "Caribou", "Adele", "Drake" }, Order("time", asc: false));
        Assert.Equal(new[] { "Drake", "Adele", "Caribou", "Bjork" }, Order("time", asc: true));
        Assert.Equal(new[] { "Caribou", "Drake", "Adele", "Bjork" }, Order("genre", asc: true));
        Assert.Equal(new[] { "Adele", "Drake", "Caribou", "Bjork" }, Order("genre", asc: false));
    }

    [AvaloniaFact]
    public void HeaderClick_SortsByThatColumn_AndASecondClickFlipsIt()
    {
        var vm = new LibraryArtistsViewModel(new FakeLibraryService());
        Assert.Equal("name", vm.SortMode);

        vm.SortByColumnCommand.Execute("time");
        Assert.Equal("time", vm.SortMode);
        Assert.False(vm.SortAscending); // lengths read longest-first, like the counts
        Assert.Equal("Time", vm.SortLabel);

        vm.SortByColumnCommand.Execute("time");
        Assert.True(vm.SortAscending);

        vm.SortByColumnCommand.Execute("genre");
        Assert.Equal("genre", vm.SortMode);
        Assert.True(vm.SortAscending);

        vm.SortByColumnCommand.Execute("name");
        vm.SortByColumnCommand.Execute("name");
        Assert.Equal("name", vm.SortMode);
        Assert.False(vm.SortAscending); // Z→A on the second click, as on the Songs headers
    }

    // ── Plays (GitHub #231) ──

    private static Track Played(string artist, int plays)
    {
        var t = Song(artist, "Album", 200);
        t.PlayCount = plays;
        return t;
    }

    /// <summary>"Coin" 5 + "Coin feat. X" 3 (the credit's first artist, as Home's Top Artists
    /// and the Songs count bucket it), "X" 4, "Rich Aucoin" 6, "Adele" never played.</summary>
    private static List<Track> PlayedTracks() => new()
    {
        Played("Coin", 5), Played("Coin feat. X", 3), Played("X", 4), Played("Rich Aucoin", 6), Played("Adele", 0),
    };

    [Fact]
    public void ComputeStats_SumsPlays_ByTheSameArtistAsHomeTopArtists()
    {
        var tracks = PlayedTracks();
        var stats = LibraryArtistsViewModel.ComputeStats(tracks, ArtistCredit.GroupMode);

        Assert.Equal(8, stats["Coin"].Plays);
        Assert.Equal(4, stats["X"].Plays);
        Assert.Equal(6, stats["Rich Aucoin"].Plays);
        Assert.Equal(0, stats["Adele"].Plays);
        // HomeViewModel's Top Artists bucket: PlayCount summed per Track.GroupingArtist.
        foreach (var g in tracks.GroupBy(t => t.GroupingArtist, StringComparer.OrdinalIgnoreCase))
            Assert.Equal(g.Sum(t => (long)t.PlayCount), stats[g.Key].Plays);
    }

    [AvaloniaFact]
    public void PlaysSort_MostPlayedFirst_ShowsTheCount_AndFlipsOnASecondClick()
    {
        var artists = new List<Artist> { A("Adele", 1, 1), A("Coin", 2, 1), A("Rich Aucoin", 1, 1), A("X", 1, 1, favorite: true) };
        var stats = LibraryArtistsViewModel.ComputeStats(PlayedTracks(), ArtistGroupMode.Artist);

        var rows = LibraryArtistsViewModel.BuildListRows(artists, string.Empty, "plays", ascending: false, stats);
        Assert.Equal(new[] { "Coin", "Rich Aucoin", "X", "Adele" }, rows.Select(r => r.Name)); // a favorite takes its real rank
        Assert.Equal(new long[] { 8, 6, 4, 0 }, rows.Select(r => r.PlayCount));
        Assert.Equal(new[] { "Adele", "X", "Rich Aucoin", "Coin" },
            LibraryArtistsViewModel.BuildListRows(artists, string.Empty, "plays", ascending: true, stats).Select(r => r.Name));

        var vm = new LibraryArtistsViewModel(new FakeLibraryService());
        vm.SortByColumnCommand.Execute("plays");
        Assert.Equal("plays", vm.SortMode);
        Assert.False(vm.SortAscending); // like Last.fm: most played first
        Assert.Equal(Noctis.Localization.Loc.T("Main.Plays"), vm.SortLabel);
        vm.SortByColumnCommand.Execute("plays");
        Assert.True(vm.SortAscending);
        vm.SetSortCommand.Execute("name");
        vm.SetSortCommand.Execute("plays");
        Assert.False(vm.SortAscending);
    }

    [AvaloniaFact]
    public async Task PlaysSort_AgreesWithHomeTopArtists()
    {
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(PlayedTracks());
        lib.ArtistList.AddRange(new[] { A("Adele", 1, 1), A("Coin", 2, 1), A("Rich Aucoin", 1, 1), A("X", 1, 1) });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var home = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await home.RefreshAsync();

        var vm = new LibraryArtistsViewModel(lib, player) { ViewMode = "list" };
        vm.SetSortCommand.Execute("plays");
        vm.IsActive = true;
        await PumpUntil(() => vm.ListRows.Count == 4);

        Assert.Equal(home.TopArtists.Select(a => a.Name), vm.ListRows.Take(home.TopArtists.Count).Select(r => r.Name));
        Assert.Equal(new[] { "Coin", "Rich Aucoin", "X" }, home.TopArtists.Select(a => a.Name));
    }

    [AvaloniaFact]
    public async Task ACountedPlay_ReRanksThePlaysSort_WithoutALibraryUpdate()
    {
        var lib = new FakeLibraryService();
        var tracks = PlayedTracks();
        lib.TrackList.AddRange(tracks);
        lib.ArtistList.AddRange(new[] { A("Adele", 1, 1), A("Coin", 2, 1), A("Rich Aucoin", 1, 1), A("X", 1, 1) });
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LibraryArtistsViewModel(lib, player) { ViewMode = "list" };
        vm.SetSortCommand.Execute("plays");
        vm.IsActive = true;
        await PumpUntil(() => vm.ListRows.Count == 4);
        Assert.Equal(new[] { "Coin", "Rich Aucoin", "X", "Adele" }, vm.ListRows.Select(r => r.Name));

        var countPlay = typeof(PlayerViewModel).GetMethod("CountPlay",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var x = tracks.Single(t => t.Artist == "X");
        for (var i = 0; i < 3; i++) countPlay.Invoke(player, new object[] { x });
        await PumpUntil(() => vm.ListRows[1].Name == "X");

        Assert.Equal(new[] { "Coin", "X", "Rich Aucoin", "Adele" }, vm.ListRows.Select(r => r.Name));
        Assert.Equal(7, vm.ListRows[1].PlayCount);

        // Under another sort the Plays column still moves, and a switch back sorts by today's counts.
        vm.SetSortCommand.Execute("name");
        await PumpUntil(() => vm.ListRows[0].Name == "Adele");
        countPlay.Invoke(player, new object[] { tracks.Single(t => t.Artist == "Adele") });
        await PumpUntil(() => vm.ListRows[0].PlayCount == 1);
        Assert.Equal(1, vm.ListRows[0].PlayCount);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task PlaysSort_SurvivesSaveAndReload()
    {
        var settings = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await settings.LoadAsync();
        settings.ArtistSortMode = "plays";
        settings.ArtistSortAscending = false;
        await settings.SaveAsync();

        var reloaded = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await reloaded.LoadAsync();
        Assert.Equal("plays", reloaded.ArtistSortMode);
        Assert.False(reloaded.ArtistSortAscending);
    }

    // ── Search ──

    [AvaloniaFact]
    public async Task Search_FiltersTheListRows_LikeTheGrid()
    {
        var lib = new FakeLibraryService();
        lib.ArtistList.AddRange(new[] { A("Caribou", 2, 1), A("Adele", 1, 1), A("Carly Rae Jepsen", 1, 1) });
        lib.TrackList.AddRange(new[] { Song("Caribou", "Swim", 200), Song("Caribou", "Swim", 100), Song("Adele", "25", 300) });
        var vm = new LibraryArtistsViewModel(lib) { ViewMode = "list" };
        vm.IsActive = true;
        await PumpUntil(() => vm.ListRows.Count == 3);
        Assert.Equal(3, vm.ListRows.Count);

        vm.ApplyFilter("cari");
        await PumpUntil(() => vm.ListRows.Count == 1);

        var row = Assert.Single(vm.ListRows);
        Assert.Equal("Caribou", row.Name);
        Assert.Equal("5m 00s", row.DurationFormatted);
        Assert.Equal(vm.ArtistRows.SelectMany(r => r.Artists).Select(a => a.Name), vm.ListRows.Select(r => r.Name));
    }

    // ── Inbox ──

    [AvaloniaFact]
    public async Task InboxSongs_AreLeftOutOfTheListsCountsTimeAndGenre()
    {
        Track Library(string title, int n, string genre) => new()
        {
            Id = Guid.NewGuid(), Title = title, Artist = "Tester", AlbumArtist = "Tester", Album = "Library Album",
            AlbumId = Track.ComputeAlbumId("Tester", "Library Album"), TrackNumber = n,
            FilePath = Path.Combine("C:", "Music", title + ".flac"), Duration = TimeSpan.FromMinutes(3), Genre = genre,
        };
        Track Inbox(string title, int n) => new()
        {
            Id = Guid.NewGuid(), Title = title, Artist = "Tester", AlbumArtist = "Tester", Album = "Downloads",
            AlbumId = Track.ComputeAlbumId("Tester", "Downloads"), TrackNumber = n,
            FilePath = Path.Combine("C:", "Music", "Downloads", title + ".flac"), Duration = TimeSpan.FromMinutes(10),
            Genre = "Ambient", IsInInbox = true,
        };
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { InboxEnabled = true });
        await persistence.SaveLibraryAsync(new List<Track>
        {
            Library("Known A", 1, "Rock"), Library("Known B", 2, ""), Inbox("New 1", 1), Inbox("New 2", 2),
        });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new AuditTrailService(persistence));
        await library.LoadAsync();
        await library.BackgroundInit;
        Assert.Equal(2, library.InboxTracks.Count);

        var vm = new LibraryArtistsViewModel(library) { ViewMode = "list" };
        vm.IsActive = true;
        await PumpUntil(() => vm.ListRows.Count > 0);

        var row = Assert.Single(vm.ListRows);
        Assert.Equal("Tester", row.Name);
        Assert.Equal(1, row.AlbumCount);       // not the Inbox's "Downloads"
        Assert.Equal(2, row.TrackCount);
        Assert.Equal("6m 00s", row.DurationFormatted); // 2 × 3 min, not + 2 × 10 min
        Assert.Equal("Rock", row.Genre);       // the two Inbox "Ambient" songs would outvote it
    }

    // ── Persistence ──

    [AvaloniaFact]
    public async Task ViewMode_DefaultsToGrid_AndSurvivesSaveAndReload()
    {
        var settings = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await settings.LoadAsync();
        Assert.Equal("grid", settings.ArtistViewMode);
        Assert.False(new LibraryArtistsViewModel(new FakeLibraryService()).IsListView);

        settings.ArtistViewMode = "list";
        await settings.SaveAsync();

        var reloaded = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await reloaded.LoadAsync();
        Assert.Equal("list", reloaded.ArtistViewMode);
        Assert.Equal("list", (await new PersistenceService(_root).LoadSettingsAsync()).ArtistViewMode);
    }

    [AvaloniaFact]
    public void ViewToggle_FlipsGridAndList_AndAnUnknownModeIsTheGrid()
    {
        var vm = new LibraryArtistsViewModel(new FakeLibraryService());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleViewModeCommand.Execute(null);
        Assert.True(vm.IsListView);
        Assert.Contains(nameof(LibraryArtistsViewModel.IsListView), changed);
        vm.ToggleViewModeCommand.Execute(null);
        Assert.False(vm.IsListView);

        vm.ViewMode = "columns"; // a hand-edited settings file
        Assert.Equal("grid", vm.ViewMode);
    }

    // ── Top bar ──

    /// <summary>The Artists sort icon (and now the grid/list icon) bound HasArtistSort alone, so
    /// both stayed over Cover Flow, which shows albums — the Folders/Playlists/Favorites leak
    /// TopBarCoverFlowGatingTests covers, on the Artists page.</summary>
    [Fact]
    public void ArtistSortAndViewIcons_HideUnderCoverFlow_AndComeBackAfter()
    {
        var noop = new RelayCommand(() => { });
        var bar = new TopBarViewModel();
        bar.ShowArtistSort(noop, "Name", "name", ascending: true, toggleViewCommand: noop, isListView: true);
        Assert.True(bar.ArtistSortVisible);
        Assert.True(bar.IsArtistListView);
        Assert.Same(noop, bar.ToggleArtistViewCommand);

        bar.ShowViewModeToggle(noop, noop, isCoverFlowMode: true);
        Assert.True(bar.HasArtistSort);
        Assert.False(bar.ArtistSortVisible);

        bar.ShowViewModeToggle(noop, noop, isCoverFlowMode: false);
        Assert.True(bar.ArtistSortVisible);

        bar.HideArtistSort();
        Assert.False(bar.ArtistSortVisible);
        Assert.Null(bar.ToggleArtistViewCommand);
    }

    // ── The view ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Settle(Window window, int passes = 6)
    {
        for (var i = 0; i < passes; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static List<ListBoxItem> Realized(ListBox list) =>
        list.GetRealizedContainers().OfType<ListBoxItem>().ToList();

    /// <summary>Raises the routed event a double click raises (as AlbumPageTintStrengthTests).</summary>
    private static void DoubleTap(Control target, Visual root)
    {
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? default;
        var press = new PointerPressedEventArgs(target, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 2);
        target.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, press));
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string?> MenuV2Rows(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string).ToList();

    private static Point CentreOf(Visual v, Window window)
        => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;

    private async Task<(LibraryArtistsViewModel vm, LibraryArtistsView view, Window window)> MountListAsync(int artists)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < artists; i++)
        {
            var name = $"Artist {i:D4}";
            lib.ArtistList.Add(A(name, 2, 1, favorite: i == 1));
            lib.TrackList.Add(Song(name, "Album", 180, i % 3 == 0 ? "Indie Pop" : "Rock"));
            lib.TrackList.Add(Song(name, "Album", 200));
        }
        var vm = new LibraryArtistsViewModel(lib) { ViewMode = "list" };
        vm.IsActive = true;
        await PumpUntil(() => vm.ListRows.Count == artists);
        var view = new LibraryArtistsView { DataContext = vm };
        var window = new Window { Width = 1300, Height = 800, Content = view };
        window.Show();
        Settle(window);
        return (vm, view, window);
    }

    [AvaloniaFact]
    public async Task ListView_ShowsInPlaceOfTheGrid_AndVirtualizesItsRows()
    {
        var (vm, view, window) = await MountListAsync(2000);
        try
        {
            Assert.False(view.ArtistListBox.IsVisible);
            Assert.True(view.ArtistTable.IsEffectivelyVisible);
            var realized = Realized(view.ArtistTable);
            _out.WriteLine($"realized rows: {realized.Count} of {vm.ListRows.Count}");
            Assert.InRange(realized.Count, 5, 60); // 800px window / 54px rows, not 2,000
            Assert.All(realized, item => Assert.Equal(LibraryArtistsView.ListRowHeight, item.Bounds.Height, 0.5));

            // Header labels sit over their cells (same column grid, same insets as Songs).
            var first = realized[0];
            var texts = first.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("1", texts);       // albums
            Assert.Contains("2", texts);       // songs
            Assert.Contains("6m 20s", texts);  // 180 s + 200 s
            Assert.Contains("Indie Pop", texts);

            // Back to the grid: the tiles come back, the list hides.
            vm.ToggleViewModeCommand.Execute(null);
            Settle(window);
            Assert.True(view.ArtistListBox.IsVisible);
            Assert.False(view.ArtistTable.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("artist-tile"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ListView_Keyboard_ClickThenArrowsEndAndEnter_OpenTheSelectedArtist()
    {
        var (vm, view, window) = await MountListAsync(300);
        try
        {
            Artist? opened = null;
            vm.ArtistOpened += (_, a) => opened = a;

            var row = Realized(view.ArtistTable)[2];
            var p = CentreOf(row, window);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Settle(window, 2);
            Assert.Equal(2, view.ArtistTable.SelectedIndex);
            Assert.True(view.ArtistTable.IsKeyboardFocusWithin);

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.Equal(3, view.ArtistTable.SelectedIndex);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Assert.Equal(1, view.ArtistTable.SelectedIndex);

            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Settle(window);
            Assert.Equal(299, view.ArtistTable.SelectedIndex);
            // Scrolled into view: the last row is realized now.
            Assert.Contains(Realized(view.ArtistTable), c => ReferenceEquals(c.DataContext, vm.ListRows[299]));

            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Assert.True(view.ArtistTable.SelectedIndex > 5, $"Page Down went to {view.ArtistTable.SelectedIndex}");

            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Same(vm.ListRows[0].Artist, opened);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ListView_DoubleClickOpens_RightClickGivesTheTilesMenu()
    {
        var (vm, view, window) = await MountListAsync(50);
        try
        {
            Artist? opened = null;
            vm.ArtistOpened += (_, a) => opened = a;
            var row = Realized(view.ArtistTable)[1];
            var expected = ((ArtistListRow)row.DataContext!).Artist;

            // Double-click on the row's name.
            var name = row.GetVisualDescendants().OfType<Noctis.Controls.HighlightTextBlock>().First();
            DoubleTap(name, window);
            Assert.Same(expected, opened);

            // A double-click on the 3-dots button does not open the page.
            opened = null;
            var dots = row.GetVisualDescendants().OfType<Button>().Single();
            DoubleTap(dots, window);
            Assert.Null(opened);

            // Right-click anywhere on the row: the artist tile's menu, bound to this artist.
            var p = CentreOf(row, window);
            window.MouseDown(p, MouseButton.Right);
            window.MouseUp(p, MouseButton.Right);
            Settle(window, 3);
            var menu = row.ContextMenu;
            Assert.NotNull(menu);
            Assert.True(menu!.IsOpen);
            Assert.Same(expected, menu.DataContext);
            Assert.Contains(MenuV2Rows(menu), h => h == Noctis.Localization.Loc.T("Menu.Snooze"));
            Assert.Contains(MenuV2Rows(menu), h => h == Noctis.Localization.Loc.T("LibraryArtists.FindPictureOnline"));
            menu.Close();
            Settle(window, 2);

            // The 3-dots button opens the same menu for its own row.
            var other = Realized(view.ArtistTable)[4];
            var otherDots = other.GetVisualDescendants().OfType<Button>().Single();
            otherDots.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window, 3);
            Assert.True(menu.IsOpen);
            Assert.Same(((ArtistListRow)other.DataContext!).Artist, menu.DataContext);
            menu.Close();
        }
        finally { window.Close(); }
    }

    // ── Eyeball probe (NOCTIS_TEST_SKIA=1): the list in both themes, saved as PNGs ──

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [AvaloniaFact]
    public async Task Probe_RenderListView()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var outDir = Path.Combine(FindRepoRoot(), "artifacts", "artists-list");
        Directory.CreateDirectory(outDir);
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var (vm, view, window) = await MountListAsync(40);
            try
            {
                window.Background = Application.Current.TryGetResource("AppMainBackground", variant, out var bg) ? (IBrush?)bg : null;
                // The top-bar icons this adds, at corner-icon size, above the page.
                var icons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(16, 8) };
                foreach (var key in new[] { "ListViewIcon", "GridViewIcon", "SortIcon", "CoverFlowIcon" })
                    icons.Children.Add(new PathIcon { Data = (Geometry)Application.Current.FindResource(key)!, Width = 16, Height = 16 });
                window.Content = null;
                var dock = new DockPanel();
                DockPanel.SetDock(icons, Dock.Top);
                dock.Children.Add(icons);
                dock.Children.Add(view);
                window.Content = dock;
                Settle(window);
                var row = Realized(view.ArtistTable)[3];
                view.ArtistTable.SelectedIndex = 3;
                Settle(window, 3);
                var path = Path.Combine(outDir, $"artists-list-{variant}.png");
                window.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);

                vm.ApplyFilter("artist 001");
                await PumpUntil(() => vm.ListRows.Count < 40);
                Settle(window);
                path = Path.Combine(outDir, $"artists-list-search-{variant}.png");
                window.CaptureRenderedFrame()!.Save(path);
                _out.WriteLine(path);
            }
            finally { window.Close(); }
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
