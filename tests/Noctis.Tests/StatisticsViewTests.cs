using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The revamped Statistics view: builds both tabs from real data with no binding errors,
/// virtualizes Play History, reads artist portraits from the cache only, and stays fast on a
/// 50,000-play history.
/// </summary>
public class StatisticsViewTests
{
    private readonly ITestOutputHelper _output;
    public StatisticsViewTests(ITestOutputHelper output) => _output = output;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int frames = 6)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Records binding warnings and errors while a test runs.</summary>
    private sealed class BindingSink : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Messages { get; } = new();
        public BindingSink() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Messages.Add($"{source?.GetType().Name}: {messageTemplate}");
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Messages.Add($"{source?.GetType().Name}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
    }

    /// <summary>A library and a log shaped like a real one: albums with covers, skips, favorites.</summary>
    private static (FakeLibraryService Library, PlayHistoryEvent[] Events) SampleData(int trackCount, int eventCount, int seed = 7)
    {
        var rng = new Random(seed);
        var library = new FakeLibraryService();
        var albums = (List<Album>)library.Albums;
        var albumIds = new HashSet<Guid>();
        var artistCount = Math.Max(3, trackCount / 15);
        for (var a = 0; a < artistCount; a++)
            library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = $"Artist {a}" });
        for (var i = 0; i < trackCount; i++)
        {
            var artist = library.ArtistList[i % artistCount].Name;
            var albumName = $"Album {i / 12}";
            var albumId = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(albumName + artist)));
            var track = new Track
            {
                Title = $"Song {i}", Artist = artist, AlbumArtist = artist, Album = albumName, AlbumId = albumId,
                Duration = TimeSpan.FromSeconds(150 + rng.Next(200)),
                FilePath = i % 9 == 0 ? $"C:/m/{i}.mp3" : $"C:/m/{i}.flac",
                SampleRate = i % 5 == 0 ? 96000 : 44100, BitsPerSample = i % 9 == 0 ? 0 : (i % 5 == 0 ? 24 : 16),
                IsFavorite = i % 97 == 0, Rating = i % 61 == 0 ? 5 : 0,
                AlbumArtworkPath = $"C:/nonexistent/art/{albumId}.jpg",
            };
            library.TrackList.Add(track);
            if (albumIds.Add(albumId))
                albums.Add(new Album { Id = albumId, Name = albumName, Artist = artist, ArtworkPath = track.AlbumArtworkPath });
        }

        var now = DateTime.UtcNow;
        var events = new PlayHistoryEvent[eventCount];
        for (var i = 0; i < eventCount; i++)
        {
            // Weighted toward a few favorites, spread over two years, oldest first.
            var idx = (int)(Math.Pow(rng.NextDouble(), 3) * trackCount);
            var t = library.TrackList[idx];
            var playedAt = now.AddMinutes(-(eventCount - i) * (730.0 * 24 * 60 / eventCount));
            events[i] = new PlayHistoryEvent
            {
                // Every fourth play logged under an id the library no longer has (a re-added folder).
                TrackId = i % 4 == 0 ? Guid.NewGuid() : t.Id,
                Title = t.Title, Artist = t.Artist, PlayedAtUtc = playedAt, Skipped = rng.Next(5) == 0,
            };
        }
        return (library, events);
    }

    [AvaloniaFact]
    public void BothTabs_Build_WithNoBindingErrors()
    {
        EnsureAppStyles();
        var (library, events) = SampleData(600, 4000);
        var vm = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events));
        using var sink = new BindingSink();

        var view = new StatisticsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        StatisticsNumbersTests.Refresh(vm);
        Pump();

        // Overview: the hero and both ranked lists are on screen.
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(vm.PlaysText, texts);
        Assert.Contains(vm.ListeningTimeText, texts);
        Assert.Equal(ListeningReportBuilder.TopArtistCount, vm.TopArtists.Count);
        Assert.Contains(vm.TopAlbums[0].Title, texts);
        Assert.Contains(vm.TopTracks[0].Title, texts);

        foreach (var period in new[] { ListeningPeriod.Last7Days, ListeningPeriod.ThisYear, ListeningPeriod.AllTime })
        {
            vm.Period = period;
            StatisticsNumbersTests.Refresh(vm);
            Pump();
        }

        vm.IsHistoryTabSelected = true;
        Pump();
        var list = view.FindControl<ItemsControl>("HistoryList")!;
        var realized = list.GetRealizedContainers().Count();
        _output.WriteLine($"history feed: {vm.HistoryFeed.Count} items, {realized} realized");
        Assert.True(vm.RecentPlayCount > 1000);
        Assert.True(realized < 60, $"Play History realized {realized} of {vm.HistoryFeed.Count} rows");
        Assert.Equal(24, vm.HourBars.Count);
        Assert.True(vm.HourBars.Count(b => b.IsPeak) == 1);

        // Scroll to the end: rows realize on demand.
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Pump();
        Assert.Contains(list.GetRealizedContainers(), c => c.DataContext is StatsPlayRow);

        window.Close();
        Assert.True(sink.Messages.Count == 0, string.Join("\n", sink.Messages.Distinct().Take(20)));
    }

    [AvaloniaFact]
    public void EmptyLibraryAndLog_ShowsTheEmptyStates()
    {
        EnsureAppStyles();
        var vm = new StatisticsViewModel(new FakeLibraryService(), new StatisticsNumbersTests.History(Array.Empty<PlayHistoryEvent>()));
        using var sink = new BindingSink();
        var view = new StatisticsView { DataContext = vm };
        var window = new Window { Width = 900, Height = 800, Content = view };
        window.Show();
        StatisticsNumbersTests.Refresh(vm);
        vm.IsHistoryTabSelected = true;
        Pump();

        Assert.False(vm.HasPlayHistory);
        Assert.IsType<StatsFeedEmpty>(vm.HistoryFeed[1]);
        Assert.Equal(2, vm.HistoryFeed.Count);
        window.Close();
        Assert.True(sink.Messages.Count == 0, string.Join("\n", sink.Messages.Distinct().Take(20)));
    }

    [AvaloniaFact]
    public void TopArtist_UsesTheCachedPortrait_AndNeverDownloads()
    {
        using var persistence = new TestPersistenceService();
        var handler = new CountingHandler();
        var images = new ArtistImageService(new HttpClient(handler), persistence);
        var library = new FakeLibraryService();
        var cached = new Artist { Id = Guid.NewGuid(), Name = "Taylor Swift" };
        var uncached = new Artist { Id = Guid.NewGuid(), Name = "Juice WRLD" };
        library.ArtistList.AddRange(new[] { cached, uncached });
        var path = images.GetCachedImagePath(cached.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[8192]); // over the 5 KB placeholder purge
        var a = new Track { Title = "Love Story", Artist = "Taylor Swift", Album = "Fearless" };
        var b = new Track { Title = "Lucid Dreams", Artist = "Juice WRLD", Album = "GBGR" };
        library.TrackList.AddRange(new[] { a, b });
        var events = new[]
        {
            StatisticsNumbersTests.Play(a.Id, a.Title, a.Artist, DateTime.UtcNow.AddHours(-2)),
            StatisticsNumbersTests.Play(a.Id, a.Title, a.Artist, DateTime.UtcNow.AddHours(-1)),
            StatisticsNumbersTests.Play(b.Id, b.Title, b.Artist, DateTime.UtcNow.AddMinutes(-30)),
        };
        var vm = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events), images);

        StatisticsNumbersTests.Refresh(vm);

        Assert.Equal(path, vm.TopArtists[0].ImagePath);
        Assert.True(vm.TopArtists[0].CanOpen);
        Assert.Null(vm.TopArtists[1].ImagePath);
        Assert.Equal(0, handler.Requests);
    }

    [AvaloniaFact]
    public void OpeningAnAlbumRow_RaisesAlbumOpened_AndAnArtistTile_CallsTheArtistAction()
    {
        var (library, events) = SampleData(60, 300);
        var vm = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events));
        StatisticsNumbersTests.Refresh(vm);
        Album? opened = null;
        string? artist = null;
        vm.AlbumOpened += (_, al) => opened = al;
        vm.SetViewArtistAction(name => artist = name);

        vm.OpenAlbumCommand.Execute(vm.TopAlbums[0]);
        vm.OpenArtistCommand.Execute(vm.TopArtists[0]);

        Assert.NotNull(opened);
        Assert.Equal(vm.TopAlbums[0].AlbumId, opened!.Id);
        Assert.Equal(vm.TopArtists[0].Name, artist);
    }

    /// <summary>
    /// Performance: a synthetic 50,000-play history (the live log caps at 10,000) over a
    /// 20,000-track library. Prints the numbers; the ceilings only catch a regression by an
    /// order of magnitude.
    /// </summary>
    [AvaloniaFact]
    public void FiftyThousandPlays_ComputeAndShowQuickly()
    {
        EnsureAppStyles();
        var (library, events) = SampleData(20_000, 50_000);
        var tracks = library.TrackList.ToArray();
        var albums = library.Albums.ToArray();
        var artists = library.ArtistList.ToArray();

        // Warm-up (JIT), then each period timed on its own.
        StatisticsViewModel.Compute(tracks, albums, artists, events, ListeningPeriod.AllTime, DateTime.Now, null);
        foreach (var period in Enum.GetValues<ListeningPeriod>())
        {
            var sw = Stopwatch.StartNew();
            var result = StatisticsViewModel.Compute(tracks, albums, artists, events, period, DateTime.Now, null);
            sw.Stop();
            _output.WriteLine($"compute {period}: {sw.ElapsedMilliseconds} ms ({result.RecentPlayCount} plays in period, {result.HistoryRows.Count} feed rows)");
            Assert.True(sw.ElapsedMilliseconds < 3000, $"{period} took {sw.ElapsedMilliseconds} ms");
        }

        // The UI-thread apply alone (no view attached).
        {
            var bare = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events));
            StatisticsNumbersTests.Refresh(bare);
            bare.Period = ListeningPeriod.AllTime;
            StatisticsNumbersTests.Refresh(bare);
            var t = bare.RefreshAsync();
            var longest = TimeSpan.Zero;
            while (!t.IsCompleted)
            {
                var step = Stopwatch.StartNew();
                Dispatcher.UIThread.RunJobs();
                longest = step.Elapsed > longest ? step.Elapsed : longest;
                Thread.Sleep(1);
            }
            _output.WriteLine($"apply only (no view), all time: longest UI-thread job {longest.TotalMilliseconds:0} ms");
        }

        // End to end, as on first open (default period) and then All Time: background compute,
        // the UI-thread apply, and the first layout.
        var vm = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events));
        var view = new StatisticsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Pump();
        foreach (var period in new[] { ListeningPeriod.Last30Days, ListeningPeriod.AllTime })
        {
            if (vm.Period != period)
            {
                vm.Period = period;               // starts its own refresh...
                StatisticsNumbersTests.Refresh(vm); // ...superseded by this one
                Pump();
            }
            var total = Stopwatch.StartNew();
            var task = vm.RefreshAsync();
            var uiBlocked = TimeSpan.Zero;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var step = Stopwatch.StartNew();
                Dispatcher.UIThread.RunJobs();
                uiBlocked = step.Elapsed > uiBlocked ? step.Elapsed : uiBlocked;
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted);
            var layout = Stopwatch.StartNew();
            Pump(2);
            layout.Stop();
            total.Stop();
            _output.WriteLine($"refresh {period} end to end: {total.ElapsedMilliseconds} ms; longest UI-thread job {uiBlocked.TotalMilliseconds:0} ms; overview layout {layout.ElapsedMilliseconds} ms");
            Assert.True(uiBlocked.TotalMilliseconds < 1500, $"UI thread blocked {uiBlocked.TotalMilliseconds:0} ms");
        }

        var tab = Stopwatch.StartNew();
        vm.IsHistoryTabSelected = true;
        Pump(2);
        tab.Stop();
        var realized = view.FindControl<ItemsControl>("HistoryList")!.GetRealizedContainers().Count();
        _output.WriteLine($"switch to Play History: {tab.ElapsedMilliseconds} ms, {realized} of {vm.HistoryFeed.Count} rows realized");
        window.Close();

        Assert.Equal(50_000, vm.RecentPlayCount);
        Assert.True(realized < 60);
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): both tabs, wide and narrow, saved as PNGs
    /// to the temp folder for a visual check (never sent anywhere).</summary>
    [AvaloniaFact]
    public void Probe_RendersBothTabs()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-statistics-shots");
        Directory.CreateDirectory(dir);
        var (library, events) = SampleData(600, 6000);
        var colors = new[] { "#C0392B", "#2D7DD2", "#3BB273", "#F2A33A", "#8E44AD", "#16A085", "#D35400", "#7F8C8D" };
        var i = 0;
        foreach (var album in library.Albums)
        {
            var path = Path.Combine(dir, $"cover{i % colors.Length}.png");
            if (!File.Exists(path))
            {
                var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(96, 96));
                using (var ctx = bmp.CreateDrawingContext())
                    ctx.FillRectangle(new SolidColorBrush(Color.Parse(colors[i % colors.Length])), new Rect(0, 0, 96, 96));
                bmp.Save(path);
            }
            album.ArtworkPath = path;
            foreach (var t in library.TrackList.Where(t => t.AlbumId == album.Id)) t.AlbumArtworkPath = path;
            i++;
        }
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new StatisticsViewModel(library, new StatisticsNumbersTests.History(events));
            foreach (var (width, name) in new[] { (1400, "wide"), (760, "narrow") })
            {
                var view = new StatisticsView { DataContext = vm };
                var window = new Window { Width = width, Height = 1500, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
                window.Show();
                StatisticsNumbersTests.Refresh(vm);
                Pump(20);
                Thread.Sleep(300);
                Pump(20);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"overview-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.IsHistoryTabSelected = true;
                Pump(20);
                Thread.Sleep(300);
                Pump(20);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"history-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                vm.IsOverviewTabSelected = true;
                window.Close();
            }
        });
        _output.WriteLine(dir);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Statistics must not download artist images: " + request.RequestUri);
        }
    }
}
