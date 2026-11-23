using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
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
/// GitHub #259: with Masonry on, every album grid uses it (the Genre page under its Songs
/// section, the artist page's release tabs, Home's album sections), and switching it on is
/// not a UI-thread stall on a big library.
/// </summary>
public class MasonryAllGridsTests
{
    private readonly ITestOutputHelper _o;
    public MasonryAllGridsTests(ITestOutputHelper o) => _o = o;

    private static readonly string[] Words =
        ["Night", "Golden", "Echoes", "of", "the", "River", "Electric", "Dreams", "Live", "at", "Wembley",
         "Remastered", "Deluxe", "Edition", "Songs", "for", "Summer", "Blue", "Hour", "Paradise", "Lost", "Found"];

    private static List<Album> Library(int count, string genre = "Rock")
    {
        var rnd = new Random(259);
        var albums = new List<Album>(count);
        for (var i = 0; i < count; i++)
        {
            var words = Enumerable.Range(0, 1 + rnd.Next(rnd.Next(4) == 0 ? 9 : 4)).Select(_ => Words[rnd.Next(Words.Length)]);
            var album = new Album
            {
                Id = Guid.NewGuid(), Name = $"{i:0000} " + string.Join(' ', words),
                Artist = "Artist " + rnd.Next(300), Year = 1970 + rnd.Next(55),
            };
            album.Tracks = new List<Track>
            {
                new() { Title = "t" + i, Artist = album.Artist, AlbumArtist = album.Artist, Album = album.Name, AlbumId = album.Id,
                        Genre = genre, PlayCount = rnd.Next(50) },
            };
            albums.Add(album);
        }
        return albums;
    }

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

    private static async Task PumpUntil(Func<bool> done, int ms = 10000)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline && !done()) { Pump(1); await Task.Delay(5); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static (LibraryAlbumsViewModel Vm, SettingsViewModel Settings) AlbumsVm(int albumCount)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(Library(albumCount));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        return (vm, settings);
    }

    private static List<Button> RealizedTiles(MasonryAlbumPanel panel) =>
        panel.RealizedElements.OfType<Button>().Where(b => b.IsVisible).ToList();

    /// <summary>
    /// "A moment of lag right after enabling Masonry": the UI-thread frames from flipping the
    /// setting on a 5000-album grid until the panel has settled. Before #259 the first frame
    /// laid out every title twice (10000 TextLayouts, ~1.2 s headless); now only the titles
    /// down to the realized band are laid out in that frame, the rest in short idle slices.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingMasonryOn_On5000Albums_IsNotAStall()
    {
        EnsureAppStyles();
        // Warm-up on a small library: template building and JIT are not the library's cost.
        var warm = await SwitchOn(100);
        var big = await SwitchOn(5000);
        Assert.True(big.Worst < Math.Max(3 * warm.Worst, 120),
            $"worst frame after switching Masonry on for 5000 albums: {big.Worst:0} ms (100 albums: {warm.Worst:0} ms)");
        Assert.True(big.FirstFrameLayouts < 600, $"{big.FirstFrameLayouts} titles laid out in the first frame");
        // The idle slices lay out the rest, each title once.
        Assert.InRange(big.Layouts, 5000, 5000 + 600);
    }

    private async Task<(double Worst, int FirstFrameLayouts, int Layouts)> SwitchOn(int albumCount)
    {
        var (vm, settings) = AlbumsVm(albumCount);
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        await PumpUntil(() => view.AlbumListBox.FindDescendantOfType<ScrollViewer>() is { Extent.Height: > 2000 });
        Pump(6);
        var panel = view.FindControl<MasonryAlbumPanel>("MasonryPanel")!;
        var sv = view.FindControl<ScrollViewer>("MasonryScroller")!;

        var sw = Stopwatch.StartNew();
        settings.AlbumGridMasonry = true;
        Pump(1);
        var first = sw.Elapsed.TotalMilliseconds;
        var firstLayouts = panel.TitleLayoutCount;
        var worst = first;
        var frames = 1;
        var total = Stopwatch.StartNew();
        var quiet = 0;
        var last = (panel.TitleLayoutCount, sv.Extent.Height);
        while (quiet < 5 && frames < 3000)
        {
            await Task.Delay(20); // the idle slices run on a timer
            sw.Restart();
            Pump(1);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
            frames++;
            var now = (panel.TitleLayoutCount, sv.Extent.Height);
            quiet = now == last ? quiet + 1 : 0;
            last = now;
        }
        Assert.True(vm.IsMasonryView);
        Assert.NotEmpty(RealizedTiles(panel));
        win.Close();
        _o.WriteLine($"{albumCount} albums, Masonry on: first frame {first:0} ms ({firstLayouts} title layouts), "
            + $"worst frame {worst:0} ms, settled after {frames} frames / {total.ElapsedMilliseconds} ms, {panel.TitleLayoutCount} title layouts");
        return (worst, firstLayouts, panel.TitleLayoutCount);
    }

    // ── Genre page (the Albums view narrowed to a genre, its Songs section on top) ──

    [AvaloniaFact]
    public async Task GenrePage_WithMasonryOn_ShowsTheSongsThenTheAlbumsInMasonry()
    {
        EnsureAppStyles();
        var (vm, settings) = AlbumsVm(300);
        settings.AlbumGridMasonry = true;
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);

        vm.SetGenreFilter("Rock");
        await PumpUntil(() => vm.FilteredAlbumRows.OfType<ArtistSongsRow>().Any());
        var panel = view.FindControl<MasonryAlbumPanel>("MasonryPanel")!;
        await PumpUntil(() => RealizedTiles(panel).Count > 0);
        Pump(4);

        Assert.True(vm.IsMasonryView);
        Assert.False(view.AlbumListBox.IsVisible);
        // The Songs section (header, song rows, "Albums" header) sits above the cards, in
        // the same scroller.
        Assert.Equal(vm.FilteredAlbumRows.Where(r => r is not AlbumRow), vm.MasonrySectionRows);
        Assert.IsType<ArtistSectionHeader>(vm.MasonrySectionRows[0]);
        Assert.IsType<ArtistSectionHeader>(vm.MasonrySectionRows[^1]);
        var sections = view.FindControl<ItemsControl>("MasonrySections")!;
        var sv = view.FindControl<ScrollViewer>("MasonryScroller")!;
        Assert.True(sections.IsVisible);
        Assert.True(sections.Bounds.Height > 100);
        Assert.Same(sv, sections.FindAncestorOfType<ScrollViewer>());
        Assert.Same(sv, panel.FindAncestorOfType<ScrollViewer>());
        var songPills = sections.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("browse-item"));
        Assert.True(songPills > 0, "song pills are shown");
        var firstTile = RealizedTiles(panel).OrderBy(t => t.TranslatePoint(default, sv)!.Value.Y).First();
        Assert.True(firstTile.TranslatePoint(default, sections)!.Value.Y >= sections.Bounds.Height - 1,
            "the album cards start below the Songs section");
        Assert.Equal(vm.CarouselAlbums, panel.Albums);
        Assert.Equal(300, vm.CarouselAlbums.Count);

        // Back on the unfiltered grid the section is gone.
        vm.ClearArtistFilter();
        vm.ApplyFilterImmediate(string.Empty); // as the Back navigation does
        await PumpUntil(() => !vm.FilteredAlbumRows.OfType<ArtistSongsRow>().Any());
        Assert.Empty(vm.MasonrySectionRows);
        Assert.True(vm.IsMasonryView);
        win.Close();
    }

    [AvaloniaFact]
    public async Task GenrePage_WithMasonryOff_KeepsTheRowGrid()
    {
        EnsureAppStyles();
        var (vm, _) = AlbumsVm(60);
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredAlbumRows.Count > 0);
        vm.SetGenreFilter("Rock");
        await PumpUntil(() => vm.FilteredAlbumRows.OfType<ArtistSongsRow>().Any());
        Pump(4);
        Assert.False(vm.IsMasonryView);
        Assert.True(vm.IsRowGridView);
        Assert.True(view.AlbumListBox.IsVisible);
        Assert.False(view.FindControl<ScrollViewer>("MasonryScroller")!.IsVisible);
        win.Close();
    }

    // ── Artist page (Artists › an artist): the release tabs ──

    private static Album ArtistAlbum(string name, string artist, int year, ReleaseType type = ReleaseType.Album)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year };
        album.Tracks = Enumerable.Range(1, type == ReleaseType.Single ? 1 : 8).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name, AlbumId = id,
            TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
            ReleaseType = type, ReleaseTypeFromTag = true,
        }).ToList();
        album.TrackCount = album.Tracks.Count;
        return album;
    }

    private static (ArtistDetailViewModel Vm, ArtistDetailView View, Window Win) MountArtist(bool masonry)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var i = 0; i < 14; i++)
            albums.Add(ArtistAlbum(i % 3 == 0 ? $"Album {i} with a much longer title that wraps onto a second line" : $"Album {i}", "Band", 1990 + i));
        for (var i = 0; i < 9; i++)
            albums.Add(ArtistAlbum(i % 2 == 0 ? $"Single {i} and its long, long title that wraps" : $"Single {i}", "Band", 2000 + i, ReleaseType.Single));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumGridMasonry = masonry;
        var vm = new ArtistDetailViewModel("Band", lib, player, settings: settings);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();
        return (vm, view, win);
    }

    private static List<Button> GridTiles(Control panel) =>
        panel.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("album-tile") && b.IsEffectivelyVisible && b.FindAncestorOfType<MasonryAlbumPanel>() == null).ToList();

    [AvaloniaFact]
    public async Task ArtistPage_WithMasonryOn_ReleaseTabsUseMasonry()
    {
        var (vm, view, win) = MountArtist(masonry: true);
        Assert.True(vm.UseMasonry);

        foreach (var (tab, panelName, masonryName) in new[] { ("albums", "AlbumsPanel", "AlbumsMasonry"), ("singles", "SinglesPanel", "SinglesMasonry") })
        {
            vm.SelectTabCommand.Execute(tab);
            var masonry = view.FindControl<MasonryAlbumPanel>(masonryName)!;
            var expected = tab == "albums" ? vm.AlbumReleases : vm.SingleReleases;
            await PumpUntil(() => RealizedTiles(masonry).Count == expected.Count && expected.Count > 0);
            Pump(4);
            var page = view.FindControl<StackPanel>(panelName)!;
            Assert.True(masonry.IsEffectivelyVisible, $"{tab}: Masonry panel shown");
            Assert.Same(expected, masonry.Albums);
            Assert.Equal(expected.Count, RealizedTiles(masonry).Count);
            Assert.Empty(GridTiles(page)); // the row grid builds no second set of tiles

            // Cards are as tall as their caption: a one-line title's card is shorter than a
            // two-line one's (the row grid gives every tile the two-line height).
            var heights = RealizedTiles(masonry).Select(t => Math.Round(t.Bounds.Height)).Distinct().ToList();
            _o.WriteLine($"{tab}: card heights {string.Join(", ", heights)}");
            Assert.True(heights.Count >= 2, $"{tab}: cards should differ in height");
            // Columns stay the grid's.
            var columns = RealizedTiles(masonry).Select(t => Math.Round(t.TranslatePoint(default, masonry)!.Value.X)).Distinct().Count();
            Assert.Equal(vm.GridColumns, columns);
        }
        win.Close();
    }

    [AvaloniaFact]
    public async Task ArtistPage_WithMasonryOff_KeepsTheUniformGrid()
    {
        var (vm, view, win) = MountArtist(masonry: false);
        Assert.False(vm.UseMasonry);
        vm.SelectTabCommand.Execute("albums");
        var page = view.FindControl<StackPanel>("AlbumsPanel")!;
        await PumpUntil(() => GridTiles(page).Count == vm.AlbumReleases.Count && vm.AlbumReleases.Count > 0);
        Assert.Equal(vm.AlbumReleases.Count, GridTiles(page).Count);
        Assert.False(view.FindControl<MasonryAlbumPanel>("AlbumsMasonry")!.IsEffectivelyVisible);
        // Every tile keeps the fixed two-line height.
        Assert.Single(GridTiles(page).Select(t => Math.Round(t.Bounds.Height)).Distinct());
        win.Close();
    }
}
