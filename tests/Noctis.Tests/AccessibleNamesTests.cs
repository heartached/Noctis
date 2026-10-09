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

    /// <summary>Every unnamed button / list row, described well enough to find it in XAML.</summary>
    private static List<string> Unnamed(IEnumerable<Control> controls)
        => controls
            .Where(c => c is Button or ListBoxItem)
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

    private static Track Song(string title, bool explicitLyrics = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Nine Inch Nails",
        Album = "Pretty Hate Machine",
        FilePath = TestPaths.Primary("a11y", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(4),
        IsExplicit = explicitLyrics,
    };

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
