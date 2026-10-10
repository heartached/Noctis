using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// The v2 context menu (10-09 redesign) applied everywhere (owner: "apply it everywhere"):
/// Songs, Folders (track + folder), Playlist page (track + header pills), Playlists grid,
/// Artists grid and the sidebar. Each menu must carry the v2 class, keep every command the
/// old menu had with the right parameter, keep the destructive row last, never leave a
/// stray separator, and follow a live language switch.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ContextMenuV2ViewsTests
{
    public ContextMenuV2ViewsTests() => ArtistCredit.ResetToDefaults();

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
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
        // Merged next to Icons.axaml in App.axaml; other test classes set up Icons.axaml only.
        if (!app.Resources.TryGetResource("MenuLineEyeOff", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/IconsMenuExtra.axaml")
            });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
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

    private static Track T(string title) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Adele", AlbumArtist = "Adele", Album = "25",
        AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = TestPaths.Primary("m", title + ".flac"),
    };

    /// <summary>Every command a user can reach: visible rows (recursively) and visible
    /// buttons in v2 panels (tiles, stars).</summary>
    private static HashSet<ICommand> Reachable(ItemCollection items)
    {
        var found = new HashSet<ICommand>();
        foreach (var o in items)
        {
            if (o is not MenuItem { IsVisible: true } item) continue;
            if (item.Command != null) found.Add(item.Command);
            found.UnionWith(Reachable(item.Items));
            if (item.Header is Control panel)
                foreach (var button in panel.GetLogicalDescendants().OfType<Button>().Where(x => x.IsVisible && x.Command != null))
                    found.Add(button.Command!);
        }
        return found;
    }

    private static void AssertSeparatorsClean(ItemCollection items)
    {
        var visible = items.OfType<Control>().Where(c => c.IsVisible).ToList();
        Assert.IsNotType<Separator>(visible.First());
        Assert.IsNotType<Separator>(visible.Last());
        for (var i = 1; i < visible.Count; i++)
            Assert.False(visible[i] is Separator && visible[i - 1] is Separator, $"two separators in a row at {i}");
    }

    private static List<Button> Tiles(ContextMenu menu)
        => menu.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile")).ToList();

    private static string? TileName(Button tile) => Avalonia.Automation.AutomationProperties.GetName(tile);

    private static MenuItem LastVisible(ContextMenu menu)
        => menu.Items.OfType<MenuItem>().Last(i => i.IsVisible);

    /// <summary>Every visible row and tile wired to one of <paramref name="commands"/> acts on <paramref name="target"/>.</summary>
    private static void AssertParameters(ContextMenu menu, object target, IEnumerable<ICommand> commands, params ICommand[] parameterless)
    {
        var set = commands.ToHashSet();
        foreach (var item in menu.GetLogicalDescendants().OfType<MenuItem>().Where(i => i.IsVisible && i.Command != null && set.Contains(i.Command)))
            if (!parameterless.Contains(item.Command))
                Assert.Same(target, item.CommandParameter);
        foreach (var tile in Tiles(menu).Where(t => t.Command != null && set.Contains(t.Command) && !parameterless.Contains(t.Command)))
            Assert.Same(target, tile.CommandParameter);
    }

    private static void RightClick(Control target, Control? source = null)
        => target.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = source ?? target });

    // ── Songs ──

    private static async Task<(LibrarySongsViewModel Vm, LibrarySongsView View, Window Win, ListBoxItem Row, Track Track)> MountSongs()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var track = T("Hello");
        lib.TrackList.Add(track);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        await PumpUntil(() => view.TrackList.GetRealizedContainers().Any(c => c.DataContext == track));
        var row = view.TrackList.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == track);
        return (vm, view, win, row, track);
    }

    [AvaloniaFact]
    public async Task SongsPage_RightClick_OpensTheV2MenuWithEveryCommand()
    {
        var (vm, _, win, row, track) = await MountSongs();
        RightClick(row);

        var menu = row.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen);
        Assert.Contains(MenuV2.MenuClass, menu.Classes);

        // Every command the classic menu had (LibrarySongsView.BindContextMenuToTrack).
        var commands = new ICommand[]
        {
            vm.PlayFromHereCommand, vm.ShuffleAllCommand, vm.PlayNextCommand, vm.AddToQueueCommand,
            vm.AddToNewPlaylistCommand, vm.ToggleFavoriteCommand, vm.OpenMetadataCommand, vm.SearchLyricsCommand,
            vm.ShowInExplorerCommand, vm.RemoveFromLibraryCommand, vm.ConvertTracksCommand, vm.ScanReplayGainCommand,
            vm.StartRadioCommand, vm.SnoozeForMonthCommand, vm.RateTrackCommand, vm.FetchLyricsCommand,
            vm.OpenLyricsStudioCommand, vm.RemoveLyricsCommand, vm.SendToFolderCommand,
        };
        var reachable = Reachable(menu.Items);
        foreach (var command in commands)
            Assert.Contains(command, reachable);
        Assert.Contains(SpectrogramLauncher.OpenCommand, reachable);
        Assert.Contains(LyricsBackgroundOverrides.ChooseForTrackCommand, reachable);
        // The four playback rows are tiles now; Shuffle never carried a parameter.
        Assert.Equal(new ICommand[] { vm.PlayFromHereCommand, vm.ShuffleAllCommand, vm.PlayNextCommand, vm.AddToQueueCommand },
            Tiles(menu).Select(t => t.Command!).ToArray());
        AssertParameters(menu, track, commands.Except(new ICommand[] { vm.RateTrackCommand }), vm.ShuffleAllCommand);

        var remove = LastVisible(menu);
        Assert.Same(vm.RemoveFromLibraryCommand, remove.Command);
        Assert.Contains("danger", remove.Classes);
        AssertSeparatorsClean(menu.Items);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public async Task SongsPage_LanguageSwitch_RebuildsTheMenuOnTheNextOpen()
    {
        var (_, _, win, row, _) = await MountSongs();
        try
        {
            RightClick(row);
            var english = row.ContextMenu!;
            english.Close();

            Loc.Instance.SetCulture("es");
            RightClick(row);
            var spanish = row.ContextMenu!;
            Assert.NotSame(english, spanish);
            Assert.Contains(MenuV2.MenuClass, spanish.Classes);
            Assert.Contains(Loc.T("LibraryAlbums.AddQueue"), Tiles(spanish).Select(TileName));
            Assert.Equal(Loc.T("LibraryAlbums.RemoveFromLibrary"), LastVisible(spanish).Header);
            spanish.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            win.Close();
        }
    }

    // ── Folders ──

    private sealed class SettingsPersistence : TestPersistenceService, IPersistenceService
    {
        public AppSettings Settings { get; } = new();
        public new Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    private static async Task<(LibraryFoldersViewModel Vm, LibraryFoldersView View, Window Win, Track Track)> MountFolders()
    {
        EnsureAppStyles();
        var root = TestPaths.Primary("Music");
        var track = new Track { Id = Guid.NewGuid(), Title = "A1", Artist = "Adele", Album = "25", FilePath = Path.Combine(root, "A", "a1.flac") };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var persistence = new SettingsPersistence();
        persistence.Settings.MusicFolders.Add(root);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryFoldersViewModel(lib, player, persistence, new SidebarViewModel(persistence, lib));
        vm.MarkDirty();
        await (Task)typeof(LibraryFoldersViewModel).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null)!;
        vm.SelectedNode = vm.RootNodes.Single().Children.Single(n => n.DisplayName == "A");

        var view = new LibraryFoldersView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        await PumpUntil(() => view.TrackList.GetRealizedContainers().Any(c => c.DataContext == track));
        return (vm, view, win, track);
    }

    [AvaloniaFact]
    public async Task FoldersPage_TrackRightClick_OpensTheV2MenuWithEveryCommand()
    {
        var (vm, view, win, track) = await MountFolders();
        var row = view.TrackList.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == track);
        RightClick(row);

        var menu = row.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.Contains(MenuV2.MenuClass, menu.Classes);
        var commands = new ICommand[]
        {
            vm.PlayTrackCommand, vm.ShuffleFolderCommand, vm.PlayNextCommand, vm.AddToQueueCommand,
            vm.AddToNewPlaylistCommand, vm.ToggleFavoriteCommand, vm.OpenMetadataCommand, vm.SearchLyricsCommand,
            vm.ShowInExplorerCommand, vm.RemoveFromLibraryCommand, vm.ConvertTracksCommand, vm.ScanReplayGainCommand,
            vm.StartRadioCommand, vm.SnoozeForMonthCommand,
        };
        var reachable = Reachable(menu.Items);
        foreach (var command in commands)
            Assert.Contains(command, reachable);
        AssertParameters(menu, track, commands, vm.ShuffleFolderCommand);
        Assert.Same(vm.RemoveFromLibraryCommand, LastVisible(menu).Command);
        Assert.Contains("danger", LastVisible(menu).Classes);
        AssertSeparatorsClean(menu.Items);
        menu.Close();
        win.Close();
    }

    private static FolderContextMenuBuilder FolderBuilder(LibraryFoldersView view)
        => (FolderContextMenuBuilder)typeof(LibraryFoldersView)
            .GetField("_folderMenuBuilder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

    [AvaloniaFact]
    public async Task FoldersPage_FolderRightClick_OpensTheV2Menu_AndFollowsALanguageSwitch()
    {
        var (vm, view, win, _) = await MountFolders();
        try
        {
            await PumpUntil(() => view.FolderTree.GetVisualDescendants().OfType<TreeViewItem>().Any(t => t.DataContext is FolderNode));
            var node = view.FolderTree.GetVisualDescendants().OfType<TreeViewItem>().First(t => t.DataContext is FolderNode);
            var folder = (FolderNode)node.DataContext!;
            RightClick(node);

            var b = FolderBuilder(view);
            var menu = b.Menu;
            Assert.True(menu.IsOpen);
            Assert.Contains(MenuV2.MenuClass, menu.Classes);
            // Play / Shuffle / Play Next / Add to Queue are the quick tiles, on the clicked folder.
            var tiles = new ICommand[] { vm.PlayNodeCommand, vm.ShuffleNodeCommand, vm.PlayNodeNextCommand, vm.AddNodeToQueueCommand };
            Assert.Equal(tiles, Tiles(menu).Select(t => t.Command!).ToArray());
            var rows = new ICommand[] { vm.AddNodeToNewPlaylistCommand, vm.ShowNodeInExplorerCommand, vm.ToggleNodeHiddenCommand };
            var reachable = Reachable(menu.Items);
            foreach (var command in rows)
                Assert.Contains(command, reachable);
            AssertParameters(menu, folder, rows.Concat(tiles));
            Assert.Same(b.ToggleHidden, LastVisible(menu));
            Assert.Equal(Loc.T("LibraryFolders.HideFromLibrary"), b.ToggleHidden.Header);
            Assert.Same(MenuV2.FindGeometry(view, "MenuLineEyeOff"), MenuV2.IconPath(b.ToggleHidden.Icon)?.Data);
            Assert.NotNull(MenuV2.IconPath(b.ToggleHidden.Icon)?.Data);
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            Loc.Instance.SetCulture("es");
            RightClick(node);
            var rebuilt = FolderBuilder(view);
            Assert.NotSame(b, rebuilt);
            Assert.True(rebuilt.Menu.IsOpen);
            Assert.Equal(Loc.T("LibraryAlbums.ShowFolder"), rebuilt.ShowFolder.Header);
            Assert.Equal(Loc.T("LibraryAlbums.AddQueue"), TileName(rebuilt.QuickAddToQueue));
            rebuilt.Menu.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            win.Close();
        }
    }

    // ── Playlist page ──

    private static async Task<(PlaylistViewModel Vm, PlaylistView View, Window Win, ListBoxItem Row, Track Track)> MountPlaylist(bool smart)
    {
        EnsureAppStyles();
        var track = T("Hello");
        track.Rating = 5;
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = smart
            ? new Playlist
            {
                Id = Guid.NewGuid(), Name = "Top rated", IsSmartPlaylist = true, MatchAll = true,
                Rules = [new SmartPlaylistRule { Field = RuleField.Rating, Operator = RuleOperator.GreaterThan, Value = "3" }],
            }
            : new Playlist { Id = Guid.NewGuid(), Name = "P", TrackIds = new() { track.Id } };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        var list = view.FindControl<ListBox>("TrackList")!;
        await PumpUntil(() => list.GetRealizedContainers().Any(c => c.DataContext == track));
        var row = list.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == track);
        return (vm, view, win, row, track);
    }

    [AvaloniaFact]
    public async Task PlaylistPage_RightClick_OpensTheV2MenuWithEveryCommand_RemoveIsPlain()
    {
        var (vm, _, win, row, track) = await MountPlaylist(smart: false);
        RightClick(row);

        var menu = row.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.Contains(MenuV2.MenuClass, menu.Classes);
        var commands = new ICommand[]
        {
            vm.PlayFromCommand, vm.ShuffleAllCommand, vm.PlayNextCommand, vm.AddToQueueCommand,
            vm.AddToNewPlaylistCommand, vm.ToggleFavoriteCommand, vm.OpenMetadataCommand, vm.SearchLyricsCommand,
            vm.ShowInExplorerCommand, vm.RemoveTrackCommand, vm.ConvertTracksCommand, vm.ScanReplayGainCommand,
            vm.StartRadioCommand, vm.SnoozeForMonthCommand, vm.RateTrackCommand, vm.FetchLyricsCommand,
            vm.OpenLyricsStudioCommand, vm.RemoveLyricsCommand, vm.SendToFolderCommand, vm.SetBadgeCommand,
        };
        var reachable = Reachable(menu.Items);
        foreach (var command in commands)
            Assert.Contains(command, reachable);
        AssertParameters(menu, track, commands.Except(new ICommand[] { vm.RateTrackCommand, vm.SetBadgeCommand }), vm.ShuffleAllCommand);
        // Badge ▸ entries carry the row's track.
        var badgeRows = menu.GetLogicalDescendants().OfType<MenuItem>().Where(i => i.Command == vm.SetBadgeCommand).ToList();
        Assert.NotEmpty(badgeRows);
        Assert.All(badgeRows, i => Assert.Same(track, ((BadgeRequest)i.CommandParameter!).Track));

        // Remove only takes the song out of this playlist: last row, not red, its own icon.
        var remove = LastVisible(menu);
        Assert.Same(vm.RemoveTrackCommand, remove.Command);
        Assert.DoesNotContain("danger", remove.Classes);
        Assert.Equal(Loc.T("Playlist.Remove"), remove.Header);
        Assert.NotNull(MenuV2.IconPath(remove.Icon)?.Data);
        Assert.Same(MenuV2.FindGeometry(remove, "MenuLinePlaylistRemove"), MenuV2.IconPath(remove.Icon)?.Data);
        AssertSeparatorsClean(menu.Items);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public async Task SmartPlaylist_RightClick_HidesRemove_WithoutAStraySeparator()
    {
        var (vm, _, win, row, _) = await MountPlaylist(smart: true);
        RightClick(row);

        var menu = row.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.DoesNotContain(vm.RemoveTrackCommand, Reachable(menu.Items));
        AssertSeparatorsClean(menu.Items);
        menu.Close();
        win.Close();
    }

    /// <summary>
    /// Why PlaylistView settles the separators itself after hiding Remove: the views open the
    /// menu with ContextMenu.Open(control), which does not raise Opening, so the builder's
    /// Opening hook (RefreshLayout) never runs for them.
    /// </summary>
    [AvaloniaFact]
    public void OpenFromCode_DoesNotRaiseOpening()
    {
        EnsureAppStyles();
        var menu = new ContextMenu { Items = { new MenuItem { Header = "x" } } };
        var opening = 0;
        menu.Opening += (_, _) => opening++;
        var owner = new Border { Width = 50, Height = 50 };
        var win = new Window { Width = 400, Height = 300, Content = owner };
        win.Show();
        owner.ContextMenu = menu;
        menu.Open(owner);
        Pump();
        Assert.True(menu.IsOpen);
        Assert.Equal(0, opening);
        menu.Close();
        win.Close();
    }

    private static Button OptionsPill(Control view)
        => view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("glass-pill") && b.Flyout is MenuFlyout { Items.Count: 7 });

    private static Button SortPill(Control view)
        => view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("glass-pill") && b.Flyout is MenuFlyout { Items.Count: 11 });

    [AvaloniaFact]
    public async Task PlaylistPage_HeaderPills_OpenTheirMenus_WithTheOldCommands()
    {
        var (vm, view, win, _, _) = await MountPlaylist(smart: false);
        var options = OptionsPill(view);
        var sort = SortPill(view);

        var menu = (MenuFlyout)options.Flyout!;
        menu.ShowAt(options);
        Pump();
        Assert.True(menu.IsOpen);
        var reachable = Reachable(menu.Items);
        foreach (var command in new ICommand[] { vm.PlayNextAllCommand, vm.AddAllToQueueCommand, vm.AddSongsCommand, vm.EditPlaylistCommand, vm.DeletePlaylistCommand })
            Assert.Contains(command, reachable);
        var last = menu.Items.OfType<MenuItem>().Last(i => i.IsVisible);
        Assert.Same(vm.DeletePlaylistCommand, last.Command);
        Assert.Contains("danger", last.Classes);
        Assert.All(menu.Items.OfType<MenuItem>(), i => Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data));
        AssertSeparatorsClean(menu.Items);
        menu.Hide();
        Pump();

        var sortMenu = (MenuFlyout)sort.Flyout!;
        sortMenu.ShowAt(sort);
        Pump();
        Assert.True(sortMenu.IsOpen);
        Assert.Equal(new[] { "Manual", "Title", "Artist", "Album", "Duration", "RecentlyAdded", "ReleaseDateOldest",
                "ReleaseDateNewest", "DateModifiedNewest", "DateModifiedOldest", "Badge" },
            sortMenu.Items.OfType<MenuItem>().Select(i => { Assert.Same(vm.SetSortCommand, i.Command); return (string)i.CommandParameter!; }));
        sortMenu.Hide();
        win.Close();
    }

    /// <summary>Review 10-09: as a ContextMenu opened from Click, a second click on "…" closed
    /// the menu on the press and the click then reopened it. A flyout toggles closed.</summary>
    [AvaloniaFact]
    public async Task PlaylistPage_OptionsPill_SecondClickClosesTheMenu()
    {
        var (_, view, win, _, _) = await MountPlaylist(smart: false);
        var options = OptionsPill(view);
        var menu = (MenuFlyout)options.Flyout!;
        var centre = options.TranslatePoint(new Point(options.Bounds.Width / 2, options.Bounds.Height / 2), win)!.Value;

        void Click()
        {
            win.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            Pump();
            win.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
            Pump();
        }

        Click();
        Assert.True(menu.IsOpen);
        await Task.Delay(400);
        Click();
        await Task.Delay(200); // the close fade
        Pump();
        Assert.False(menu.IsOpen);
        win.Close();
    }

    [AvaloniaFact]
    public async Task SmartPlaylist_OptionsMenu_HidesAddSongs_WithoutAStraySeparator()
    {
        var (vm, view, win, _, _) = await MountPlaylist(smart: true);
        var options = OptionsPill(view);
        var menu = (MenuFlyout)options.Flyout!;
        menu.ShowAt(options);
        Pump();
        Assert.True(menu.IsOpen);
        Assert.DoesNotContain(vm.AddSongsCommand, Reachable(menu.Items));
        Assert.Contains(vm.EditPlaylistCommand, Reachable(menu.Items));
        AssertSeparatorsClean(menu.Items);
        menu.Hide();
        win.Close();
    }

    // ── Playlists grid ──

    private static (LibraryPlaylistsViewModel Vm, LibraryPlaylistsView View, Window Win, List<PlaylistNavItem> Items) MountPlaylists()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var vm = new LibraryPlaylistsViewModel(sidebar, player, lib, persistence);
        var items = new List<PlaylistNavItem>
        {
            new() { Key = "playlist:0", Label = "Road Trip", PlaylistId = Guid.NewGuid() },
            new() { Key = "playlist:1", Label = "Gym", PlaylistId = Guid.NewGuid(), IsPinned = true },
        };
        foreach (var item in items) sidebar.PlaylistItems.Add(item);
        var view = new LibraryPlaylistsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        return (vm, view, win, items);
    }

    private static Button TileFor(Control view, object item)
        => view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("album-tile") && b.DataContext == item);

    [AvaloniaFact]
    public void PlaylistsPage_RightClick_OpensTheV2MenuWithEveryCommand()
    {
        var (vm, view, win, items) = MountPlaylists();
        var tile = TileFor(view, items[0]);
        RightClick(tile);

        var menu = tile.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.Contains(MenuV2.MenuClass, menu.Classes);
        var tiles = new ICommand[] { vm.PlayPlaylistCommand, vm.ShufflePlaylistCommand, vm.PlayNextPlaylistCommand, vm.AddPlaylistToQueueCommand };
        Assert.Equal(tiles, Tiles(menu).Select(t => t.Command!).ToArray());
        var rows = new ICommand[] { vm.OpenPlaylistCommand, vm.EditPlaylistCommand, vm.ExportPlaylistCommand, vm.TogglePinCommand, vm.DeletePlaylistCommand };
        var reachable = Reachable(menu.Items);
        foreach (var command in rows)
            Assert.Contains(command, reachable);
        AssertParameters(menu, items[0], rows.Concat(tiles));
        // Not pinned: Star in Sidebar, not Unstar.
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => i.IsVisible && i.Header as string == Loc.T("LibraryPlaylists.StarSidebar"));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => i.IsVisible && i.Header as string == Loc.T("LibraryPlaylists.UnstarFromSidebar"));
        Assert.Same(vm.DeletePlaylistCommand, LastVisible(menu).Command);
        Assert.Contains("danger", LastVisible(menu).Classes);
        Assert.All(menu.Items.OfType<MenuItem>().Where(i => !i.Classes.Contains(MenuV2.PanelClass)),
            i => Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data));
        AssertSeparatorsClean(menu.Items);
        menu.Close();

        // The dots button of the pinned playlist: the same menu, rebound to that tile.
        var pinned = TileFor(view, items[1]);
        var dots = pinned.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tile-more"));
        dots.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Same(menu, pinned.ContextMenu);
        Assert.Null(tile.ContextMenu);
        Assert.True(menu.IsOpen);
        AssertParameters(menu, items[1], rows.Concat(tiles));
        Assert.Contains(menu.Items.OfType<MenuItem>(), i => i.IsVisible && i.Header as string == Loc.T("LibraryPlaylists.UnstarFromSidebar"));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => i.IsVisible && i.Header as string == Loc.T("LibraryPlaylists.StarSidebar"));
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void PlaylistsPage_LanguageSwitch_RebuildsTheMenuOnTheNextOpen()
    {
        var (_, view, win, items) = MountPlaylists();
        try
        {
            var tile = TileFor(view, items[0]);
            RightClick(tile);
            var english = tile.ContextMenu!;
            english.Close();

            Loc.Instance.SetCulture("es");
            RightClick(tile);
            var spanish = tile.ContextMenu!;
            Assert.NotSame(english, spanish);
            Assert.Equal(Loc.T("LibraryPlaylists.DeletePlaylist"), LastVisible(spanish).Header);
            Assert.Contains(Loc.T("LibraryPlaylists.AddQueue"), Tiles(spanish).Select(TileName));
            spanish.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            win.Close();
        }
    }

    // ── Artists grid ──

    private static (LibraryArtistsViewModel Vm, LibraryArtistsView View, Window Win, List<Artist> Artists) MountArtists()
    {
        EnsureAppStyles();
        var vm = new LibraryArtistsViewModel(new FakeLibraryService());
        var row = new ArtistRow();
        var artists = new List<Artist>
        {
            new() { Id = Guid.NewGuid(), Name = "Adele" },
            new() { Id = Guid.NewGuid(), Name = "Drake", IsFavorite = true, ImagePath = TestPaths.Primary("img", "drake.jpg") },
        };
        foreach (var a in artists) row.Artists.Add(a);
        vm.ArtistRows.ReplaceAll(new[] { row });
        var view = new LibraryArtistsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        for (var i = 0; i < 40 && view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("artist-tile")) < 2; i++)
        {
            Pump(1);
            win.UpdateLayout();
        }
        return (vm, view, win, artists);
    }

    private static Button ArtistTile(Control view, Artist artist)
        => view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("artist-tile") && b.DataContext == artist);

    [AvaloniaFact]
    public void ArtistsPage_RightClick_OpensTheV2Menu_WithEveryEntryOnTheClickedArtist()
    {
        var (_, view, win, artists) = MountArtists();
        try
        {
            var plain = ArtistTile(view, artists[0]);
            RightClick(plain);
            var menu = plain.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.Contains(MenuV2.MenuClass, menu.Classes);

            string?[] Visible() => menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string).ToArray();
            // Not a favourite, no picture: Set as Favorite, Choose, Find; no Remove Picture.
            Assert.Equal(new[] { Loc.T("LibraryArtists.SetAsFavorite"), Loc.T("LibraryArtists.ChooseFromFile"), Loc.T("LibraryArtists.FindPictureOnline") }, Visible());
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.IsVisible), i =>
            {
                Assert.NotNull(i.Command);
                Assert.Same(artists[0], i.CommandParameter);
                Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data);
            });
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            // Favourite with a picture: Remove from Favorites (filled heart), and Remove Picture last in red.
            var drake = ArtistTile(view, artists[1]);
            RightClick(drake);
            Assert.Same(menu, drake.ContextMenu);
            Assert.Null(plain.ContextMenu);
            Assert.Equal(new[] { Loc.T("LibraryArtists.RemoveFromFavorites"), Loc.T("LibraryArtists.ChooseFromFile"),
                Loc.T("LibraryArtists.FindPictureOnline"), Loc.T("LibraryArtists.RemovePicture") }, Visible());
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.IsVisible), i => Assert.Same(artists[1], i.CommandParameter));
            Assert.Contains("mv2-fav", MenuV2.IconPath(menu.Items.OfType<MenuItem>().First(i => i.IsVisible).Icon)!.Classes);
            Assert.Contains("danger", LastVisible(menu).Classes);
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            Loc.Instance.SetCulture("es");
            RightClick(drake);
            Assert.NotSame(menu, drake.ContextMenu);
            Assert.Equal(Loc.T("LibraryArtists.RemovePicture"), LastVisible(drake.ContextMenu!).Header);
            drake.ContextMenu!.Close();
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            win.Close();
        }
    }

    // ── Sidebar ──

    [AvaloniaFact]
    public void Sidebar_PlaylistAndFolderMenus_AreV2_WithTheOldCommands()
    {
        EnsureAppStyles();
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService()) { TopBar = new TopBarViewModel() };
        var folder = new PlaylistNavItem { Label = "Mixes", IsFolder = true, Folder = "Mixes" };
        var loose = new PlaylistNavItem { Label = "Road Trip", PlaylistId = Guid.NewGuid() };
        var filed = new PlaylistNavItem { Label = "Gym", PlaylistId = Guid.NewGuid(), Folder = "Mixes", IsPinned = true };
        vm.SidebarRows.Add(folder);
        vm.SidebarRows.Add(loose);
        vm.SidebarRows.Add(filed);
        var view = new SidebarView { DataContext = vm };
        var win = new Window { Width = 300, Height = 900, Content = view };
        win.Show();
        Pump();
        try
        {
            var list = view.FindControl<ListBox>("PlaylistList")!;
            ContextMenu Open(PlaylistNavItem item)
            {
                var row = list.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == item);
                var m = row.ContextMenu!;
                m.DataContext = item;
                m.Open(row);
                Pump();
                return m;
            }
            string?[] Visible(ContextMenu m) => m.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string).ToArray();

            var menu = Open(loose);
            Assert.Contains(MenuV2.MenuClass, menu.Classes);
            Assert.Equal(new[] { Loc.T("Sidebar.EditPlaylist"), Loc.T("Sidebar.StarSidebar"), Loc.T("Sidebar.MoveFolder"), Loc.T("Sidebar.DeletePlaylist") }, Visible(menu));
            var reachable = Reachable(menu.Items);
            foreach (var command in new ICommand[] { vm.EditPlaylistItemCommand, vm.TogglePinItemCommand, vm.MoveToFolderCommand, vm.DeletePlaylistItemCommand })
                Assert.Contains(command, reachable);
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.IsVisible), i =>
            {
                Assert.Same(loose, i.CommandParameter);
                Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data);
            });
            Assert.Contains("danger", LastVisible(menu).Classes);
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            menu = Open(filed);
            Assert.Equal(new[] { Loc.T("Sidebar.EditPlaylist"), Loc.T("Sidebar.UnstarFromSidebar"), Loc.T("Sidebar.MoveFolder"),
                Loc.T("Sidebar.RemoveFromFolder"), Loc.T("Sidebar.DeletePlaylist") }, Visible(menu));
            Assert.Contains(vm.RemoveFromFolderCommand, Reachable(menu.Items));
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.IsVisible), i => Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data));
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            menu = Open(folder);
            Assert.Equal(new[] { Loc.T("Sidebar.NewPlaylistFolder"), Loc.T("Sidebar.RenameFolder"), Loc.T("Sidebar.RemoveFolderKeepPlaylists") }, Visible(menu));
            reachable = Reachable(menu.Items);
            foreach (var command in new ICommand[] { vm.NewPlaylistInFolderCommand, vm.RenameFolderCommand, vm.DissolveFolderCommand })
                Assert.Contains(command, reachable);
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => i.IsVisible), i => Assert.Same(folder, i.CommandParameter));
            Assert.Contains("danger", LastVisible(menu).Classes);
            AssertSeparatorsClean(menu.Items);
            menu.Close();

            // The PLAYLISTS header: New Playlist / New Smart Playlist.
            var header = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.ContextMenu is { Items.Count: 2 });
            Assert.Contains(MenuV2.MenuClass, header.ContextMenu!.Classes);
            header.ContextMenu.Open(header);
            Pump();
            Assert.Equal(new ICommand[] { vm.CreatePlaylistCommand, vm.CreateSmartPlaylistFromSidebarCommand },
                header.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Command!).ToArray());
            Assert.All(header.ContextMenu.Items.OfType<MenuItem>(), i => Assert.NotNull(MenuV2.IconPath(i.Icon)?.Data));
            Assert.Same(MenuV2.FindGeometry(view, "MenuLineSmartPlaylist"), MenuV2.IconPath(header.ContextMenu.Items.OfType<MenuItem>().Last().Icon)!.Data);
            header.ContextMenu.Close();
        }
        finally
        {
            win.Close();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// "Segoe Fluent Icons" / "Segoe MDL2 Assets" ship with Windows only: a glyph drawn in
    /// them is a box or nothing on macOS and Linux. Icons must be vector geometry.
    /// </summary>
    [Fact]
    public void NoView_DrawsAGlyphWithAWindowsOnlySegoeIconFont()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Text, "FontFamily=\"[^\"]*Segoe (Fluent Icons|MDL2 Assets)"))
            .Select(x => $"{Path.GetFileName(x.File)}:{x.Line}")
            .ToList();
        Assert.True(offenders.Count == 0, "Windows-only icon font in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The playlist row's # cell turns into a drag grip on hover when the list can be
    /// reordered. Both the number and the grip used to set Opacity as a local value, which
    /// outranks the :pointerover style, so the swap never happened.
    /// </summary>
    [AvaloniaFact]
    public async Task PlaylistRow_Hover_SwapsTheNumberForTheVectorGrip()
    {
        var (vm, _, win, row, _) = await MountPlaylist(smart: false);
        try
        {
            Assert.True(vm.CanReorder);
            var grip = row.GetVisualDescendants().OfType<Noctis.Controls.LineIcon>().Single(i => i.Classes.Contains("row-grip"));
            var index = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("row-index"));
            Assert.Same(Application.Current!.FindResource("LineGrip"), grip.Data);
            Assert.Equal(0, grip.Opacity);

            var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), win)!.Value;
            win.MouseMove(centre, Avalonia.Input.RawInputModifiers.None);
            var deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline && Math.Abs(grip.Opacity - 0.55) > 0.01)
            {
                Pump(1);
                await Task.Delay(10);
            }
            var body = row.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-body"));
            Assert.Contains("reorderable", body.Classes);
            Assert.Contains(":pointerover", body.Classes);
            Assert.Equal(0.55, grip.Opacity, 2);
            Assert.Equal(0, index.Opacity, 2);
        }
        finally
        {
            win.Close();
        }
    }
}
