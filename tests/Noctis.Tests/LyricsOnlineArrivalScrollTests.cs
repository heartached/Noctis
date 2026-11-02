using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// GitHub #155: open the lyrics page on a song with no lyrics, late in the song; Noctis fetches
/// lyrics online and shows them — but parked at the top, every line around the playing one faded
/// out of the ±9 window, until the next line change. The online result was applied with the
/// scroll asked for on the spot, before the new lines were laid out, and the request was marked
/// done all the same. The page must land on the playing line once the lines are laid out.
/// </summary>
public class LyricsOnlineArrivalScrollTests
{
    private const int LineCount = 60;
    private const int SecondsPerLine = 3;

    private sealed class StubLrcLib : ILrcLibService
    {
        public LrcLibResult? Answer;
        // A real lookup takes a network round trip: the page has long settled when it lands.
        public async Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            await Task.Delay(500, ct);
            return Answer;
        }
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
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
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static string Lrc() => string.Join("\n", Enumerable.Range(0, LineCount).Select(i =>
        $"[{i * SecondsPerLine / 60:00}:{i * SecondsPerLine % 60:00}.00]Line {i} of the song" + (i % 4 == 3 ? " that runs on long enough to wrap" : "")));

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int ms) => await PumpUntilAsync(() => false, ms);

    [AvaloniaTheory]
    [InlineData(true, false)] // the Search button, during playback
    [InlineData(false, true)] // Settings ▸ Lyrics ▸ Automatically download lyrics, paused
    public async Task LyricsFetchedMidSong_LandOnThePlayingLine(bool playing, bool autoDownload)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics155-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrcLib = new StubLrcLib();
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        var track = new Track
        {
            Title = "No Lyrics Yet",
            Artist = "Someone",
            Duration = TimeSpan.FromSeconds(LineCount * SecondsPerLine),
            FilePath = Path.Combine(dir, "song.mp3"),
        };
        player.Duration = track.Duration;
        player.CurrentTrack = track;
        // Close to the end, as in the report.
        player.Position = TimeSpan.FromSeconds(50 * SecondsPerLine + 1);
        if (playing) player.State = PlaybackState.Playing;
        player.LyricsAutoDownload = autoDownload;
        var answer = new LrcLibResult
        {
            TrackName = track.Title,
            ArtistName = track.Artist,
            Duration = track.Duration.TotalSeconds,
            SyncedLyrics = Lrc(),
        };
        if (autoDownload) lrcLib.Answer = answer;

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        try
        {
            win.Show();
            if (!autoDownload)
            {
                await PumpUntilAsync(() => vm.ShowSearchButton);
                await Pump(400); // settled: the page's own resize re-anchor has run

                Assert.Empty(vm.LyricLines);
                lrcLib.Answer = answer;
                vm.SearchLyricsCommand.Execute(null);
            }
            await PumpUntilAsync(() => vm.LyricLines.Count > 0 && !vm.IsSearching);
            await Pump(600);

            var index = vm.ScrollLineIndex;
            Assert.True(index >= 45, $"the playing line is {index}");
            var sv = view.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            var panel = (Panel)view.FindControl<ItemsControl>("LyricsItemsControl")!
                .GetVisualDescendants().OfType<ItemsPresenter>().First()
                .GetVisualChildren().First();
            var line = panel.Children[index];
            var top = line.TranslatePoint(new Point(0, 0), sv.Content as Visual ?? sv)!.Value.Y;

            Assert.True(top >= sv.Offset.Y && top + line.Bounds.Height <= sv.Offset.Y + sv.Viewport.Height,
                $"line {index} at {top:0}..{top + line.Bounds.Height:0} is outside the viewport {sv.Offset.Y:0}..{sv.Offset.Y + sv.Viewport.Height:0}");
        }
        finally
        {
            win.Close();
            foreach (var ext in new[] { ".lrc", ".elrc" })
                AppWrittenSidecarRegistry.Default.Remove(Path.Combine(dir, "song" + ext));
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { File.Delete(Path.Combine(LyricsViewModel.LyricsCacheDir, track.Id + ".lrc")); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task LyricsFetchedMidSong_LandOnThePlayingLine_InTheSidePanel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics155p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrcLib = new StubLrcLib();
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new StubMetadata(), new TestPersistenceService(), new FakeLibraryService());
        var track = new Track
        {
            Title = "No Lyrics Yet",
            Artist = "Someone",
            Duration = TimeSpan.FromSeconds(LineCount * SecondsPerLine),
            FilePath = Path.Combine(dir, "song.mp3"),
        };
        player.Duration = track.Duration;
        player.CurrentTrack = track;
        player.Position = TimeSpan.FromSeconds(50 * SecondsPerLine + 1);

        var view = new LyricsPanelView { DataContext = vm };
        var win = new Window { Width = 360, Height = 720, Content = view };
        try
        {
            win.Show();
            vm.EnsureLyricsForCurrentTrack();
            await PumpUntilAsync(() => vm.ShowSearchButton);
            await Pump(400);
            lrcLib.Answer = new LrcLibResult
            {
                TrackName = track.Title,
                ArtistName = track.Artist,
                Duration = track.Duration.TotalSeconds,
                SyncedLyrics = Lrc(),
            };
            vm.SearchLyricsCommand.Execute(null);
            await PumpUntilAsync(() => vm.LyricLines.Count > 0 && !vm.IsSearching);
            await Pump(600);

            var index = vm.ScrollLineIndex;
            Assert.True(index >= 45, $"the playing line is {index}");
            var sv = view.FindControl<ScrollViewer>("PanelScrollViewer")!;
            var panel = (Panel)view.FindControl<ItemsControl>("PanelItemsControl")!
                .GetVisualDescendants().OfType<ItemsPresenter>().First()
                .GetVisualChildren().First();
            var line = panel.Children[index];
            var top = line.TranslatePoint(new Point(0, 0), sv.Content as Visual ?? sv)!.Value.Y;

            Assert.True(top >= sv.Offset.Y && top + line.Bounds.Height <= sv.Offset.Y + sv.Viewport.Height,
                $"line {index} at {top:0}..{top + line.Bounds.Height:0} is outside the viewport {sv.Offset.Y:0}..{sv.Offset.Y + sv.Viewport.Height:0}");
        }
        finally
        {
            win.Close();
            foreach (var ext in new[] { ".lrc", ".elrc" })
                AppWrittenSidecarRegistry.Default.Remove(Path.Combine(dir, "song" + ext));
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { File.Delete(Path.Combine(LyricsViewModel.LyricsCacheDir, track.Id + ".lrc")); } catch { }
        }
    }
}
