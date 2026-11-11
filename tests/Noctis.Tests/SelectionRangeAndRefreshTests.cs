using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #226: Shift+click selects the range from the last clicked album or song, by data,
/// so albums scrolled out of view between the two are selected too; Ctrl+Shift adds it.
/// GitHub #225: library updates right after launch (a startup scan publishes every 1.5 s)
/// re-filled the Albums grid each time and dropped the Ctrl-selection. An update that shows
/// the same albums now leaves the grid alone, and one that rebuilds the Album objects keeps
/// the selection by album id.
/// </summary>
public class SelectionRangeAndRefreshTests
{
    private readonly ITestOutputHelper _o;
    public SelectionRangeAndRefreshTests(ITestOutputHelper o) => _o = o;

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

    private static async Task Settle(int ms = 300)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(10); }
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

    private static List<Button> Tiles(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible).ToList();

    private static bool Selected(Control c) => c.Classes.Contains("ctrl-selected");

    /// <summary>One album per song, named "Album 000".. so grid order is index order.</summary>
    private static Album NewAlbum(Guid id, Track track) => new()
    {
        Id = id, Name = track.Album, Artist = "Artist", Year = 2020, TrackCount = 1, Tracks = new List<Track> { track },
    };

    private static async Task<(FakeLibraryService lib, LibraryAlbumsViewModel vm, LibraryAlbumsView view, Window win)> MountAlbums(int count, bool masonry = false)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            var t = new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = $"Album {i:000}", AlbumId = id, FilePath = $"C:\\m\\{i}.mp3",
                // Not "recently added": those float to the top in their own order.
                DateAdded = new DateTime(2020, 1, 1) };
            lib.TrackList.Add(t);
            albums.Add(NewAlbum(id, t));
        }
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumGridMasonry = masonry;
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump();
        vm.IsActive = true;
        await PumpUntil(() => Tiles(view).Count > 0);
        Assert.Equal(masonry, vm.IsMasonryView);
        return (lib, vm, view, win);
    }

    private static void Click(Window win, Control target, RawInputModifiers mods = RawInputModifiers.None)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, 40), win)!.Value;
        win.MouseDown(p, MouseButton.Left, mods);
        win.MouseUp(p, MouseButton.Left, mods);
        Pump(2);
    }

    private static Button TileOf(Visual root, Album album) =>
        Tiles(root).First(t => ReferenceEquals(t.DataContext, album));

    // ── #225 ──

    [AvaloniaFact]
    public async Task Albums_LibraryUpdatesWithTheSameAlbums_LeaveTheGridAndSelectionAlone()
    {
        var (lib, vm, view, win) = await MountAlbums(30);
        win.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); Pump(2);
        var tiles = Tiles(view);
        Assert.All(tiles, t => Assert.True(Selected(t)));

        var events = 0;
        vm.FilteredAlbumRows.CollectionChanged += (_, _) => events++;
        for (var i = 0; i < 4; i++)
        {
            lib.RaiseLibraryUpdated();
            await Settle(120);
        }
        _o.WriteLine($"row events after 4 updates: {events}");
        Assert.Equal(0, events);
        Assert.All(tiles, t => Assert.True(Selected(t), "same tile objects, still selected"));
        Assert.Equal(30, view.GetSelectedTracks().Count);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Albums_LibraryUpdatesThatRebuildTheAlbums_KeepTheSelectionById_AndDropRemovedOnes()
    {
        var (lib, vm, view, win) = await MountAlbums(30);
        var albums = (List<Album>)lib.Albums;
        // Ctrl-click the first album, Shift+click the sixth: six albums.
        Click(win, TileOf(view, albums[0]), RawInputModifiers.Control);
        Click(win, TileOf(view, albums[5]), RawInputModifiers.Shift);
        Assert.Equal(6, view.GetSelectedTracks().Count);

        // Several library updates, each with freshly built Album objects (same ids), the way
        // a metadata pass rebuilds them; the last one drops album 2 from the library.
        for (var pass = 0; pass < 3; pass++)
        {
            var rebuilt = albums.Select(a => NewAlbum(a.Id, a.Tracks[0])).ToList();
            if (pass == 2) rebuilt.RemoveAt(2);
            albums.Clear();
            albums.AddRange(rebuilt);
            lib.RaiseLibraryUpdated();
            await PumpUntil(() => vm.FilteredAlbumRows.OfType<AlbumRow>().First().Albums[0] == albums[0]);
            await Settle(60);
        }

        var selectedTiles = Tiles(view).Where(Selected).Select(t => (Album)t.DataContext!).ToList();
        _o.WriteLine("selected: " + string.Join(", ", selectedTiles.Select(a => a.Name)));
        Assert.Equal(new[] { "Album 000", "Album 001", "Album 003", "Album 004", "Album 005" }, selectedTiles.Select(a => a.Name));
        Assert.All(selectedTiles, a => Assert.Contains(a, albums)); // the new objects
        Assert.Equal(5, view.GetSelectedTracks().Count);

        // The anchor followed its album too: Shift+click on Album 007 spans 000..007 without 002.
        Click(win, TileOf(view, albums[6]), RawInputModifiers.Shift);
        Assert.Equal(7, view.GetSelectedTracks().Count);
        win.Close();
    }

    [AvaloniaFact]
    public async Task Albums_ACoverArriving_ReplacesOnlyItsRow()
    {
        var (lib, vm, view, win) = await MountAlbums(30);
        var albums = (List<Album>)lib.Albums;
        win.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); Pump(2);
        var firstRowTiles = Tiles(view).Take(vm.GridColumns).ToList();

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.FilteredAlbumRows.CollectionChanged += (_, e) => actions.Add(e.Action);
        // Album 20 got a cover: a new object for it alone (the others are reused).
        var changed = NewAlbum(albums[20].Id, albums[20].Tracks[0]);
        changed.ArtworkPath = "C:\\art\\20.jpg";
        albums[20] = changed;
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => vm.CarouselAlbums.Contains(changed));
        await Settle(60);

        _o.WriteLine("events: " + string.Join(", ", actions));
        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Replace }, actions);
        // The untouched first row kept its tiles, still selected; so did the changed album.
        Assert.All(firstRowTiles, t => Assert.True(TopLevel.GetTopLevel(t) != null && Selected(t)));
        Assert.Equal(30, view.GetSelectedTracks().Count);
        win.Close();
    }

    // ── #226 ──

    [AvaloniaFact]
    public async Task Albums_ShiftClick_SelectsTheRange_IncludingAlbumsScrolledOutOfView()
    {
        var (lib, vm, view, win) = await MountAlbums(300);
        var albums = (List<Album>)lib.Albums;
        Click(win, TileOf(view, albums[0])); // plain click: the anchor (it would open the album)
        Click(win, TileOf(view, albums[2]), RawInputModifiers.Shift);
        Assert.Equal(3, view.GetSelectedTracks().Count);
        Assert.True(Selected(TileOf(view, albums[1])));

        // Scroll far down; the first rows are no longer realized.
        var sv = view.AlbumListBox.FindDescendantOfType<ScrollViewer>()!;
        sv.Offset = new Vector(0, sv.Extent.Height - sv.Viewport.Height);
        await PumpUntil(() => Tiles(view).Any(t => ReferenceEquals(t.DataContext, albums[290])));
        Assert.DoesNotContain(Tiles(view), t => ReferenceEquals(t.DataContext, albums[0]));

        var target = albums[290];
        Click(win, TileOf(view, target), RawInputModifiers.Shift);
        Assert.Equal(291, view.GetSelectedTracks().Count);
        Assert.All(Tiles(view), t => Assert.Equal(albums.IndexOf((Album)t.DataContext!) <= 290, Selected(t)));

        // Shift alone replaces the range; Ctrl+Shift adds to what is there (the last two rows
        // are on screen).
        Click(win, TileOf(view, albums[292]), RawInputModifiers.Shift);
        Assert.Equal(293, view.GetSelectedTracks().Count);
        Click(win, TileOf(view, albums[297]), RawInputModifiers.Shift | RawInputModifiers.Control);
        Assert.Equal(298, view.GetSelectedTracks().Count);

        // A Ctrl+click moves the anchor; the next Shift+click spans from there only.
        Click(win, TileOf(view, albums[295]), RawInputModifiers.Control); // toggles 295 off
        Click(win, TileOf(view, albums[297]), RawInputModifiers.Shift);
        Assert.Equal(new[] { 295, 296, 297 }, view.GetSelectedTracks().Select(t => lib.TrackList.IndexOf(t)).OrderBy(i => i));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Songs_ShiftClick_SelectsTheRange_AndCtrlShiftAdds()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 12; i++)
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i:00}", Artist = "Artist", Album = "Album", FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"), Duration = TimeSpan.FromMinutes(3) });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        vm.IsActive = true;
        await PumpUntil(() => view.TrackList.GetVisualDescendants().OfType<ListBoxItem>().Count() == 12);

        ListBoxItem Row(int i) => view.TrackList.GetVisualDescendants().OfType<ListBoxItem>().First(r => ReferenceEquals(r.DataContext, vm.FilteredTracks[i]));
        void ClickRow(int i, RawInputModifiers mods = RawInputModifiers.None)
        {
            var r = Row(i);
            var p = r.TranslatePoint(new Point(200, r.Bounds.Height / 2), win)!.Value;
            win.MouseDown(p, MouseButton.Left, mods); win.MouseUp(p, MouseButton.Left, mods); Pump(2);
        }
        int[] SelectedRows() => Enumerable.Range(0, 12).Where(i => Selected(Row(i))).ToArray();

        ClickRow(2);
        ClickRow(6, RawInputModifiers.Shift);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, SelectedRows());
        Assert.Equal(5, view.GetSelectedTracks().Count);

        ClickRow(0, RawInputModifiers.Shift); // re-spans from the same anchor
        Assert.Equal(new[] { 0, 1, 2 }, SelectedRows());

        ClickRow(9, RawInputModifiers.Shift | RawInputModifiers.Control);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, SelectedRows());

        // A library update that hands back a re-read song (new object, same id) keeps it selected.
        var reRead = vm.FilteredTracks[4];
        var fresh = new Track { Id = reRead.Id, Title = reRead.Title, Artist = reRead.Artist, Album = reRead.Album, FilePath = reRead.FilePath, Duration = reRead.Duration };
        lib.TrackList[lib.TrackList.IndexOf(reRead)] = fresh;
        lib.TrackList.RemoveAt(lib.TrackList.IndexOf(vm.FilteredTracks[5])); // and one song left the library
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => vm.FilteredTracks.Contains(fresh) && vm.FilteredTracks.Count == 11);
        Assert.Contains(fresh, view.GetSelectedTracks());
        Assert.DoesNotContain(reRead, view.GetSelectedTracks());
        Assert.Equal(9, view.GetSelectedTracks().Count);
        win.Close();
    }
}
