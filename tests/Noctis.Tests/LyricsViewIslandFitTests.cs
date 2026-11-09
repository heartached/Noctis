using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #211 (the lyrics-page player island must stay between the window's left edge and
/// the lyrics, also at 1366×768 full screen with many island extras) and GitHub #204 (with
/// no lyrics the centred player is centred in the room left beside the open queue, not
/// under it). Arranged headlessly at the reporters' window sizes.
/// </summary>
public class LyricsViewIslandFitTests
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

    private static (LyricsViewModel Vm, PlayerViewModel Player) MakeViewModel()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        player.CurrentTrack = new Track
        {
            Title = "Apna Friday Aayega",
            Artist = "Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-fit-{Guid.NewGuid():N}.mp3"),
            SourceType = SourceType.Local,
        };
        player.IsLyricsPageActive = true;
        return (vm, player);
    }

    /// <summary>The reporter's island (#211 screenshot): every opt-in extra on, 700px of buttons.</summary>
    private static void ShowManyExtras(PlayerViewModel player)
    {
        player.IslandShowSkipButtons = true;
        player.IslandShowSleepTimer = true;
        player.IslandShowRepeat = true;
        player.IslandShowShuffle = true;
        player.IslandShowEqualizer = true;
        player.IslandShowFavorite = true;
        player.IslandShowQuickPlaylist = true;
        player.IslandShowTrash = true;
        player.IslandShowMiniPlayer = true;
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

    private static async Task SettleAsync(LyricsViewModel vm)
    {
        for (var i = 0; i < 40 && !vm.ShowSearchButton; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        await PumpAsync();
    }

    /// <summary>Where <paramref name="control"/> is drawn in the window, render transforms included.</summary>
    private static Rect DrawnRect(Visual control, Visual root)
    {
        var a = control.TranslatePoint(default, root)!.Value;
        var b = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), root)!.Value;
        return new Rect(a, b);
    }

    private static Rect LayoutRect(Visual control, Visual root) =>
        new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);

    [AvaloniaFact]
    public async Task Island_StaysInsideThePlayerColumn_At1366x768_WithEveryExtra()
    {
        var (vm, player) = MakeViewModel();
        ShowManyExtras(player);
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            // Lyrics on screen: the split layout of the screenshot.
            vm.ShowSearchButton = false;
            vm.LyricLines.Add(new LyricLine { Text = "line", Timestamp = TimeSpan.FromSeconds(1) });
            await PumpAsync();
            Assert.False(vm.IsNoLyricsCentered);

            var bar = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!;
            var island = bar.FindControl<Border>("IslandBorder")!;
            var drawn = DrawnRect(island, win);
            const double column = 1366 / 2.0;

            // Off neither edge of the column: not past the window's left edge, not over the lyrics.
            Assert.True(drawn.Left >= 0, $"island drawn from x={drawn.Left:F1}, off the window's left edge");
            Assert.True(drawn.Right <= column, $"island drawn to x={drawn.Right:F1}, over the lyrics column at {column}");
            // Centred in the column, and every button still inside the drawn pill.
            Assert.Equal(column / 2, drawn.Center.X, 0);
            foreach (var b in island.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
                Assert.True(drawn.Contains(DrawnRect(b, win)), $"{b.Name ?? b.GetType().Name} spills out of the island {drawn}");
            // The bar's own hit area stays off the lyrics too (it is Background=Transparent).
            var barRect = LayoutRect(bar, win);
            Assert.True(barRect.Left >= 0 && barRect.Right <= column, $"bar hit area {barRect} leaves the column");
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task Island_KeepsItsFullSize_WhenTheColumnHasRoom()
    {
        var (vm, player) = MakeViewModel();
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1600, Height = 900, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            vm.ShowSearchButton = false;
            vm.LyricLines.Add(new LyricLine { Text = "line", Timestamp = TimeSpan.FromSeconds(1) });
            await PumpAsync();

            var island = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!.FindControl<Border>("IslandBorder")!;
            Assert.Null(island.RenderTransform);
            Assert.Equal(island.Bounds.Width, DrawnRect(island, win).Width, 1);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task NoLyrics_CentresBesideTheOpenQueue_AndBackWhenItCloses()
    {
        var (vm, player) = MakeViewModel();
        ShowManyExtras(player);
        player.QueuePanelWidth = 330;
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            Assert.True(vm.IsNoLyricsCentered);

            var stack = view.FindControl<StackPanel>("LeftContentStack")!;
            var island = view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!.FindControl<Border>("IslandBorder")!;
            Assert.Equal(683, DrawnRect(stack, win).Center.X, 0);

            // The queue drawer covers its width plus its 8px margin at the right edge.
            player.IsQueuePopupOpen = true;
            await PumpAsync();
            var queueLeft = 1366 - 330 - 8;
            Assert.Equal(queueLeft / 2.0, DrawnRect(stack, win).Center.X, 0);
            Assert.Equal(queueLeft / 2.0, DrawnRect(island, win).Center.X, 0);
            Assert.True(DrawnRect(stack, win).Right <= queueLeft, "the player runs under the queue");
            Assert.True(DrawnRect(island, win).Right <= queueLeft, "the island runs under the queue");
            Assert.True(DrawnRect(island, win).Left >= 0);

            player.IsQueuePopupOpen = false;
            await PumpAsync();
            Assert.Equal(683, DrawnRect(stack, win).Center.X, 0);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task NoLyrics_SmallWindowAndWideQueue_KeepsThePlayerOnScreen()
    {
        var (vm, player) = MakeViewModel();
        player.QueuePanelWidth = QueuePanelMetrics.Max;
        player.IsQueuePopupOpen = true;
        var view = new Noctis.Views.LyricsView { DataContext = vm };
        var win = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            win.Show();
            await SettleAsync(vm);
            Assert.True(vm.IsNoLyricsCentered);

            // 1000 − 728 leaves too little room beside the queue: the player keeps a usable
            // width, fully on screen, and as far left as that allows.
            var stack = DrawnRect(view.FindControl<StackPanel>("LeftContentStack")!, win);
            var island = DrawnRect(view.FindControl<Noctis.Views.PlaybackBarView>("LyricsPlaybackBar")!.FindControl<Border>("IslandBorder")!, win);
            Assert.True(stack.Left >= 0 && stack.Right <= 1000, $"stack {stack}");
            Assert.True(island.Left >= 0 && island.Right <= 1000, $"island {island}");
            Assert.True(stack.Center.X < 500, $"stack centre {stack.Center.X} did not move away from the queue");
        }
        finally
        {
            win.Close();
        }
    }
}
