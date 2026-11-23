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
}
