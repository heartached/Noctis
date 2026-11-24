using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23: (1) a Favorites tile could not be dragged at all (its DataContext is a
/// FavoriteItem, which the drag did not know); (2) dragging several selected songs onto a
/// sidebar playlist added only the one grabbed: the drag carried its own row only, and the
/// plain press that starts a drag cleared the selection first. A drag that starts on an item
/// of a multi-selection now carries the whole selection in list order; one that starts on an
/// unselected item carries just that item.
/// </summary>
public class DragCarriesSelectionTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
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
        Pump(4);
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Song(int i) => new()
    {
        Id = Guid.NewGuid(), Title = $"Song {i:00}", Artist = "Artist", Album = $"Single {i}", AlbumArtist = "Artist",
        FilePath = $"/music/{i}.mp3", Duration = TimeSpan.FromMinutes(3), IsFavorite = true, AlbumId = Guid.NewGuid(),
    };

    private static void Click(Window win, Point p, RawInputModifiers mods = RawInputModifiers.None)
    {
        win.MouseDown(p, MouseButton.Left, mods);
        win.MouseUp(p, MouseButton.Left, mods);
        Pump(2);
    }

    private static Point Centre(Control c, Visual to) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), to)!.Value;

    /// <summary>The control a drag from this tile / row starts on (the tile, or the row's content).</summary>
    private static Control DragSource(Control under) =>
        under.GetSelfAndVisualDescendants().OfType<Control>().First(DragFileBehavior.GetEnableFileDrag);

    // ── Favorites (item 1) ──

    [Fact]
    public void FavoriteItem_DragsItsSongOrItsAlbumsSongs()
    {
        var song = Song(1);
        Assert.Equal(new[] { song }, DragFileBehavior.GetTracks(new FavoriteItem { Track = song }));

        var a = Song(2); var b = Song(3);
        var album = new Album { Id = Guid.NewGuid(), Name = "LP", Artist = "Artist", Tracks = new List<Track> { a, b } };
        Assert.Equal(new[] { a, b }, DragFileBehavior.GetTracks(new FavoriteItem { Album = album }));

        // The chip shows the album as on the Albums page, the song as on a song row.
        var albumChip = DragFileBehavior.BuildPreview(new FavoriteItem { Album = album }, new[] { a, b });
        Assert.Equal("LP", albumChip!.Title);
        Assert.Equal(2, albumChip.Count);
        var songChip = DragFileBehavior.BuildPreview(new FavoriteItem { Track = song }, new[] { song });
        Assert.Equal("Song 01", songChip!.Title);
    }

    private static async Task<(FavoritesView view, Window win, List<Track> songs)> MountFavorites(int count)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var songs = Enumerable.Range(0, count).Select(Song).ToList();
        lib.TrackList.AddRange(songs);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new FavoritesViewModel(player, lib, persistence, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistory()));
        vm.Refresh();
        var view = new FavoritesView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        await PumpUntil(() => FavoriteTiles(view).Count > 0);
        return (view, win, songs);
    }

    /// <summary>The tiles in grid order.</summary>
    private static List<Button> FavoriteTiles(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible)
            .OrderBy(b => b.TranslatePoint(default, root)!.Value.Y).ThenBy(b => b.TranslatePoint(default, root)!.Value.X).ToList();

    private static List<Track>? DragFrom(Control tile) =>
        DragFileBehavior.TracksForDrag(DragSource(tile), DragFileBehavior.PayloadOf(DragSource(tile)));

    [AvaloniaFact]
    public async Task Favorites_ATileDrags_ItsSong()
    {
        var (view, win, _) = await MountFavorites(4);
        var tiles = FavoriteTiles(view);
        var item = (FavoriteItem)tiles[1].DataContext!;
        Assert.Equal(new[] { item.Track }, DragFrom(tiles[1]));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Favorites_DragFromASelectedTile_CarriesTheSelectionInGridOrder()
    {
        var (view, win, _) = await MountFavorites(6);
        var tiles = FavoriteTiles(view);
        Track T(int i) => ((FavoriteItem)tiles[i].DataContext!).Track!;

        // Selected out of order: 4, then 1, then 2.
        Click(win, Centre(tiles[4], win), TestKeys.Primary);
        Click(win, Centre(tiles[1], win), TestKeys.Primary);
        Click(win, Centre(tiles[2], win), TestKeys.Primary);

        Assert.Equal(new[] { T(1), T(2), T(4) }, DragFrom(tiles[2]));
        // An unselected tile carries just itself.
        Assert.Equal(new[] { T(3) }, DragFrom(tiles[3]));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Favorites_PlainPressOnASelectedTile_KeepsTheSelectionForTheDrag_AClickClearsIt()
    {
        var (view, win, _) = await MountFavorites(6);
        var tiles = FavoriteTiles(view);
        Track T(int i) => ((FavoriteItem)tiles[i].DataContext!).Track!;
        Click(win, Centre(tiles[0], win), TestKeys.Primary);
        Click(win, Centre(tiles[3], win), TestKeys.Primary);

        // The press that starts a drag must not drop the selection.
        var p = Centre(tiles[3], win);
        win.MouseDown(p, MouseButton.Left);
        Pump(2);
        Assert.True(tiles[0].Classes.Contains("ctrl-selected"));
        Assert.Equal(new[] { T(0), T(3) }, DragFrom(tiles[3]));

        // Released without a drag, it was a click: the selection clears as it always did.
        win.MouseUp(p, MouseButton.Left);
        Pump(2);
        Assert.Empty(((ITrackSelectionSource)view).GetSelectedTracks());
        Assert.DoesNotContain(FavoriteTiles(view), t => t.Classes.Contains("ctrl-selected"));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Favorites_ShiftClick_SelectsTheRange()
    {
        var (view, win, _) = await MountFavorites(6);
        var tiles = FavoriteTiles(view);
        Click(win, Centre(tiles[1], win), TestKeys.Primary);
        Click(win, Centre(tiles[4], win), RawInputModifiers.Shift);
        Assert.Equal(new[] { false, true, true, true, true, false },
            tiles.Select(t => t.Classes.Contains("ctrl-selected")).ToArray());
        Assert.Equal(4, ((ITrackSelectionSource)view).GetSelectedTracks().Count);
        win.Close();
    }

    // ── Songs page (item 2) ──

    [AvaloniaFact]
    public async Task Songs_DragFromASelectedRow_CarriesTheSelectionInListOrder()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var songs = Enumerable.Range(0, 8).Select(Song).ToList();
        lib.TrackList.AddRange(songs);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        await PumpUntil(() => view.GetVisualDescendants().OfType<ListBoxItem>().Count(r => r.IsVisible) >= 8);

        var rows = view.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsVisible)
            .OrderBy(r => r.TranslatePoint(default, view)!.Value.Y).ToList();
        Track Row(int i) => (Track)rows[i].DataContext!;

        Click(win, Centre(rows[5], win), TestKeys.Primary);
        Click(win, Centre(rows[2], win), TestKeys.Primary);
        Click(win, Centre(rows[6], win), TestKeys.Primary);

        // The plain press that starts the drag keeps the selection; the drag carries all three.
        var p = Centre(rows[2], win);
        win.MouseDown(p, MouseButton.Left);
        Pump(2);
        Assert.Equal(new[] { Row(2), Row(5), Row(6) }, DragFrom(rows[2]));
        win.MouseUp(p, MouseButton.Left);
        Pump(2);

        // A row outside the selection carries just itself.
        Assert.Equal(new[] { Row(3) }, DragFrom(rows[3]));
        win.Close();
    }
}
