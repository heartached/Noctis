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
/// GitHub #241: "with Masonry on, scrolling stops every few albums". A wheel glide (40 px a
/// step) down a 2000-album library in both layouts: UI-thread ms per step (jobs + a render
/// tick, which runs layout headless) and tiles re-bound per step. A Masonry step must cost
/// about what a grid step costs.
/// </summary>
public class MasonryScrollCostTests
{
    private readonly ITestOutputHelper _o;
    public MasonryScrollCostTests(ITestOutputHelper o) => _o = o;

    private static readonly string[] Words =
        ["Night", "Golden", "Echoes", "of", "the", "River", "Electric", "Dreams", "Live", "at", "Wembley",
         "Remastered", "Deluxe", "Edition", "Songs", "for", "Summer", "Blue", "Hour", "Paradise", "Lost", "Found"];

    private static List<Album> Library(int count)
    {
        var rnd = new Random(241);
        var albums = new List<Album>(count);
        for (var i = 0; i < count; i++)
        {
            var words = Enumerable.Range(0, 1 + rnd.Next(rnd.Next(4) == 0 ? 9 : 4)).Select(_ => Words[rnd.Next(Words.Length)]);
            var tracks = new List<Track> { new() { Title = "t", IsFavorite = rnd.Next(10) == 0, IsExplicit = rnd.Next(12) == 0 } };
            albums.Add(new Album
            {
                Id = Guid.NewGuid(), Name = $"{i:0000} " + string.Join(' ', words),
                Artist = "Artist " + rnd.Next(300), Year = 1970 + rnd.Next(55), Tracks = tracks,
            });
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

    private static void Pump(int n = 1)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private async Task<(double Avg, double P95, double Max, double Rebinds)> Glide(bool masonry, int albumCount, int steps)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(Library(albumCount));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        vm.AlbumSortMode = "title";
        settings.AlbumGridMasonry = masonry;
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(4);
        vm.IsActive = true;
        var deadline = Environment.TickCount64 + 10000;
        ScrollViewer? Scroller() => masonry
            ? view.FindControl<ScrollViewer>("MasonryScroller")
            : view.AlbumListBox.FindDescendantOfType<ScrollViewer>();
        while (Environment.TickCount64 < deadline && !(Scroller() is { } s0 && s0.Extent.Height > 5000)) { Pump(1); await Task.Delay(5); }
        Pump(6);
        Assert.Equal(masonry, vm.IsMasonryView);
        var sv = Scroller()!;

        // Tiles re-bound to another album during the glide.
        var rebinds = 0;
        var hooked = new HashSet<Button>();
        void Hook()
        {
            foreach (var b in view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")))
                if (hooked.Add(b)) b.DataContextChanged += (_, _) => rebinds++;
        }
        Hook();

        // Warm-up glide (first-time template work, JIT), then the timed one from the top.
        for (var i = 0; i < 60; i++) { sv.Offset = new Vector(0, sv.Offset.Y + 40); Pump(1); }
        sv.Offset = default; Pump(4); Hook(); rebinds = 0;
        var times = new List<double>(steps);
        for (var i = 0; i < steps; i++)
        {
            var sw = Stopwatch.StartNew();
            sv.Offset = new Vector(0, sv.Offset.Y + 40);
            Pump(1);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Hook();
        }
        win.Close();
        times.Sort();
        var r = (times.Average(), times[(int)(times.Count * 0.95)], times[^1], rebinds / (double)steps);
        _o.WriteLine($"{(masonry ? "masonry" : "grid   ")} {albumCount} albums, {steps} x 40px: avg {r.Item1:0.00} ms, p95 {r.Item2:0.00} ms, max {r.Item3:0.00} ms, {r.Item4:0.0} re-binds/step");
        return r;
    }

    [AvaloniaFact]
    public async Task MasonryWheelStep_CostsAboutWhatAGridStepCosts()
    {
        var grid = await Glide(masonry: false, 2000, 400);
        var masonry = await Glide(masonry: true, 2000, 400);
        // Bounded by the grid's own step (plus headless timing noise), not by the library size.
        Assert.True(masonry.Max < Math.Max(4 * grid.Max, 40),
            $"Masonry worst step {masonry.Max:0.0} ms vs grid {grid.Max:0.0} ms");
        Assert.True(masonry.Avg < Math.Max(3 * grid.Avg, 4),
            $"Masonry average step {masonry.Avg:0.00} ms vs grid {grid.Avg:0.00} ms");
    }
}
