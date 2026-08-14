using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Closing the Lyrics split view must hand the window back at the size the user
/// came from (or the target form's canonical size), never leave it parked at the
/// lyrics-sized rect with a small form rattling around inside it.
/// </summary>
// Building a SettingsViewModel pushes the "use embedded artwork" setting into
// MetadataService's static mirror, so this class can't run beside the tests that
// deliberately flip it.
[Collection("MetadataServiceStatics")]
public class MiniPlayerLyricsResizeTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
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
        var app = Avalonia.Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml"),
        });
    }

    private static MiniPlayerViewModel MakeViewModel()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    /// <summary>Frame pump that also lets wall-clock time pass, so the eased window
    /// resize (280ms stopwatch-driven) actually progresses.</summary>
    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    [AvaloniaFact]
    public async Task ClosingLyricsEnteredByResize_StillShrinksTheWindow()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        // Reopened (or dragged) straight into the split view: no menu jump, so no
        // captured pre-lyrics size exists.
        var win = new MiniPlayerWindow { DataContext = vm, Width = 795, Height = 505 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);

            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(900);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            Assert.Equal(340, win.Width, 1.0);
            Assert.Equal(432, win.Height, 1.0);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task StaleCaptureFromAnEarlierLyricsSession_DoesNotReinflateTheWindow()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        // A big-but-valid Card (ratio below the LargeIcon line, width below the
        // lyrics threshold).
        var win = new MiniPlayerWindow { DataContext = vm, Width = 480, Height = 520 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.Equal(MiniPlayerForm.Card, vm.Form);

            // Menu → Lyrics captures (Card, 480, 520).
            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(700);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);

            // The user drags the split view NARROW enough to fall out of the lyrics
            // form — no menu involved, so nothing consumed the capture.
            win.Width = 500;
            win.Height = 412;
            await PumpFor(120);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);

            // They settle on a small card...
            win.Width = 340;
            win.Height = 432;
            await PumpFor(120);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);

            // ...then drag back INTO the split view (again no menu jump)...
            win.Width = 800;
            win.Height = 500;
            await PumpFor(120);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);

            // ...and close it from the menu. The window must come back small — not
            // "restored" to the 480x520 left over from the earlier lyrics session.
            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(900);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            Assert.Equal(340, win.Width, 1.0);
            Assert.Equal(432, win.Height, 1.0);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingLyrics_RestoresThePreLyricsSize()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.Equal(MiniPlayerForm.Card, vm.Form);

            // Menu → Lyrics: window animates to the canonical split-view size.
            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(700);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);
            Assert.Equal(640, win.Width, 1.0);
            Assert.Equal(412, win.Height, 1.0);

            // User drags the split view bigger while it is open.
            win.Width = 800;
            win.Height = 500;
            await PumpFor(120);
            Assert.Equal(MiniPlayerForm.Lyrics, vm.Form);

            // Menu → Hide Lyrics: back to the size the user came from.
            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(900);
            Assert.Equal(MiniPlayerForm.Card, vm.Form);
            Assert.Equal(340, win.Width, 1.0);
            Assert.Equal(432, win.Height, 1.0);
        }
        finally
        {
            win.Close();
        }
    }
}
