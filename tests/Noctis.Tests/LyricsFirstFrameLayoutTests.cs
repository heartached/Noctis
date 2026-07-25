using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The lyrics page sizes its cover, left column and lyric text from the window in
/// <c>UpdateResponsiveLayout</c>. The stretched playback bar lives inside that left
/// column, so if the column is still at its XAML authoring width when the page first
/// renders, the bar is drawn wide and then snaps narrower — the visible shrink/move
/// on entering the lyrics page.
/// </summary>
public class LyricsFirstFrameLayoutTests
{
    private readonly ITestOutputHelper _output;

    public LyricsFirstFrameLayoutTests(ITestOutputHelper output) => _output = output;

    /// <summary>The width baked into LyricsView.axaml for LeftContentStack.</summary>
    private const double AuthoringStackWidth = 760;

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

    private static LyricsViewModel MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    private static double BarWidth(LyricsView view) =>
        view.GetVisualDescendants().OfType<PlaybackBarView>().FirstOrDefault()?.Bounds.Width ?? -1;

    private static double StackWidth(LyricsView view) =>
        view.FindControl<StackPanel>("LeftContentStack")?.Bounds.Width ?? -1;

    /// <summary>Mounts the page the way navigation does and reports the very first
    /// laid-out geometry alongside the settled geometry.</summary>
    [AvaloniaFact]
    public void EnteringTheLyricsPage_DoesNotRenderTheBarAtTheAuthoringWidth()
    {
        var vm = MakeViewModel();
        var view = new LyricsView { DataContext = vm };

        // A realistic desktop window: the responsive maths resolves well below 760.
        var win = new Window { Width = 1600, Height = 900 };
        try
        {
            win.Show();

            // Content swap, exactly as the ContentControl does on navigation.
            win.Content = view;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var firstStack = StackWidth(view);
            var firstBar = BarWidth(view);

            for (var i = 0; i < 40; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            var settledStack = StackWidth(view);
            var settledBar = BarWidth(view);

            _output.WriteLine($"first  frame: stack={firstStack:F1} bar={firstBar:F1}");
            _output.WriteLine($"settled     : stack={settledStack:F1} bar={settledBar:F1}");

            Assert.True(settledStack > 0, "harness never laid the page out");
            Assert.NotEqual(AuthoringStackWidth, settledStack);

            // The bar must never be presented at a width it is about to abandon.
            Assert.Equal(settledStack, firstStack, 1);
            Assert.Equal(settledBar, firstBar, 1);
        }
        finally
        {
            win.Close();
        }
    }
}
