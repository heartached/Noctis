using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (f7uh, "hiding lyrics"): island ⋯ ▸ Lyrics Display ▸ Hide Lyrics shows the lyrics
/// page in its centred player look even when the song has lyrics, and brings the split
/// look back when switched off. Same layout path as the no-lyrics centring (#204 queue
/// inset, #211 island fit); the lyrics themselves are left alone (no search, no change).
/// </summary>
[Collection("MetadataServiceStatics")]
public class LyricsPageHideLyricsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public int Calls;
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<LrcLibResult?>(null);
        }
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new List<LrcLibResult>());
        }
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static (LyricsViewModel Vm, PlayerViewModel Player, StubLrcLib Lrc) MakeViewModel()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lrc = new StubLrcLib();
        var vm = new LyricsViewModel(player, lrc, new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        player.CurrentTrack = new Track
        {
            Title = "Hold Me Down",
            Artist = "Daniel Caesar",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-hide-{Guid.NewGuid():N}.mp3"),
            SourceType = SourceType.Local,
        };
        player.IsLyricsPageActive = true;
        return (vm, player, lrc);
    }

    private static async Task PumpAsync(int ms = 900)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    /// <summary>Lets the (stubbed, empty) automatic search finish, then puts lyrics on the page.</summary>
    private static async Task ShowLyricsAsync(LyricsViewModel vm)
    {
        for (var i = 0; i < 40 && !vm.ShowSearchButton; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        await PumpAsync(300);
        vm.ShowSearchButton = false;
        vm.LyricLines.Add(new LyricLine { Text = "Could you, could you", Timestamp = TimeSpan.FromSeconds(1) });
        await PumpAsync();
        Assert.False(vm.IsNoLyricsCentered);
    }

    private static Rect DrawnRect(Visual control, Visual root)
    {
        var a = control.TranslatePoint(default, root)!.Value;
        var b = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), root)!.Value;
        return new Rect(a, b);
    }

    [AvaloniaFact]
    public async Task HideLyrics_CentresThePlayer_WithLyrics_AndShowBringsTheSplitBack()
    {
        var (vm, player, lrc) = MakeViewModel();
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            win.Show();
            await ShowLyricsAsync(vm);
            var stack = view.FindControl<StackPanel>("LeftContentStack")!;
            var right = view.FindControl<Grid>("RightPanel")!;
            var bar = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!;
            var island = bar.FindControl<Border>("IslandBorder")!;
            var search = bar.FindControl<Button>("LyricsSearchButton")!;
            Assert.Equal(1366 / 4.0, DrawnRect(stack, win).Center.X, 1.0);
            var callsBefore = lrc.Calls;

            player.LyricsPageHideLyrics = true;
            Assert.True(vm.IsLyricsHiddenOnPage);
            await PumpAsync();

            Assert.Equal(683, DrawnRect(stack, win).Center.X, 0);
            Assert.Equal(683, DrawnRect(island, win).Center.X, 0);
            Assert.Equal(0, right.Opacity);
            Assert.False(right.IsHitTestVisible);
            // The song HAS lyrics: no island Search Lyrics, and nothing searched or changed.
            Assert.False(search.IsVisible);
            Assert.Equal(callsBefore, lrc.Calls);
            Assert.False(vm.IsSearching);
            Assert.Single(vm.LyricLines);
            Assert.False(vm.IsNoLyricsCentered);

            player.LyricsPageHideLyrics = false;
            await PumpAsync();
            Assert.Equal(1366 / 4.0, DrawnRect(stack, win).Center.X, 1.0);
            Assert.Equal(1, right.Opacity);
            Assert.True(right.IsHitTestVisible);
            Assert.Single(vm.LyricLines);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task HideLyrics_CentresBesideTheOpenQueue_AndTheIslandFits()
    {
        var (vm, player, _) = MakeViewModel();
        player.QueuePanelWidth = 330;
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            win.Show();
            await ShowLyricsAsync(vm);
            player.LyricsPageHideLyrics = true;
            player.IsQueuePopupOpen = true;
            await PumpAsync();

            var stack = view.FindControl<StackPanel>("LeftContentStack")!;
            var island = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!.FindControl<Border>("IslandBorder")!;
            var queueLeft = 1366 - 330 - 8;
            Assert.Equal(queueLeft / 2.0, DrawnRect(stack, win).Center.X, 0);
            Assert.Equal(queueLeft / 2.0, DrawnRect(island, win).Center.X, 0);
            Assert.True(DrawnRect(island, win).Right <= queueLeft, "the island runs under the queue");
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task HideLyrics_InNarrowMode_FadesTheLyricsBelowTheHeader()
    {
        var (vm, player, _) = MakeViewModel();
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 800, Height = 700, Content = view };
        try
        {
            win.Show();
            await ShowLyricsAsync(vm);
            var right = view.FindControl<Grid>("RightPanel")!;
            Assert.Equal(1, right.Opacity);

            player.LyricsPageHideLyrics = true;
            await PumpAsync();
            Assert.Equal(0, right.Opacity);
            Assert.False(right.IsHitTestVisible);

            // Widening past the breakpoint keeps them hidden, now in the centred look.
            win.Width = 1366;
            await PumpAsync();
            Assert.Equal(0, right.Opacity);
            Assert.Equal(683, DrawnRect(view.FindControl<StackPanel>("LeftContentStack")!, win).Center.X, 0);

            player.LyricsPageHideLyrics = false;
            await PumpAsync();
            Assert.Equal(1, right.Opacity);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task HideLyrics_SurvivesSaveAndReload_AndReachesThePlayer()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(),
            new NoOpPlayHistoryService());
        await settings.LoadAsync();
        settings.SetPlayer(player);
        Assert.False(new AppSettings().LyricsPageHideLyrics);
        Assert.False(settings.LyricsPageHideLyrics);

        settings.ToggleLyricsPageHideLyricsCommand.Execute(null);
        Assert.True(settings.LyricsPageHideLyrics);
        Assert.True(player.LyricsPageHideLyrics);
        await settings.SaveAsync();

        var reloaded = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(),
            new NoOpPlayHistoryService());
        await reloaded.LoadAsync();
        Assert.True(reloaded.LyricsPageHideLyrics);
        Assert.True(reloaded.GetSettings().LyricsPageHideLyrics);
    }
}
