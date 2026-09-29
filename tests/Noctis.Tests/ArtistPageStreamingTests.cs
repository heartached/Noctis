using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Artist page with a big catalogue (owner, 09-24: "browsing Albums and Songs on an artist
/// with lots of music lags, freezes ~10 s, and the artwork goes black and flashes").
/// The headless probe on a Bad Bunny-sized artist (175 songs, 30 releases) found:
/// <list type="bullet">
/// <item>StreamingFill appended every slice after the first with AddRange, which raises
/// Reset: the ItemsControl dropped every row realized so far and built them all again, so
/// opening Songs realized 535 rows for 175 songs (360 torn down, the last slice all 175 in
/// one pass) and the covers already on screen were rebuilt blank on each slice.</item>
/// <item>The Songs list was a plain StackPanel: every song was a live row, whatever the
/// viewport showed.</item>
/// </list>
/// </summary>
[Collection("ArtworkCache")]
public class ArtistPageStreamingTests : IDisposable
{
    private int _decodes;

    public ArtistPageStreamingTests()
    {
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = (path, width) =>
        {
            Interlocked.Increment(ref _decodes);
            return new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        };
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
    }

    // ── Collection + slicing ──

    [Fact]
    public void AppendRange_RaisesOneAdd_WithTheItemsAndTheirStartIndex()
    {
        var col = new BulkObservableCollection<int>();
        col.ReplaceAll(new[] { 1, 2, 3 });
        var events = new List<NotifyCollectionChangedEventArgs>();
        col.CollectionChanged += (_, e) => events.Add(e);

        col.AppendRange(new[] { 4, 5 });
        col.AppendRange(Array.Empty<int>()); // nothing to add: no event

        var e = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, e.Action);
        Assert.Equal(3, e.NewStartingIndex);
        Assert.Equal(new[] { 4, 5 }, e.NewItems!.Cast<int>());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, col);
    }

    [AvaloniaFact]
    public void StreamingFill_ReplacesOnce_ThenAppendsEachSlice_NeverResetsAgain()
    {
        var col = new BulkObservableCollection<int>();
        col.ReplaceAll(new[] { -1 });
        var events = new List<NotifyCollectionChangedEventArgs>();
        col.CollectionChanged += (_, e) => events.Add(e);

        StreamingFill.Into(col, Enumerable.Range(0, 25).ToList(), generation: 1, () => 1, first: 10, chunk: 10);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Enumerable.Range(0, 25), col);
        Assert.Equal(3, events.Count);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action); // the old content goes once
        Assert.All(events.Skip(1), e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal(new[] { 10, 20 }, events.Skip(1).Select(e => e.NewStartingIndex));
    }

    // ── The page ──

    [AvaloniaFact]
    public void SongsTab_BigArtist_RealizesOnlyTheRowsInView_AndNeverRebuildsThem()
    {
        var (vm, view, win) = Mount(BigArtist("Bad Bunny"));
        var songs = ListFor(view, vm.AllSongs);
        int prepared = 0, cleared = 0;
        songs.ContainerPrepared += (_, _) => prepared++;
        songs.ContainerClearing += (_, _) => cleared++;

        vm.SelectTabCommand.Execute("songs");
        for (var i = 0; i < 10; i++) Frame(win);

        Assert.Equal(175, vm.AllSongs.Count);   // the whole ranking is there to scroll to...
        Assert.InRange(prepared, 1, 40);         // ...but only the rows in view were built
        Assert.Equal(0, cleared);                // and none was torn down by a later slice

        // Scrolling to the end realizes the tail (container reuse keeps the count bounded).
        var scroll = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        for (var i = 0; i < 4; i++) Frame(win);
        var realized = RealizedRows(view, "SongsPanel");
        Assert.Contains(realized, r => r.Rank == 175);
        Assert.InRange(realized.Count, 1, 40);
        win.Close();
    }

    [AvaloniaFact]
    public void SongsTab_Virtualized_BackNavigationRestoresTheScrolledPosition()
    {
        var (vm, view, win) = Mount(BigArtist("Bad Bunny"));
        vm.SelectTabCommand.Execute("songs");
        for (var i = 0; i < 10; i++) Frame(win);
        var scroll = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        scroll.Offset = new Vector(0, 5000);
        for (var i = 0; i < 4; i++) Frame(win);
        var left = scroll.Offset.Y;
        Assert.Equal(5000, left, 1);
        var shown = RealizedRows(view, "SongsPanel").Select(r => r.Rank).Where(r => r > 6).ToList();

        // Album page and Back: the VM lives on in history, the view is rebuilt.
        win.Content = null;
        Frame(win);
        var view2 = new ArtistDetailView { DataContext = vm };
        win.Content = view2;
        for (var i = 0; i < 8; i++) Frame(win);

        Assert.Equal(left, view2.FindControl<ScrollViewer>("PageScrollViewer")!.Offset.Y, 1);
        var back = RealizedRows(view2, "SongsPanel").Select(r => r.Rank).ToList();
        Assert.Contains(shown[shown.Count / 2], back); // the same stretch of the ranking is on screen
        win.Close();
    }

    [AvaloniaFact]
    public void SinglesTab_Streaming_BuildsEachTileOnce_AndKeepsTheFirstCoverOnScreen()
    {
        var (vm, view, win) = Mount(BigArtist("Bad Bunny"));
        var grid = ListFor(view, vm.SingleReleases);
        int prepared = 0, cleared = 0;
        grid.ContainerPrepared += (_, _) => prepared++;
        grid.ContainerClearing += (_, _) => cleared++;

        // The first row lands with the switch. Lay it out and let its covers load, but hold
        // the Background-priority slices back so the rest arrive after that cover is shown.
        vm.SelectTabCommand.Execute("singles");
        win.UpdateLayout();
        var first = view.FindControl<StackPanel>("SinglesPanel")!.GetVisualDescendants().OfType<CachedImage>()
            .First(i => i.DataContext is Album a && ReferenceEquals(a, vm.SingleReleases[0]));
        for (var i = 0; i < 50 && first.Source == null; i++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            Thread.Sleep(10);
        }
        Assert.NotNull(first.Source);
        Assert.True(vm.SingleReleases.Count < 20, "the later slices should still be queued");
        var blanked = 0;
        first.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Image.SourceProperty && e.OldValue != null && e.NewValue == null) blanked++;
        };
        for (var i = 0; i < 10; i++) Frame(win);

        Assert.Equal(20, vm.SingleReleases.Count);
        Assert.Equal(0, blanked);                    // its cover never went blank...
        Assert.NotNull(TopLevel.GetTopLevel(first)); // ...because the same tile is still the one on screen
        Assert.NotNull(first.Source);
        Assert.Equal(20, prepared);  // was 40: every slice rebuilt the tiles before it
        Assert.Equal(0, cleared);
        win.Close();
    }

    /// <summary>Prints what a tab switch costs on a big artist (not a timing bound: CI noise).
    /// Set NOCTIS_PROBE_OUT to a file path to collect the lines.</summary>
    [AvaloniaFact]
    public void Probe_TabSwitchCost_Report()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(BigArtist("Bad Bunny"));
        // Filler library so Classify walks a realistic catalogue.
        for (var i = 0; i < 1500; i++)
            ((List<Album>)lib.Albums).Add(MakeAlbum($"Other {i}", $"Other Artist {i % 400} feat. Someone", 2000 + i % 25, 12, 0));
        lib.TrackList.AddRange(lib.Albums.SelectMany(a => a.Tracks));

        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sw = Stopwatch.StartNew();
        var vm = new ArtistDetailViewModel("Bad Bunny", lib, player);
        Log($"PROBE ctor (Classify over {lib.TrackList.Count} tracks): {sw.Elapsed.TotalMilliseconds:F1}ms");

        var probes = new Dictionary<string, ListProbe>();
        var starts = new Dictionary<object, long>();
        // Subscribed before the view binds, so this runs ahead of the ItemsControl's handler.
        void Pre(object? s, NotifyCollectionChangedEventArgs e) => starts[s!] = Stopwatch.GetTimestamp();
        vm.AllSongs.CollectionChanged += Pre;
        vm.AlbumReleases.CollectionChanged += Pre;
        vm.SingleReleases.CollectionChanged += Pre;

        sw.Restart();
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 1000, Content = view };
        win.Show();
        Frame(win);
        Log($"PROBE open (Overview first frames): {sw.Elapsed.TotalMilliseconds:F0}ms");

        foreach (var (name, source) in new (string, object)[] { ("songs", vm.AllSongs), ("albums", vm.AlbumReleases), ("singles", vm.SingleReleases) })
        {
            var p = probes[name] = new ListProbe();
            var ic = ListFor(view, source);
            ic.ContainerPrepared += (_, _) => p.Prepared++;
            ic.ContainerClearing += (_, _) => p.Cleared++;
            // Runs after the ItemsControl's handler: lay the change out now and time it.
            ((INotifyCollectionChanged)source).CollectionChanged += (s, e) =>
            {
                win.UpdateLayout();
                var ms = (Stopwatch.GetTimestamp() - starts[s!]) * 1000.0 / Stopwatch.Frequency;
                p.Events++; p.MsTotal += ms; p.MsMax = Math.Max(p.MsMax, ms);
            };
        }

        var scroll = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        var favTarget = vm.PopularSongs.Last().Track;
        var steps = new (string Name, Action Act)[]
        {
            ("tab songs", () => vm.SelectTabCommand.Execute("songs")),
            ("scroll songs to end", () => scroll.Offset = new Vector(0, scroll.Extent.Height)),
            ("tab albums", () => vm.SelectTabCommand.Execute("albums")),
            ("heart on albums tab", () => { favTarget.IsFavorite = !favTarget.IsFavorite; lib.NotifyFavoritesChanged(new[] { favTarget }); }),
            ("tab singles", () => vm.SelectTabCommand.Execute("singles")),
            ("tab songs again", () => vm.SelectTabCommand.Execute("songs")),
            ("tab albums again", () => vm.SelectTabCommand.Execute("albums")),
        };
        foreach (var (stepName, act) in steps)
        {
            foreach (var p in probes.Values) { p.Prepared = p.Cleared = p.Events = 0; p.MsMax = p.MsTotal = 0; }
            var gc0 = GC.CollectionCount(0); var gc2 = GC.CollectionCount(2);
            var alloc = GC.GetTotalAllocatedBytes(); var pause = GC.GetTotalPauseDuration();
            sw.Restart();
            act();
            for (var i = 0; i < 20; i++) Frame(win);
            var wall = sw.Elapsed.TotalMilliseconds;
            Log($"PROBE {stepName}: wall {wall:F0}ms, alloc {(GC.GetTotalAllocatedBytes() - alloc) / 1048576.0:F1}MB, " +
                $"gc0 +{GC.CollectionCount(0) - gc0} gc2 +{GC.CollectionCount(2) - gc2}, pause +{(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:F0}ms");
            foreach (var (name, p) in probes)
                if (p.Events > 0 || p.Prepared > 0 || p.Cleared > 0)
                    Log($"PROBE   {name}: collection events {p.Events}, containers prepared {p.Prepared}, cleared {p.Cleared}, " +
                        $"event+layout max {p.MsMax:F1}ms, total {p.MsTotal:F1}ms");
        }
        Log($"PROBE decodes: {_decodes}");
        win.Close();
    }

    // ── Helpers ──

    private sealed class ListProbe
    {
        public int Prepared, Cleared, Events;
        public double MsMax, MsTotal;
    }

    private static void Log(string line)
    {
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("NOCTIS_PROBE_OUT") is { Length: > 0 } file)
            System.IO.File.AppendAllText(file, line + Environment.NewLine);
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static Album MakeAlbum(string name, string artist, int year, int trackCount, int playBase)
    {
        var id = Guid.NewGuid();
        var album = new Album
        {
            Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>(),
            ArtworkPath = System.IO.Path.Combine("C:\\", "art", id + ".jpg"),
        };
        for (var i = 1; i <= trackCount; i++)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
                PlayCount = playBase + trackCount - i, Genre = "Latin", AlbumArtworkPath = album.ArtworkPath,
                FilePath = System.IO.Path.Combine("C:\\", "m", id + "-" + i + ".flac"),
            });
        }
        album.TrackCount = trackCount;
        return album;
    }

    /// <summary>Bad Bunny-sized: 10 albums x 14 + 15 singles + 5 four-track EPs = 175 songs, 30 releases.</summary>
    private static List<Album> BigArtist(string artist)
    {
        var albums = new List<Album>();
        for (var i = 0; i < 10; i++) albums.Add(MakeAlbum($"Album {i:00}", artist, 2010 + i, 14, i * 3));
        for (var i = 0; i < 15; i++) albums.Add(MakeAlbum($"Single {i:00}", artist, 2015 + i % 10, 1, i));
        for (var i = 0; i < 5; i++) albums.Add(MakeAlbum($"EP {i:00}", artist, 2012 + i, 4, i));
        foreach (var t in albums.SelectMany(a => a.Tracks).Where((_, i) => i % 9 == 0)) t.IsFavorite = true;
        return albums;
    }

    private static (ArtistDetailViewModel Vm, ArtistDetailView View, Window Win) Mount(List<Album> albums)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(albums);
        lib.TrackList.AddRange(albums.SelectMany(a => a.Tracks));
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel(albums[0].Artist, lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 1000, Content = view };
        win.Show();
        Frame(win);
        return (vm, view, win);
    }

    private static ItemsControl ListFor(ArtistDetailView view, object source)
        => view.GetVisualDescendants().OfType<ItemsControl>().First(ic => ReferenceEquals(ic.ItemsSource, source));

    private static List<TopSongRow> RealizedRows(ArtistDetailView view, string panel)
        => view.FindControl<StackPanel>(panel)!.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("song-row") && b.IsEffectivelyVisible)
            .Select(b => (TopSongRow)b.DataContext!).ToList();

    /// <summary>One frame: queued jobs, a render tick (runs layout headless), a layout flush.</summary>
    private static void Frame(Window win)
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        win.UpdateLayout();
    }
}
