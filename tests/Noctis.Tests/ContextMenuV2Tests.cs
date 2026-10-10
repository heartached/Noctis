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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The v2 context menu (10-09 redesign): opt-in per view through Build(..., v2: true). Only the
/// album page track rows and the Albums page tiles use it; every command of the classic menu
/// must still be reachable, optional entries must still hide when a view doesn't wire them,
/// and the quick tiles / rating stars must run the same commands with the same parameters.
/// </summary>
public class ContextMenuV2Tests
{
    private readonly ITestOutputHelper _o;
    public ContextMenuV2Tests(ITestOutputHelper o) => _o = o;

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

    /// <summary>One distinct command per role, so the menus can be compared role by role.</summary>
    private sealed class TrackCommands
    {
        public readonly Dictionary<string, ICommand> All = new();
        public readonly List<(string Role, object? Parameter)> Ran = new();
        public ICommand this[string role] => All[role];

        public TrackCommands(params string[] roles)
        {
            foreach (var role in roles)
                All[role] = new RelayCommand<object?>(p => Ran.Add((role, p)));
        }
    }

    private static readonly string[] Required =
        { "play", "shuffle", "next", "queue", "playlist", "fav", "meta", "lyrics", "folder", "remove" };
    private static readonly string[] Optional =
        { "convert", "rg", "radio", "snooze", "rate", "fetch", "studio", "rmlyrics", "send", "badge", "album", "artist" };

    private static Track T(string artist = "Kanye West, GLC", int rating = 0, bool fav = false) => new()
    {
        Id = Guid.NewGuid(), Title = "Spaceship", Artist = artist, AlbumArtist = "Kanye West", Album = "The College Dropout",
        AlbumId = Track.ComputeAlbumId("Kanye West", "The College Dropout"), Rating = rating, IsFavorite = fav,
        FilePath = "C:/m/spaceship.flac",
    };

    private static TrackContextMenuBuilder Build(bool v2)
    {
        EnsureAppStyles();
        var b = new TrackContextMenuBuilder();
        b.Build("Remove from Library", null, Host(), v2: v2);
        return b;
    }

    /// <summary>An unattached host resolves only its own resources (the classic build reads
    /// three geometry icons through it); v2 falls back to the application on its own.</summary>
    private static Border Host()
    {
        var host = new Border();
        foreach (var key in new[] { "HeartFillIcon", "StarIcon", "TrashIcon" })
            host.Resources[key] = Geometry.Parse("M0 0L1 1");
        return host;
    }

    private static void BindAll(TrackContextMenuBuilder b, Track track, TrackCommands c, bool optional)
        => b.Bind(track, c["play"], c["shuffle"], c["next"], c["queue"], c["playlist"], c["fav"], c["meta"],
            c["lyrics"], c["folder"], c["remove"],
            convertCommand: optional ? c["convert"] : null,
            scanReplayGainCommand: optional ? c["rg"] : null,
            startRadioCommand: optional ? c["radio"] : null,
            snoozeCommand: optional ? c["snooze"] : null,
            rateCommand: optional ? c["rate"] : null,
            fetchLyricsCommand: optional ? c["fetch"] : null,
            lyricsStudioCommand: optional ? c["studio"] : null,
            removeLyricsCommand: optional ? c["rmlyrics"] : null,
            sendToFolderCommand: optional ? c["send"] : null,
            badgeCommand: optional ? c["badge"] : null,
            badgeNames: optional ? new[] { "Live" } : null,
            viewAlbumCommand: optional ? c["album"] : null,
            viewArtistCommand: optional ? c["artist"] : null);

    /// <summary>Every command a user can reach: visible menu items (recursively) and the
    /// visible buttons in v2 panels (tiles, stars).</summary>
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

    // ── Track menu ──

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrackV2_ReachesEveryCommandOfTheClassicMenu(bool optional)
    {
        var classic = Build(v2: false);
        var v2 = Build(v2: true);
        var track = T(rating: 3);
        var c = new TrackCommands(Required.Concat(Optional).ToArray());
        BindAll(classic, track, c, optional);
        BindAll(v2, track, c, optional);

        var before = Reachable(classic.Menu.Items);
        var after = Reachable(v2.Menu.Items);
        // Open With is a per-builder RelayCommand; compare it by presence instead.
        before.Remove(classic.OpenWith.Command!);
        after.Remove(v2.OpenWith.Command!);
        Assert.Equal(classic.OpenWith.IsVisible, v2.OpenWith.IsVisible);
        Assert.Empty(before.Except(after));
        Assert.Empty(after.Except(before));
        Assert.Contains(SpectrogramLauncher.OpenCommand, after);
        Assert.Contains(LyricsBackgroundOverrides.ChooseForTrackCommand, after);
        if (optional)
            foreach (var role in Optional)
                Assert.Contains(c[role], after);
    }

    [AvaloniaFact]
    public void TrackV2_OptionalItemsHide_WhenTheViewWiresNothing()
    {
        var b = Build(v2: true);
        BindAll(b, T(), new TrackCommands(Required.Concat(Optional).ToArray()), optional: false);

        foreach (var item in new[] { b.StartRadio, b.SnoozeForMonth, b.Convert, b.ScanReplayGain, b.SendToFolder,
                     b.Rate, b.Badge, b.ViewAlbum, b.ViewArtist, b.FetchLyrics, b.LyricsStudio, b.RemoveLyrics, b.DontScrobble })
            Assert.False(item.IsVisible, $"{item.Header} should hide when unwired");

        // Spectrogram needs no wiring, so Tools ▸ stays; the radio/snooze group is gone with its line.
        Assert.True(b.Tools.IsVisible);
        Assert.True(b.Spectrogram.IsVisible);
        AssertSeparatorsClean(b.Menu.Items);
    }

    [AvaloniaFact]
    public void TrackV2_Submenu_SlidesInFromTheParentMenu()
    {
        // Owner 10-09: Tools ▸ / Lyrics ▸ used to pop in with no motion. The submenu card
        // now runs the menu open animation, sliding in sideways (not rising like the menu).
        var b = Build(v2: true);
        BindAll(b, T(), new TrackCommands(Required.Concat(Optional).ToArray()), optional: true);
        var owner = new Border { Width = 50, Height = 50 };
        var win = new Window { Width = 1200, Height = 1000, Content = owner };
        win.Show();
        owner.ContextMenu = b.Menu;
        b.Menu.Open(owner);
        Dispatcher.UIThread.RunJobs();

        b.Tools.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        var card = b.Tools.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First().Child as Border;
        Assert.NotNull(card);
        Assert.True(MenuOpenAnimation.GetEnable(card!));
        Assert.Equal(-8, MenuOpenAnimation.GetOffsetX(card!));
        Assert.Contains(card!.Transitions!, t => t is Avalonia.Animation.TransformOperationsTransition);
        Assert.Contains(card.Transitions!, t => t is Avalonia.Animation.DoubleTransition d && d.Property == Visual.OpacityProperty);

        b.Menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void TrackV2_GroupsAndToolsSubmenu()
    {
        var b = Build(v2: true);
        var c = new TrackCommands(Required.Concat(Optional).ToArray());
        BindAll(b, T(), c, optional: true);

        Assert.Contains("v2", b.Menu.Classes);
        // No title header (owner 10-09): the quick tiles are the first row.
        var first = Assert.IsType<MenuItem>(b.Menu.Items[0]);
        Assert.Contains(b.QuickPlay, ((Control)first.Header!).GetLogicalDescendants());
        Assert.Equal(new object[] { b.Convert, b.ScanReplayGain, b.Spectrogram, b.SendToFolder, b.OpenWith, b.DontScrobble },
            b.Tools.Items.Cast<object>().ToArray());
        // The four playback rows became tiles: none of them is a list row any more.
        Assert.DoesNotContain(b.Play, b.Menu.Items);
        Assert.DoesNotContain(b.AddToQueue, b.Menu.Items);
        Assert.Same(b.Remove, b.Menu.Items[^1]);
        Assert.Contains("danger", b.Remove.Classes);
        // Several credited artists: View Artist is a submenu, one entry each.
        Assert.Equal(new[] { "Kanye West", "GLC" }, b.ViewArtist.Items.OfType<MenuItem>().Select(i => i.Header as string));
        AssertSeparatorsClean(b.Menu.Items);
        // Distinct line icons for the rows that used to share one.
        var icons = new[] { b.Metadata, b.Convert, b.ScanReplayGain, b.Spectrogram, b.OpenWith, b.StartRadio, b.SnoozeForMonth }
            .Select(i => MenuV2.IconPath(i.Icon)?.Data).ToList();
        Assert.All(icons, Assert.NotNull);
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    [AvaloniaFact]
    public void TrackV2_FavoriteToggle_ShowsOneRow_FilledHeartWhenFavorited()
    {
        var b = Build(v2: true);
        var c = new TrackCommands(Required.Concat(Optional).ToArray());
        BindAll(b, T(fav: true), c, optional: false);
        Assert.False(b.Favorite.IsVisible);
        Assert.True(b.Unfavorite.IsVisible);
        Assert.Contains("mv2-fav", MenuV2.IconPath(b.Unfavorite.Icon)!.Classes);

        BindAll(b, T(fav: false), c, optional: false);
        Assert.True(b.Favorite.IsVisible);
        Assert.False(b.Unfavorite.IsVisible);
    }

    [AvaloniaFact]
    public void QuickTiles_RunTheSameCommandsWithTheRowParameter()
    {
        var b = Build(v2: true);
        var track = T();
        var c = new TrackCommands(Required.Concat(Optional).ToArray());
        BindAll(b, track, c, optional: false);

        foreach (var tile in new[] { b.QuickPlay, b.QuickShuffle, b.QuickPlayNext, b.QuickAddToQueue })
            tile.Command!.Execute(tile.CommandParameter);

        // Shuffle never carried a parameter in the classic menu either.
        Assert.Equal(new (string, object?)[] { ("play", track), ("shuffle", null), ("next", track), ("queue", track) }, c.Ran);
    }

    [AvaloniaFact]
    public async Task QuickTileClick_ClosesTheMenu()
    {
        var b = Build(v2: true);
        BindAll(b, T(), new TrackCommands(Required.Concat(Optional).ToArray()), optional: false);
        var owner = new Border { Width = 100, Height = 100 };
        var win = new Window { Width = 400, Height = 300, Content = owner };
        win.Show();
        owner.ContextMenu = b.Menu;
        b.Menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.Menu.IsOpen);

        b.QuickPlayNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Menus close with a 150 ms shrink + fade (MenuOpenAnimation.Pop), inert while it runs;
        // a yielding pump lets its DispatcherTimer finish the close.
        Assert.False(b.Menu.IsHitTestVisible);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (b.Menu.IsOpen && sw.ElapsedMilliseconds < 2000) { Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
        Assert.False(b.Menu.IsOpen);
        win.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(4)]
    public void RatingStars_SendRateRequestWithTheirCount(int current)
    {
        var b = Build(v2: true);
        var track = T(rating: current);
        var c = new TrackCommands(Required.Concat(Optional).ToArray());
        BindAll(b, track, c, optional: true);

        var stars = b.Rating!.Buttons;
        Assert.True(b.Rate.IsVisible);
        Assert.Equal(current > 0, stars[0].IsVisible); // clear only when rated
        for (var i = 1; i <= 5; i++)
            Assert.Equal(i <= current, stars[i].Classes.Contains("lit"));

        for (var i = 0; i <= 5; i++)
            stars[i].Command!.Execute(stars[i].CommandParameter);
        var sent = c.Ran.Where(r => r.Role == "rate").Select(r => (RateRequest)r.Parameter!).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, sent.Select(r => r.Stars));
        Assert.All(sent, r => Assert.Same(track, r.Track));
    }

    [AvaloniaFact]
    public void ClassicBuild_KeepsTheOriginalLayout()
    {
        var b = Build(v2: false);
        BindAll(b, T(), new TrackCommands(Required.Concat(Optional).ToArray()), optional: true);

        Assert.False(b.IsV2);
        Assert.DoesNotContain("v2", b.Menu.Classes);
        Assert.Same(b.Play, b.Menu.Items[0]);
        Assert.Equal("Play", b.Play.Header);
        Assert.Contains(b.Convert, b.Menu.Items); // top level, not under Tools
        Assert.DoesNotContain(b.Menu.Items.OfType<MenuItem>(), i => i.Classes.Contains(MenuV2.PanelClass));

        var album = new AlbumContextMenuBuilder();
        album.Build("Remove from Library", Host());
        Assert.DoesNotContain("v2", album.Menu.Classes);
        Assert.Same(album.Play, album.Menu.Items[0]);
    }

    [AvaloniaFact]
    public void TrackV2_ArrowKeys_SkipHeaderAndTiles()
    {
        var b = Build(v2: true);
        BindAll(b, T(), new TrackCommands(Required.Concat(Optional).ToArray()), optional: true);
        var owner = new Border { Width = 100, Height = 100 };
        var win = new Window { Width = 600, Height = 900, Content = owner };
        win.Show();
        owner.ContextMenu = b.Menu;
        b.Menu.Open(owner);
        Dispatcher.UIThread.RunJobs();

        b.Menu.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = b.Menu });
        Dispatcher.UIThread.RunJobs();
        var selected = b.Menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsSelected)
            ?? TopLevel.GetTopLevel(b.Menu)?.FocusManager?.GetFocusedElement() as MenuItem;
        _o.WriteLine($"after Down: {selected?.Header}");
        Assert.NotNull(selected);
        Assert.DoesNotContain(MenuV2.PanelClass, selected!.Classes);
        Assert.Same(b.AddToPlaylist, selected);
        b.Menu.Close();
        win.Close();
    }

    // ── Album menu ──

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static (LibraryAlbumsViewModel Vm, LibraryAlbumsView View, Window Win, Album Album) MountAlbums()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var track = new Track { Id = Guid.NewGuid(), Title = "One", Artist = "Adele", AlbumArtist = "Adele", Album = "25", TrackNumber = 1 };
        var album = new Album { Id = Guid.NewGuid(), Name = "25", Artist = "Adele", TrackCount = 11, Tracks = new List<Track> { track } };
        vm.FilteredAlbumRows.Add(new AlbumRow { Albums = new List<Album> { album } });
        Dispatcher.UIThread.RunJobs();
        return (vm, view, win, album);
    }

    [AvaloniaFact]
    public void AlbumsPage_RightClick_OpensTheV2MenuWithEveryCommand()
    {
        var (vm, view, win, album) = MountAlbums();
        var tile = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("album-tile"));
        tile.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = tile });

        var menu = tile.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen);
        Assert.Contains("v2", menu.Classes);

        // Every command the old XAML menu had, plus the lyrics entries it lacked.
        var reachable = Reachable(menu.Items);
        foreach (var command in new ICommand[]
                 {
                     vm.PlayAlbumCommand, vm.ShuffleAlbumCommand, vm.PlayNextCommand, vm.AddToQueueCommand,
                     vm.AddToNewPlaylistCommand, vm.ToggleAlbumFavoritesCommand, vm.OpenMetadataCommand,
                     vm.ConvertAlbumCommand, vm.ScanAlbumReplayGainCommand, vm.ShowInExplorerCommand,
                     vm.RemoveFromLibraryCommand, vm.SearchLyricsAlbumCommand, LyricsBackgroundOverrides.ChooseForAlbumCommand,
                 })
            Assert.Contains(command, reachable);

        // Every row and tile acts on the right-clicked album.
        foreach (var item in menu.GetLogicalDescendants().OfType<MenuItem>().Where(i => i.Command != null && i.IsVisible))
            if (item.Command != ScrobbleMenu.ToggleCommand)
                Assert.Same(album, item.CommandParameter);
        foreach (var button in menu.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile")))
            Assert.Same(album, button.CommandParameter);

        // No title header (owner 10-09): nothing in the menu shows the album's name.
        Assert.DoesNotContain(menu.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "25");
        var remove = menu.Items.OfType<MenuItem>().Last();
        Assert.Contains("danger", remove.Classes);
        AssertSeparatorsClean(menu.Items);
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void AlbumsPage_DotsButton_OpensTheSameMenu_AndPushesTheSelection()
    {
        var (vm, view, win, album) = MountAlbums();
        var tile = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("album-tile"));
        var dots = tile.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tile-more"));
        vm.CtrlSelectedAlbums = new List<Album> { new() { Name = "stale" } };

        dots.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var menu = tile.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen);
        Assert.Contains("v2", menu.Classes);
        Assert.Empty(vm.CtrlSelectedAlbums); // no ctrl-selection: the stale list was replaced
        Assert.All(menu.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile")),
            b => Assert.Same(album, b.CommandParameter));
        menu.Close();
        win.Close();
    }

    [AvaloniaFact]
    public void AlbumV2_UnwiredOptionalItemsHide_ToolsHidesWhenEmpty()
    {
        EnsureAppStyles();
        var b = new AlbumContextMenuBuilder();
        b.Build("Remove from Library", new Border(), v2: true);
        var none = new RelayCommand<object?>(_ => { });
        var album = new Album { Id = Guid.NewGuid(), Name = "X", Artist = "Y", TrackCount = 1 };
        b.Bind(album, none, none, none, none, none, none, none, none, none);

        Assert.False(b.Convert.IsVisible);
        Assert.False(b.ScanReplayGain.IsVisible);
        Assert.False(b.SearchLyrics.IsVisible);
        Assert.False(b.EditDescription.IsVisible);
        Assert.False(b.Tools.IsVisible); // nothing left inside (scrobbling off in tests)
        var first = Assert.IsType<MenuItem>(b.Menu.Items[0]);
        Assert.Contains(b.QuickPlay, ((Control)first.Header!).GetLogicalDescendants());
        AssertSeparatorsClean(b.Menu.Items);
    }

    // ── Long translations (es "Reproducir a continuación", fr "Ajouter à la file d'attente") ──

    [AvaloniaTheory]
    [InlineData("es")]
    [InlineData("fr")]
    public void V2Menus_LongTranslations_StayInsideTheCard(string culture)
    {
        EnsureAppStyles();
        Noctis.Localization.Loc.Instance.SetCulture(culture);
        try
        {
            // Strings are read when the menu is built, so build under the culture.
            var track = Build(v2: true);
            BindAll(track, T(rating: 2), new TrackCommands(Required.Concat(Optional).ToArray()), optional: true);
            var album = new AlbumContextMenuBuilder();
            album.Build(Noctis.Localization.Loc.T("LibraryAlbums.RemoveFromLibrary"), Host(), v2: true, removeIsDanger: true);
            var none = new RelayCommand<object?>(_ => { });
            album.Bind(new Album { Id = Guid.NewGuid(), Name = "Un álbum con un título realmente muy largo", Artist = "Artista", TrackCount = 12 },
                none, none, none, none, none, none, none, none, none,
                convertCommand: none, scanReplayGainCommand: none, searchLyricsCommand: none);

            // Translated labels are in use, not English fallbacks.
            Assert.Equal(Noctis.Localization.Loc.T("LibraryAlbums.AddQueue"), TileLabel(track.QuickAddToQueue).Text);
            Assert.NotEqual("Add to Queue", TileLabel(track.QuickAddToQueue).Text);

            foreach (var menu in new[] { track.Menu, album.Menu })
            {
                var owner = new Border { Width = 50, Height = 50 };
                var win = new Window { Width = 1200, Height = 1000, Content = owner };
                win.Show();
                owner.ContextMenu = menu;
                menu.Open(owner);
                Dispatcher.UIThread.RunJobs();

                var card = menu.Bounds.Width;
                _o.WriteLine($"{culture}: card {card:0.#} (max {menu.MaxWidth})");
                Assert.InRange(card, 1, menu.MaxWidth + 0.5);

                foreach (var tile in menu.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile")))
                {
                    var label = TileLabel(tile);
                    var bottomRight = label.TranslatePoint(new Point(label.Bounds.Width, label.Bounds.Height), tile)!.Value;
                    var topLeft = label.TranslatePoint(new Point(0, 0), tile)!.Value;
                    _o.WriteLine($"  tile '{label.Text}' {label.Bounds.Width:0.#}x{label.Bounds.Height:0.#} in {tile.Bounds.Width:0.#}x{tile.Bounds.Height:0.#}");
                    Assert.True(topLeft.X >= -0.5 && bottomRight.X <= tile.Bounds.Width + 0.5, $"'{label.Text}' spills out of its tile");
                    Assert.True(bottomRight.Y <= tile.Bounds.Height + 0.5, $"'{label.Text}' is cut at the bottom of its tile");
                    Assert.True(label.DesiredSize.Width <= tile.Bounds.Width + 0.5);
                    // Two wrapped lines must hold the whole label (es "Reproducir a continuación"
                    // used to end as "Reproducir / …"). Needs real font metrics: the headless
                    // stand-in font is far wider than Inter.
                    if (HeadlessTestApp.RealRendering)
                        Assert.False(label.TextLayout.TextLines.Any(l => l.HasCollapsed), $"'{label.Text}' is cut short with an ellipsis");
                    // Icon + label sit in the middle of the tile: a one-line "Play" beside a
                    // two-line "Add to Queue" must not hug the top (owner 10-09).
                    var content = (Control)tile.Content!;
                    var above = content.TranslatePoint(new Point(0, 0), tile)!.Value.Y;
                    var below = tile.Bounds.Height - above - content.Bounds.Height;
                    Assert.True(Math.Abs(above - below) <= 1.5, $"'{label.Text}' is not centred: {above:0.#} above, {below:0.#} below");
                    var tileRight = tile.TranslatePoint(new Point(tile.Bounds.Width, 0), menu)!.Value.X;
                    Assert.True(tileRight <= card + 0.5, $"tile '{label.Text}' ends past the card");
                }

                foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.IsVisible && !i.Classes.Contains(MenuV2.PanelClass)))
                {
                    var right = item.TranslatePoint(new Point(item.Bounds.Width, 0), menu)!.Value.X;
                    Assert.True(right <= card + 0.5, $"row '{item.Header}' ends past the card");
                    var parts = item.GetVisualDescendants().OfType<Control>().ToList();
                    var header = parts.OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter");
                    var headerRight = header.TranslatePoint(new Point(header.Bounds.Width, 0), item)!.Value.X;
                    Assert.True(headerRight <= item.Bounds.Width + 0.5, $"row '{item.Header}' text runs past the row");
                    var chevron = parts.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault(p => p.Name == "PART_ChevronPath" && p.IsEffectivelyVisible);
                    if (chevron != null)
                    {
                        var chevronLeft = chevron.TranslatePoint(new Point(0, 0), item)!.Value.X;
                        var chevronRight = chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), item)!.Value.X;
                        Assert.True(chevronRight <= item.Bounds.Width + 0.5, $"chevron of '{item.Header}' pushed out");
                        Assert.True(headerRight <= chevronLeft + 0.5, $"'{item.Header}' overlaps its chevron");
                    }
                }

                menu.Close();
                win.Close();
            }
        }
        finally
        {
            Noctis.Localization.Loc.Instance.SetCulture("en");
        }
    }

    private static TextBlock TileLabel(Button tile)
        => tile.GetLogicalDescendants().OfType<TextBlock>().Single();

    // ── Eyeball probe (NOCTIS_TEST_SKIA=1): renders both menus + the icon sheet to PNGs ──

    [AvaloniaFact]
    public void Probe_RenderMenus()
    {
        if (!HeadlessTestApp.RealRendering) return;
        EnsureAppStyles();
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var track = Build(v2: true);
            BindAll(track, T(rating: 3, fav: true), new TrackCommands(Required.Concat(Optional).ToArray()), optional: true);
            var album = new AlbumContextMenuBuilder();
            album.Build("Remove from Library", new Border(), v2: true);
            var none = new RelayCommand<object?>(_ => { });
            album.Bind(new Album { Id = Guid.NewGuid(), Name = "25", Artist = "Adele", TrackCount = 11 },
                none, none, none, none, none, none, none, none, none, convertCommand: none, scanReplayGainCommand: none, searchLyricsCommand: none);

            var sheet = new WrapPanel { Width = 300, Margin = new Thickness(10) };
            foreach (var key in new[] { "MenuLinePlay", "MenuLineShuffle", "MenuLinePlayNext", "MenuLineQueue", "MenuLinePlaylistAdd",
                         "MenuLineHeart", "MenuLineStar", "MenuLineAlbum", "MenuLineArtist", "MenuLineRadio", "MenuLineSnooze",
                         "MenuLineEdit", "MenuLineLyrics", "MenuLineVideo", "MenuLineFolder", "MenuLineTools", "MenuLineConvert",
                         "MenuLineReplayGain", "MenuLineSpectrogram", "MenuLineSendToFolder", "MenuLineOpenWith",
                         "MenuLineScrobbleOff", "MenuLineBadge", "MenuLineTrash", "MenuLineClose" })
            {
                var big = MenuV2.LineIcon(sheet, key, 32);
                big.Margin = new Thickness(6);
                sheet.Children.Add(big);
                var small = MenuV2.LineIcon(sheet, key, 16);
                small.Margin = new Thickness(6);
                sheet.Children.Add(small);
            }

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, Margin = new Thickness(16) };
            row.Children.Add(track.Menu);
            row.Children.Add(album.Menu);
            row.Children.Add(sheet);
            var win = new Window
            {
                Width = 1000, Height = 900, Content = row,
                Background = (IBrush?)(Application.Current.TryGetResource("AppMainBackground", variant, out var bg) ? bg : null),
            };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"noctis-menu-v2-{variant}.png");
            win.CaptureRenderedFrame()!.Save(path);
            _o.WriteLine(path);
            win.Close();
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
