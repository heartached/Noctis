using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the "Unknown Artist → click album → crash" report
/// (Discord, v1.4.6): every untagged file in the library lands in the shared
/// Unknown-Album bucket, and AlbumDetailView realized the full track list
/// synchronously through a non-virtualizing panel — with TWO full ~17-item
/// menus built per row. At WAV-rip library scale (thousands of untagged
/// tracks in the one bucket) opening the page froze the UI thread for
/// minutes and ballooned memory by gigabytes: an OOM/killed-app "crash".
///
/// The fix is two-part, both pinned here:
///  1. Track rows share ONE context menu bound on open (LibrarySongsView's
///     TrackContextMenuBuilder pattern) instead of two per-row XAML menus.
///  2. AlbumDetailViewModel streams rows into DiscGroups in chunks: the
///     synchronous first paint realizes at most TrackRealizeChunk rows.
/// </summary>
public class AlbumDetailLargeAlbumTests
{
    private const int Chunk = 200; // mirrors AlbumDetailViewModel.TrackRealizeChunk

    private readonly ITestOutputHelper _output;

    public AlbumDetailLargeAlbumTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>Grafts the app's icon resources + global styles onto the headless app
    /// so AlbumDetailView's StaticResource references resolve like in production.
    /// Probes the live app instead of a static flag: the headless session can hand
    /// tests a fresh Application, so the graft must re-apply per app instance.</summary>
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        // The headless font manager can't shape the embedded TTF; the key only
        // needs to resolve, so map it to the platform default family.
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis/Assets/Styles.axaml")
        });
    }

    /// <summary>Builds N tracks shaped exactly like untagged WAV rips (what
    /// MetadataService produces when a file has no tags at all).</summary>
    private static List<Track> UntaggedWavTracks(int count)
    {
        var tracks = new List<Track>(count);
        for (var i = 0; i < count; i++)
        {
            tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                FilePath = TestPaths.Primary("itunes", $"Album{i / 12}", $"{i % 12 + 1:00} Track {i}.wav"),
                Title = $"{i % 12 + 1:00} Track {i}",
                Artist = "Unknown Artist",
                AlbumArtist = "Unknown Artist",
                Album = "Unknown Album",
                AlbumId = Track.UnknownAlbumBucketId,
                TrackNumber = 0,
                DiscNumber = 1,
                Duration = TimeSpan.FromMinutes(3),
                Codec = "PCM",
                SampleRate = 44100,
                BitsPerSample = 16,
                Bitrate = 1411,
            });
        }
        return tracks;
    }

    private static (AlbumDetailViewModel Vm, AlbumDetailView View, Window Win) Mount(List<Track> tracks)
    {
        EnsureAppStyles();

        var album = new Album
        {
            Id = Track.UnknownAlbumBucketId,
            Name = "Unknown Album",
            Artist = "Unknown Artist",
            TrackCount = tracks.Count,
            TotalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks)),
            Tracks = tracks,
        };

        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);

        var vm = new AlbumDetailViewModel(album, player, persistence, lib, sidebar, new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        return (vm, view, win);
    }

    private static int RealizedRows(AlbumDetailView view) =>
        view.GetVisualDescendants().OfType<ListBoxItem>().Count();

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 30000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task HugeUnknownAlbumBucket_FirstPaintRealizesOnlyOneChunk_ThenStreamsAll()
    {
        const int total = 800;
        var (_, view, win) = Mount(UntaggedWavTracks(total));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        win.Show(); // synchronous initial layout pass — this is the "click frame"
        sw.Stop();

        var atFirstPaint = RealizedRows(view);
        _output.WriteLine($"First paint: {sw.ElapsedMilliseconds} ms, realized rows: {atFirstPaint}");

        // The click that used to freeze the app must realize only the first chunk.
        Assert.Equal(Chunk, atFirstPaint);

        // The remainder streams in on background dispatcher passes.
        await PumpUntil(() => RealizedRows(view) == total);
        Assert.Equal(total, RealizedRows(view));

        win.Close();
    }

    [AvaloniaFact]
    public void NormalAlbum_StillFullyRealizedInOnePass()
    {
        var (_, view, win) = Mount(UntaggedWavTracks(150));

        win.Show();

        // Below the chunk threshold nothing changes: all rows land on first paint.
        Assert.Equal(150, RealizedRows(view));

        win.Close();
    }

    [AvaloniaFact]
    public void TrackRow_RightClick_OpensSharedMenuWithFullItemSet()
    {
        var (_, view, win) = Mount(UntaggedWavTracks(20));
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var item = view.GetVisualDescendants().OfType<ListBoxItem>().First();
        item.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = item });

        // The shared menu is attached to the row it was opened on and is open.
        var menu = item.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen, "shared track menu should be open after ContextRequested");

        // Same item set the per-row XAML menu had (16 items; optional ones are
        // visible because AlbumDetailView passes every optional command).
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.True(items.Count >= 14, $"expected the full track menu, got {items.Count} items");
        Assert.Equal("Play", items.First().Header as string);
        var remove = items.Last();
        Assert.Equal("Remove from Library", remove.Header as string);
        Assert.Contains("danger", remove.Classes);

        menu.Close();
        win.Close();
    }
}
