using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Entering the lyrics page must present its playback island at its final geometry on
/// the first frame. ToggleLyrics drives IsLyricsPageActive true→false→true in one go
/// (OnCurrentViewChanged wires it, ClearAllTopBarActions unwires it, then it is wired
/// again), and the page's bar is already attached by then — so any transition on the
/// island's width turns that churn into a visible glide from the wide layout down to
/// the compact one. This mounts the real view through the CachedViewLocator, navigates
/// away and back (the trip the user repeats), and samples the island every frame.
/// </summary>
public class LyricsEntryBarGeometryTests
{
    private readonly ITestOutputHelper _output;

    public LyricsEntryBarGeometryTests(ITestOutputHelper output) => _output = output;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds)
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

    private static (LyricsViewModel Vm, PlayerViewModel Player) MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        return (vm, player);
    }

    private sealed record Sample(int Frame, double IslandWidth, double BarWidth, double StackWidth,
        double CoverWidth, double BarTop);

    private static Sample? Probe(Visual root, int frame)
    {
        var bar = root.GetVisualDescendants().OfType<PlaybackBarView>().FirstOrDefault();
        if (bar == null) return null;
        var island = bar.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "IslandBorder");
        if (island == null) return null;

        var view = root.GetVisualDescendants().OfType<LyricsView>().FirstOrDefault();
        var stack = view?.FindControl<StackPanel>("LeftContentStack");
        var cover = view?.FindControl<Border>("AlbumArtBorder");
        var top = bar.TranslatePoint(new Point(0, 0), root)?.Y ?? -1;

        return new Sample(frame, island.Bounds.Width, bar.Bounds.Width,
            stack?.Bounds.Width ?? -1, cover?.Bounds.Width ?? -1, top);
    }

    [AvaloniaFact]
    public void EnteringLyrics_IslandNeverRendersAtAWidthItAbandons()
    {
        var (vm, player) = MakeViewModel();
        player.CurrentTrack = new Track { Title = "Probe", Artist = "Test", Album = "Test" };

        var locator = new CachedViewLocator(new Dictionary<Type, Func<Control>>
        {
            [typeof(LyricsViewModel)] = () => new LyricsView(),
        });

        var host = new ContentControl();
        host.DataTemplates.Add(locator);

        var win = new Window { Width = 1600, Height = 900, Content = host };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            // ── Exactly what MainWindowViewModel.ToggleLyrics does, in order ──
            void EnterLyrics()
            {
                host.Content = vm;                  // CurrentView = _lyricsVm
                player.IsLyricsPageActive = true;   // WireLyricsPageToPlayer (OnCurrentViewChanged)
                player.IsLyricsPageActive = false;  // ClearAllTopBarActions -> Unwire
                player.IsLyricsPageActive = true;   // WireLyricsPageToPlayer
            }

            // First visit, then navigate away and back — the cached view is reused, so
            // the return trip is the case the user actually repeats.
            EnterLyrics();
            for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

            host.Content = new TextBlock { Text = "albums" };
            player.IsLyricsPageActive = false;
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

            EnterLyrics();

            var samples = new List<Sample>();
            for (var frame = 0; frame < 30; frame++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var s = Probe(win, frame);
                if (s != null) samples.Add(s);
            }

            foreach (var s in samples.Take(12))
                _output.WriteLine($"frame {s.Frame,2}: island={s.IslandWidth,6:F1} bar={s.BarWidth,6:F1} " +
                                  $"stack={s.StackWidth,6:F1} cover={s.CoverWidth,6:F1} barTop={s.BarTop,7:F1}");

            Assert.NotEmpty(samples);
            var settled = samples[^1];
            _output.WriteLine($"settled: island={settled.IslandWidth:F1} stack={settled.StackWidth:F1} " +
                              $"cover={settled.CoverWidth:F1} barTop={settled.BarTop:F1}");

            var offenders = samples.Where(s =>
                Math.Abs(s.IslandWidth - settled.IslandWidth) > 1 ||
                Math.Abs(s.StackWidth - settled.StackWidth) > 1 ||
                Math.Abs(s.CoverWidth - settled.CoverWidth) > 1 ||
                Math.Abs(s.BarTop - settled.BarTop) > 1).ToList();

            foreach (var o in offenders)
                _output.WriteLine($"OFFENDER frame {o.Frame}: island={o.IslandWidth:F1} stack={o.StackWidth:F1} " +
                                  $"cover={o.CoverWidth:F1} barTop={o.BarTop:F1}");

            Assert.True(offenders.Count == 0,
                $"bar geometry was transient on {offenders.Count} frame(s): " +
                string.Join(", ", offenders.Select(o =>
                    $"f{o.Frame}(island={o.IslandWidth:F0},stack={o.StackWidth:F0},top={o.BarTop:F0})")));
        }
        finally
        {
            win.Close();
        }
    }
}
