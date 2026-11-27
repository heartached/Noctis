using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
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
/// Mini player Queue drawer scroll lag (09-16). Two measured causes, both pinned here:
///
/// 1. The row list was a plain StackPanel, so every queued track inflated a full row the
///    moment it was added — 1.6-2.5 ms each, 157-245 ms for a hundred. StreamingFill only
///    spread that over ~10 dispatcher turns, which put a ~20 ms stall inside most of the
///    glide frames of a wheel notch. Virtualized, the whole fill measured 13-24 ms and only
///    the rows in the ~155px viewport are real.
/// 2. Every overflowing row title lapped its marquee on the frame clock once the 7 s rest
///    pause elapsed, including rows scrolled out of view.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerQueueScrollTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerQueueScrollTests(ITestOutputHelper output) => _out = output;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }
    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
    }
    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private static (MiniPlayerWindow win, MiniPlayerViewModel vm, List<Track> tracks) Open()
    {
        EnsureAppResources();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);

        // 102 tracks: one plays, 101 land in UpNext — one past the 100-row preview cap.
        var tracks = Enumerable.Range(0, 102).Select(i => new Track
        {
            Title = $"You're On Your Own, Kid (Taylor's Version) [From The Vault] {i}",
            Artist = "Taylor Swift feat. Somebody Else With A Long Name",
            FilePath = $@"C:\t{i}.flac",
        }).ToList();
        player.ReplaceQueueAndPlay(tracks, 0);

        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        return (win, vm, tracks);
    }

    [AvaloniaFact]
    public async Task QueueDrawer_IsVirtualized_AndFillsInOneGo()
    {
        var (win, vm, _) = Open();
        await PumpFor(300);
        try
        {
            vm.ToggleQueueDrawerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            // The whole queue is available immediately — no streamed slices, so the
            // scrollbar does not grow under the user's thumb while they scroll.
            Assert.Equal(100, vm.QueuePreview.Count);
            Assert.True(vm.QueuePreviewTruncated);

            var sheet = win.FindControl<Border>("DrawerSheet")!;
            sheet.Height = 200; // headless windows do not grow; the sheet's height comes from the resize
            win.UpdateLayout();
            await PumpFor(800);

            var items = sheet.GetVisualDescendants().OfType<ItemsControl>()
                .First(ic => ReferenceEquals(ic.ItemsSource, vm.QueuePreview));
            Assert.Equal(100, items.ItemCount);
            var panel = items.GetVisualDescendants().OfType<Panel>()
                .First(p => p.GetVisualParent() is ItemsPresenter);
            Assert.IsType<VirtualizingStackPanel>(panel);

            // Only the handful of rows inside the viewport are real controls.
            var rows = items.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("mini-queue-row"));
            var scroller = items.FindAncestorOfType<ScrollViewer>()!;
            var rowsInView = (int)Math.Ceiling(scroller.Viewport.Height / 48.0) + 2;
            _out.WriteLine($"liveRows={rows} of {items.ItemCount}, viewport={scroller.Viewport}, extent={scroller.Extent}");
            Assert.InRange(rows, 1, rowsInView);
            // The scrollbar still spans the whole queue.
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 10,
                $"extent {scroller.Extent.Height} should cover all 100 rows");
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>Owner 11-27: "I Knew It, I Knew You (Acoustic V..." ran under the play button
    /// with no ellipsis. Rows now use the main queue rows' title line (LeadTailPanel): the
    /// title trims with an ellipsis before the button, no marquee per row, and the overlay
    /// scroll bar has its own gutter instead of covering the buttons.</summary>
    [AvaloniaFact]
    public async Task QueueDrawer_LongTitlesTrimBeforeThePlayButton_AndTheScrollBarClearsIt()
    {
        var (win, vm, _) = Open();
        await PumpFor(300);
        try
        {
            vm.ToggleQueueDrawerCommand.Execute(null);
            await PumpFor(200);
            var sheet = win.FindControl<Border>("DrawerSheet")!;
            sheet.Height = 200;
            win.UpdateLayout();
            await PumpFor(400);

            Assert.Empty(sheet.GetVisualDescendants().OfType<MarqueeTextBlock>());
            var scroller = win.FindControl<ScrollViewer>("QueueScroller")!;
            var rows = sheet.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("mini-queue-row")).ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows.Take(3))
            {
                var title = row.GetVisualDescendants().OfType<LeadTailPanel>().First()
                    .Children.OfType<TextBlock>().First();
                var play = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("glass-circle"));
                Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
                var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), row)!.Value.X;
                var playLeft = play.TranslatePoint(default, row)!.Value.X;
                var playRight = play.TranslatePoint(new Point(play.Bounds.Width, 0), scroller)!.Value.X;
                _out.WriteLine($"title right {titleRight:0.0}, play {playLeft:0.0}..{playRight:0.0}, viewport {scroller.Viewport.Width:0.0}");
                Assert.True(titleRight <= playLeft, "the title runs under the play button");
                Assert.True(playRight <= scroller.Viewport.Width - 8, "the scroll bar covers the play button");
            }
        }
        finally
        {
            win.Close();
        }
    }
}
