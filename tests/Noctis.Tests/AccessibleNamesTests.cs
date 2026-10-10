using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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
/// Screen readers and UI Automation read a control by its automation peer's name. With no
/// AutomationProperties.Name, Avalonia falls back to the content's ToString(), so the
/// sidebar rows read "Noctis.Models.NavItem" and the icon buttons "Avalonia.Controls.PathIcon"
/// (UIA measured live 10-09). These walk the real views and require every button and list
/// row to carry a real name.
/// </summary>
public class AccessibleNamesTests
{
    public AccessibleNamesTests() => AccessibleNames.Install();

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
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static string? NameOf(Control control)
        => ControlAutomationPeer.CreatePeerForElement(control).GetName();

    private static bool IsJunk(string? name)
        => string.IsNullOrWhiteSpace(name)
           || name.StartsWith("Avalonia.", StringComparison.Ordinal)
           || name.StartsWith("Noctis.", StringComparison.Ordinal);

    /// <summary>Every unnamed button / list, tab or tree row, described well enough to find it in XAML.</summary>
    private static List<string> Unnamed(IEnumerable<Control> controls)
        => controls
            .Where(c => c is Button or ListBoxItem or TabItem or TreeViewItem)
            .Where(c => AutomationProperties.GetAccessibilityView(c) != AccessibilityView.Raw) // not exposed
            .Select(c => (Control: c, Name: NameOf(c)))
            .Where(x => IsJunk(x.Name))
            .Select(x => Describe(x.Control, x.Name))
            .ToList();

    private static string Describe(Control control, string? name)
    {
        var path = string.Join(" < ", control.GetLogicalAncestors().OfType<Control>()
            .Where(a => !string.IsNullOrEmpty(a.Name)).Take(2).Select(a => a.Name));
        var tip = ToolTip.GetTip(control);
        return $"{control.GetType().Name}#{control.Name} [{string.Join(' ', control.Classes)}] "
               + $"tip={tip?.GetType().Name ?? "none"} name='{name}' in {path}";
    }

    /// <summary>Both trees: the visual one holds templated rows, the logical one the
    /// content of popups and flyout hosts that aren't open.</summary>
    private static void AssertAllNamed(Control root, Func<Control, bool>? skip = null)
    {
        var controls = root.GetVisualDescendants().OfType<Control>()
            .Concat(root.GetLogicalDescendants().OfType<Control>())
            .Distinct()
            .Where(c => skip == null || !skip(c));
        var unnamed = Unnamed(controls);
        Assert.True(unnamed.Count == 0, $"{unnamed.Count} unnamed:\n" + string.Join('\n', unnamed));
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 1280, Height = 900, Content = content };
        window.Show();
        Pump(window);
        return window;
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

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

    private static Track Song(string title, bool explicitLyrics = false, string artist = "Nine Inch Nails",
        string album = "Pretty Hate Machine", int plays = 0) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        FilePath = TestPaths.Primary("a11y", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(4),
        IsExplicit = explicitLyrics,
        PlayCount = plays,
    };

    private static List<Button> ButtonsWith(Control root, string cls)
        => root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(cls)).ToList();

    [AvaloniaFact]
    public void Sidebar_NavRowsReadTheirLabels_AndEveryButtonIsNamed()
    {
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService())
        {
            TopBar = new TopBarViewModel(),
        };
        vm.SidebarRows.Add(new PlaylistNavItem { Label = "Mixes", IsFolder = true, Folder = "Mixes" });
        vm.SidebarRows.Add(new PlaylistNavItem { Label = "Road Trip", PlaylistId = Guid.NewGuid() });
        var view = new SidebarView { DataContext = vm };
        var window = Show(view);
        try
        {
            var names = view.GetVisualDescendants().OfType<ListBoxItem>().Select(NameOf).ToList();
            Assert.Equal(vm.NavItems.Count + vm.FavoritesItems.Count + vm.SidebarRows.Count, names.Count);
            foreach (var item in vm.NavItems.Concat(vm.FavoritesItems))
                Assert.Contains(item.Label, names);
            Assert.Contains("Road Trip", names);
            Assert.Contains("Mixes", names);

            AssertAllNamed(view);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlaybackBar_EveryButtonIsNamed_AndPlayPauseFollowsState()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var window = Show(bar);
        try
        {
            AssertAllNamed(bar);

            var play = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("pill-btn-play"));
            Assert.Equal(player.PlayPauseTooltip, NameOf(play));
            Assert.Equal(Loc.T("PlaybackBar.VolumeTip"), NameOf(bar.FindControl<Button>("VolumeButton")!));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlaylistPage_HeaderAndTrackRowButtonsAreNamed()
    {
        var tracks = new List<Track> { Song("Head Like A Hole"), Song("Terrible Lie", explicitLyrics: true) };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Name = "Night Drive", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        await PumpUntil(() => vm.Tracks.Count == tracks.Count);

        var view = new PlaylistView { DataContext = vm };
        var window = Show(view);
        try
        {
            await PumpUntil(() => view.GetVisualDescendants().OfType<ListBoxItem>().Count() >= tracks.Count);
            Pump(window);
            Assert.True(view.GetVisualDescendants().OfType<ListBoxItem>().Count() >= tracks.Count, "the track rows never realized");

            AssertAllNamed(view);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AlbumPage_HeaderAndTrackRowButtonsAreNamed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var tracks = new List<Track> { Song("Head Like A Hole"), Song("Terrible Lie", explicitLyrics: true) };
        var album = new Album
        {
            Id = Guid.NewGuid(),
            Name = "Pretty Hate Machine",
            Artist = "Nine Inch Nails",
            TrackCount = tracks.Count,
            TotalDuration = TimeSpan.FromMinutes(8),
            Tracks = tracks,
        };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());

        var view = new AlbumDetailView { DataContext = vm };
        var window = Show(view);
        try
        {
            await PumpUntil(() => view.GetVisualDescendants().OfType<ListBoxItem>().Count() >= tracks.Count);
            Pump(window);
            Assert.True(view.GetVisualDescendants().OfType<ListBoxItem>().Count() >= tracks.Count, "the track rows never realized");

            AssertAllNamed(view);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Home read "Avalonia.Controls.StackPanel" for the hero's Resume and every Top
    /// Artists tile, and "Avalonia.Controls.Border" for each chart row (UIA, 10-09).</summary>
    [AvaloniaFact]
    public async Task HomePage_HeroArtistTilesChartRowsAndHistoryPillsAreNamed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var a = Song("Head Like A Hole", plays: 12);
        var b = Song("Terrible Lie", explicitLyrics: true, plays: 9);
        a.AlbumId = b.AlbumId = albumId;
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).Add(new Album
        {
            Id = albumId, Name = "Pretty Hate Machine", Artist = "Nine Inch Nails", Tracks = new List<Track> { a, b },
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a);
        player.History.Add(b);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();
        if (vm.TopArtists.Count == 0)
            vm.TopArtists.Add(new Artist { Id = Guid.NewGuid(), Name = "Nine Inch Nails" });
        vm.TimeRotationTracks.Add(a);
        vm.HeavyRotationTracks.Add(b);
        vm.RediscoveredTracks.Add(a);

        var view = new HomeView { DataContext = vm };
        var window = Show(view);
        try
        {
            await PumpUntil(() => ButtonsWith(view, "home-chart-row").Count > 0
                                  && ButtonsWith(view, "home-history-pill").Count >= 3);
            Pump(window);

            var resume = view.GetVisualDescendants().OfType<Button>().Single(x => x.Command == vm.ResumeContinueCommand);
            Assert.Equal(Loc.T("Home.Resume"), NameOf(resume));
            vm.IsContinuePlaying = true;
            Assert.Equal(Loc.T("Home.Pause"), NameOf(resume));
            vm.IsContinuePlaying = false;

            var artistTile = view.GetVisualDescendants().OfType<Button>().First(x => x.Command == vm.OpenTopArtistCommand);
            Assert.Equal(((Artist)artistTile.DataContext!).Name, NameOf(artistTile));

            var chartNames = ButtonsWith(view, "home-chart-row").Select(NameOf).ToList();
            Assert.Contains("Head Like A Hole, Nine Inch Nails", chartNames);
            Assert.Contains("Terrible Lie, Nine Inch Nails", chartNames);
            var pillNames = ButtonsWith(view, "home-history-pill").Select(NameOf).ToList();
            Assert.Equal(3, pillNames.Count);
            Assert.Contains("Terrible Lie, Nine Inch Nails", pillNames);

            AssertAllNamed(view);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The artist page's tabs, its Play pill, its song rows and its Similar Artists
    /// tiles read as their content's type name (UIA, 10-09).</summary>
    [AvaloniaFact]
    public async Task ArtistPage_TabsPillsSongRowsAndSimilarTilesAreNamed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var tracks = new List<Track> { Song("Head Like A Hole", plays: 5), Song("Terrible Lie", plays: 3) };
        foreach (var t in tracks) { t.AlbumId = albumId; t.Year = 1989; }
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        ((List<Album>)lib.Albums).Add(new Album
        {
            Id = albumId, Name = "Pretty Hate Machine", Artist = "Nine Inch Nails", Year = 1989,
            TrackCount = tracks.Count, Tracks = tracks,
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Nine Inch Nails", lib, player);
        var similar = new[]
        {
            new SimilarArtistRow { Name = "Ministry", IsInLibrary = true },
            new SimilarArtistRow { Name = "Skinny Puppy" },
        };
        foreach (var row in similar)
        {
            vm.SimilarArtists.Add(row);
            vm.OverviewSimilar.Add(row);
        }

        var view = new ArtistDetailView { DataContext = vm };
        var window = Show(view);
        try
        {
            await PumpUntil(() => ButtonsWith(view, "song-row").Count >= tracks.Count);
            Pump(window);

            var tabs = ButtonsWith(view, "page-tab").Select(NameOf).ToList();
            Assert.Equal(new[]
            {
                Loc.T("ArtistDetail.Overview"), Loc.T("ArtistDetail.Albums"), Loc.T("ArtistDetail.SinglesEPs"),
                Loc.T("ArtistDetail.Songs"), Loc.T("ArtistDetail.SimilarArtists"),
            }, tabs);
            var songNames = ButtonsWith(view, "song-row").Select(NameOf).ToList();
            Assert.Contains("Head Like A Hole, Nine Inch Nails", songNames);
            var tiles = ButtonsWith(view, "similar-tile").Select(NameOf).ToList();
            Assert.Contains("Ministry", tiles);
            Assert.Contains("Skinny Puppy", tiles);

            AssertAllNamed(view);

            // Every tab's content, not just the Overview's.
            foreach (var tab in new[] { "albums", "singles", "songs", "similar" })
            {
                vm.SelectTabCommand.Execute(tab);
                Pump(window);
                AssertAllNamed(view);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Queue page's Clear read "Avalonia.Controls.StackPanel" (UIA, 10-09); its
    /// rows (and the queue drawer's, which hold the same Track items) read "title, artist".</summary>
    [AvaloniaFact]
    public async Task QueuePage_ClearAndRowsAreNamed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.UpNext.Add(Song("Head Like A Hole"));
        player.UpNext.Add(Song("Terrible Lie"));
        player.History.Add(Song("Down In It"));
        var view = new QueueView { DataContext = new QueueViewModel(player) };
        var window = Show(view);
        try
        {
            await PumpUntil(() => view.GetVisualDescendants().OfType<ListBoxItem>().Count() >= 3);
            Pump(window);

            var clear = view.GetVisualDescendants().OfType<Button>()
                .Single(x => x.Command == ((QueueViewModel)view.DataContext!).ClearQueueCommand);
            Assert.Equal(Loc.T("Queue.Clear"), NameOf(clear));
            var rows = view.GetVisualDescendants().OfType<ListBoxItem>().Select(NameOf).ToList();
            Assert.Contains("Head Like A Hole, Nine Inch Nails", rows);
            Assert.Contains("Down In It, Nine Inch Nails", rows);

            AssertAllNamed(view);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The Folders tree's rows had an empty name (UIA, 10-09). Each reads its folder
    /// and song count, and keeps the tree item's expand/collapse pattern.</summary>
    [AvaloniaFact]
    public void FoldersTree_RowsReadTheirFolder_AndExpandCollapse()
    {
        var root = new FolderNode { FullPath = "C:/Music", DisplayName = "C:/Music", IsRoot = true, TotalTrackCount = 173 };
        root.Children.Add(new FolderNode { FullPath = "C:/Music/Bruno Mars", DisplayName = "Bruno Mars", TotalTrackCount = 172 });
        root.Children.Add(new FolderNode { FullPath = "C:/Music/Single", DisplayName = "Single", TotalTrackCount = 1 });
        var view = new LibraryFoldersView();
        var tree = view.FindControl<Noctis.Controls.FoldingTreeView>("FolderTree")!;
        tree.ItemsSource = new[] { root };
        var window = Show(view);
        try
        {
            var rootItem = (TreeViewItem)tree.ContainerFromItem(root)!;
            Assert.Equal("C:/Music, " + Loc.T("DescriptionDialog.Songs", 173), NameOf(rootItem));

            var peer = ControlAutomationPeer.CreatePeerForElement(rootItem);
            var expander = peer.GetProvider<Avalonia.Automation.Provider.IExpandCollapseProvider>();
            Assert.NotNull(expander);
            Assert.Equal(ExpandCollapseState.Collapsed, expander!.ExpandCollapseState);
            expander.Expand();
            Pump(window);
            Assert.True(root.IsExpanded);
            Assert.Equal(ExpandCollapseState.Expanded, expander.ExpandCollapseState);

            var bruno = (TreeViewItem)rootItem.ContainerFromItem(root.Children[0])!;
            Assert.Equal("Bruno Mars, " + Loc.T("DescriptionDialog.Songs", 172), NameOf(bruno));
            var single = (TreeViewItem)rootItem.ContainerFromItem(root.Children[1])!;
            Assert.Equal("Single, " + Loc.T("DescriptionDialog.Song"), NameOf(single));
            // A folder without subfolders is a leaf.
            Assert.Equal(ExpandCollapseState.LeafNode, ControlAutomationPeer.CreatePeerForElement(bruno)
                .GetProvider<Avalonia.Automation.Provider.IExpandCollapseProvider>()!.ExpandCollapseState);

            AssertAllNamed(view);

            expander.Collapse();
            Pump(window);
            Assert.False(root.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Track_ReadsAsTitleThenArtist()
        => Assert.Equal("Head Like A Hole, Nine Inch Nails", Song("Head Like A Hole").ToString());

    /// <summary>MainWindow isn't mounted headlessly (showing it runs app startup), but its
    /// XAML tree, the top bar's corner icons included, is built by the constructor. The
    /// sidebar and the island are checked above with their view models (their state
    /// tooltips are bindings, empty without one).</summary>
    [AvaloniaFact]
    public void MainWindow_TopBarAndQueueButtonsAreNamed()
    {
        var window = new MainWindow();
        try
        {
            // The queue's now-playing row binds the current track (none without a player);
            // its artist link reads the artist once there is one.
            var nowPlaying = window.FindControl<StackPanel>("QueueNowPlaying")!;
            var artistLink = nowPlaying.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("queue-link"));
            artistLink.DataContext = Song("Head Like A Hole");
            Assert.Equal("Nine Inch Nails", NameOf(artistLink));

            AssertAllNamed(window, skip: c => c.FindLogicalAncestorOfType<PlaybackBarView>() != null
                                              || c.FindLogicalAncestorOfType<SidebarView>() != null);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The queue drawer's rows, realized: the drawer is lifted out of the unshown
    /// MainWindow into a test window and fed tracks directly.</summary>
    [AvaloniaFact]
    public async Task QueueDrawer_ClearAndRowsAreNamed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        Window? window = null;
        try
        {
            var drawer = main.FindControl<Border>("QueuePopupPanel")!;
            var list = main.FindControl<ListBox>("QueuePopupListBox")!;
            ((Panel)drawer.Parent!).Children.Remove(drawer);
            drawer.IsVisible = true;
            drawer.Opacity = 1;
            list.ItemsSource = new List<Track> { Song("Head Like A Hole"), Song("Terrible Lie") };
            // The now-playing card (shown only with a current track) binds that track.
            main.FindControl<StackPanel>("QueueNowPlaying")!.GetLogicalDescendants().OfType<Border>().First()
                .DataContext = Song("Down In It");
            window = Show(drawer);
            await PumpUntil(() => list.GetVisualDescendants().OfType<ListBoxItem>().Count() >= 2);
            Pump(window);

            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().Select(NameOf).ToList();
            Assert.Equal(new[] { "Head Like A Hole, Nine Inch Nails", "Terrible Lie, Nine Inch Nails" }, rows);
            var actions = drawer.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("queue-action"))
                .Select(NameOf).ToList();
            Assert.Equal(new[] { Loc.T("Main.Close"), Loc.T("Main.Clear") }, actions);

            AssertAllNamed(drawer);
        }
        finally
        {
            window?.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void AlbumTileHoverButtons_AreNamedByTheSharedStyle()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var play = new Button { Classes = { "tile-play" }, Content = new Panel() };
        var more = new Button { Classes = { "tile-more" }, Content = new Viewbox() };
        var window = Show(new StackPanel { Children = { play, more } });
        try
        {
            Assert.Equal(Loc.T("Main.PlayTip"), NameOf(play));
            Assert.Equal(Loc.T("Main.OptionsTip"), NameOf(more));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TooltipNamesAnIconButton_ButExplicitNamesAndTextContentWin()
    {
        var icon = new Button { Content = new PathIcon() };
        ToolTip.SetTip(icon, "Shuffle");
        Assert.Equal("Shuffle", NameOf(icon));

        // A state tooltip (Play/Pause) or a language switch re-names it.
        ToolTip.SetTip(icon, "Aleatorio");
        Assert.Equal("Aleatorio", NameOf(icon));
        ToolTip.SetTip(icon, null);
        Assert.True(IsJunk(NameOf(icon)), "a cleared tooltip left its old name behind");

        var named = new Button { Content = new PathIcon() };
        AutomationProperties.SetName(named, "Options");
        ToolTip.SetTip(named, "More options for this song");
        Assert.Equal("Options", NameOf(named));

        var text = new Button { Content = "Play" };
        ToolTip.SetTip(text, "Play all songs in order");
        Assert.Equal("Play", NameOf(text));
    }

    [Fact]
    public void NavItem_ReadsAsItsLabel()
    {
        Assert.Equal("Home", new NavItem { Key = "home", Label = "Home" }.ToString());
        Assert.Equal("Road Trip", new PlaylistNavItem { Label = "Road Trip" }.ToString());
    }
}
