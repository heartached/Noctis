using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
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
/// Owner 11-27: "lyrics flowing should be the same as the lyrics page". The mini player's
/// lyrics column runs the page's own scroll engine (LyricsScrollDriver, moved out of
/// LyricsView): for the same song both views take the same route, the same tempo spring and
/// put the sung line at the same place in their viewports; scrolling by hand pauses
/// following with the page's Follow pill and resume rule; Ctrl+wheel shifts the timing.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerLyricsFlowTests
{
    private readonly ITestOutputHelper _out;
    public MiniPlayerLyricsFlowTests(ITestOutputHelper output) => _out = output;

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

    private static void Tick(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(8);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Line start times with uneven gaps, so each line has its own hold (and spring).</summary>
    private static readonly double[] Gaps = { 2.0, 4.5, 1.2, 6.0, 3.0, 0.8, 5.0 };

    private static double TimeOf(int i) => Enumerable.Range(0, i).Sum(k => Gaps[k % Gaps.Length]);

    private static async Task<(LyricsViewModel Lyrics, PlayerViewModel Player, MiniPlayerViewModel Mini)> LoadSong()
    {
        EnsureAppResources();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-mini-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lrc = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            var t = TimeSpan.FromSeconds(TimeOf(i));
            lrc.Append($"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]Line number {i}\n");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "song.lrc"), lrc.ToString());

        var (vm, player) = LyricsPageGenreLinkTests.MakeLyrics();
        player.Duration = TimeSpan.FromSeconds(TimeOf(60) + 10);
        player.CurrentTrack = new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "A", Album = "B", FilePath = Path.Combine(dir, "song.mp3"),
        };
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !(vm.IsSynced && vm.LyricLines.Count > 50))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(vm.IsSynced, "the sidecar .lrc never loaded");
        var library = new FakeLibraryService();
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return (vm, player, new MiniPlayerViewModel(player, vm, settings, library));
    }

    /// <summary>Plays into line <paramref name="index"/> (a line with a gap of 2 s or more,
    /// so the view model's scroll lead stays on it).</summary>
    private static void GoTo(PlayerViewModel player, LyricsViewModel lyrics, int index, int settleMs = 60)
    {
        player.Position = TimeSpan.FromSeconds(TimeOf(index) + 0.2);
        Tick(settleMs);
        Assert.Equal(index, lyrics.ScrollLineIndex);
    }

    /// <summary>A hand scroll: an offset the engine did not write, toward whichever end has room,
    /// once the glide in flight has landed.</summary>
    private static void HandScroll(MiniPlayerWindow win, ScrollViewer scroller)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (win.LyricsScrollEngine.IsAnimating && Environment.TickCount64 < deadline) Tick(20);
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var y = scroller.Offset.Y;
        scroller.Offset = new Vector(0, y >= 150 ? y - 150 : Math.Min(max, y + 150));
        Tick(50);
    }

    private static MiniPlayerWindow ShowMiniLyrics(MiniPlayerViewModel mini)
    {
        var win = new MiniPlayerWindow { DataContext = mini, Width = 640, Height = 412 };
        win.Show();
        Tick(500);
        Assert.Equal(MiniPlayerForm.Lyrics, mini.Form);
        return win;
    }

    private static LyricsScrollDriver PageEngine(LyricsView view) =>
        (LyricsScrollDriver)typeof(LyricsView).GetField("_scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    /// <summary>Where line <paramref name="index"/>'s centre sits, as a share of the viewport.</summary>
    private static double AnchorShare(ScrollViewer scroller, ItemsControl items, int index)
    {
        var container = items.ContainerFromIndex(index)!;
        var top = container.TranslatePoint(new Point(0, 0), scroller)!.Value.Y;
        return (top + container.Bounds.Height / 2) / scroller.Viewport.Height;
    }

    [AvaloniaFact]
    public async Task SameSong_BothViews_SameRoute_SameSpring_SameAnchor()
    {
        var (lyrics, player, mini) = await LoadSong();
        var page = new LyricsView { DataContext = lyrics };
        var pageWin = new Window { Width = 1400, Height = 800, Content = page };
        pageWin.Show();
        var miniWin = ShowMiniLyrics(mini);
        try
        {
            Tick(600);
            var pageEngine = PageEngine(page);
            var miniEngine = miniWin.LyricsScrollEngine;
            Assert.NotSame(pageEngine, miniEngine);
            Assert.IsType<LyricsScrollDriver>(miniEngine); // one engine, two hosts

            var pageScroller = page.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            var pageItems = page.FindControl<ItemsControl>("LyricsItemsControl")!;
            var miniScroller = miniWin.FindControl<ScrollViewer>("LyricsScroll")!;
            var miniItems = miniWin.FindControl<ItemsControl>("LyricsItems")!;

            foreach (var index in new[] { 14, 15, 17, 18, 20 })
            {
                GoTo(player, lyrics, index);      // a line advance, seconds after the last one
                Assert.Equal(LyricsScrollDriver.Route.Glide, pageEngine.LastRoute);
                Assert.Equal(LyricsScrollDriver.Route.Glide, miniEngine.LastRoute);
                var expected = LineMotion.ForHold(LyricsTimeline.HoldMs(lyrics.LyricLines, index));
                Assert.Equal(expected, pageEngine.LastSpring);
                Assert.Equal(expected, miniEngine.LastSpring);
                Tick(1500); // let both glides land
                var pageShare = AnchorShare(pageScroller, pageItems, index);
                var miniShare = AnchorShare(miniScroller, miniItems, index);
                _out.WriteLine($"line {index}: hold {LyricsTimeline.HoldMs(lyrics.LyricLines, index):0} ms, spring ζ{expected.Zeta:0.00} ωn{expected.NaturalFrequency:0.0}; " +
                               $"anchor page {pageShare:0.000} mini {miniShare:0.000}");
                Assert.Equal(LyricsScrollDriver.PageAnchorRatio, pageShare, 2);
                Assert.Equal(LyricsScrollDriver.PageAnchorRatio, miniShare, 2);
            }

            // Updates on top of each other (a scrub) chase in both.
            player.Position = TimeSpan.FromSeconds(TimeOf(28) + 0.2);
            Tick(40);
            player.Position = TimeSpan.FromSeconds(TimeOf(29) + 0.2);
            Tick(40);
            Assert.Equal(LyricsScrollDriver.Route.Chase, pageEngine.LastRoute);
            Assert.Equal(LyricsScrollDriver.Route.Chase, miniEngine.LastRoute);
        }
        finally
        {
            miniWin.Close();
            pageWin.Close();
            lyrics.Dispose();
        }
    }

    [AvaloniaFact]
    public void Routes_AndSprings_AreTheSharedRules()
    {
        Assert.Equal(LyricsScrollDriver.Route.Glide, LyricsScrollDriver.ChooseRoute(double.MaxValue, double.MaxValue));
        Assert.Equal(LyricsScrollDriver.Route.Chase, LyricsScrollDriver.ChooseRoute(LyricsScrollDriver.ScrubWindowMs - 1, double.MaxValue));
        Assert.Equal(LyricsScrollDriver.Route.Glide, LyricsScrollDriver.ChooseRoute(LyricsScrollDriver.ScrubWindowMs + 1, double.MaxValue));
        Assert.Equal(LyricsScrollDriver.Route.Chase, LyricsScrollDriver.ChooseRoute(double.MaxValue, LyricsScrollDriver.SeekFollowWindowMs - 1));
        Assert.Equal(LineMotion.CascadeDelayPerLineMs, LyricsScrollDriver.CascadeDelayPerLineMs);
        var lines = Enumerable.Range(0, 5).Select(i => new LyricLine { Timestamp = TimeSpan.FromSeconds(TimeOf(i)), Text = "x" }).ToList();
        for (var i = 0; i < 4; i++)
            Assert.Equal(LineMotion.ForHold(Gaps[i] * 1000), LyricsScrollDriver.SpringFor(lines, i));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScrollingByHand_PausesFollowing_WithThePagesFollowPill(bool autoReturn)
    {
        var (lyrics, player, mini) = await LoadSong();
        var win = ShowMiniLyrics(mini);
        try
        {
            lyrics.AutoReturnToCurrentLine = autoReturn;
            GoTo(player, lyrics, 20, 1500);
            var scroller = win.FindControl<ScrollViewer>("LyricsScroll")!;
            var follow = win.FindControl<Button>("LyricsFollowButton")!;
            Assert.False(follow.IsVisible);

            // A hand scroll: an offset this engine did not write.
            HandScroll(win, scroller);
            Assert.True(lyrics.IsAutoFollowPaused);
            Assert.Equal(autoReturn, win.LyricsScrollEngine.AutoFollowResumePending);
            Assert.True(follow.IsEffectivelyVisible);
            Assert.Same(lyrics.ResumeAutoFollowCommand, follow.Command);

            // Line changes do not move the list while paused.
            var parked = scroller.Offset.Y;
            GoTo(player, lyrics, 21, 800);
            Assert.Equal(parked, scroller.Offset.Y, 1);

            // Follow: back to the sung line.
            follow.Command!.Execute(null);
            Tick(1500);
            Assert.False(lyrics.IsAutoFollowPaused);
            Assert.False(win.LyricsScrollEngine.AutoFollowResumePending);
            var items = win.FindControl<ItemsControl>("LyricsItems")!;
            Assert.Equal(LyricsScrollDriver.PageAnchorRatio, AnchorShare(scroller, items, 21), 2);
        }
        finally
        {
            win.Close();
            lyrics.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task LeavingTheLyricsColumn_HandsFollowingBack()
    {
        var (lyrics, player, mini) = await LoadSong();
        var win = ShowMiniLyrics(mini);
        try
        {
            lyrics.AutoReturnToCurrentLine = false;
            GoTo(player, lyrics, 20, 1200);
            var scroller = win.FindControl<ScrollViewer>("LyricsScroll")!;
            HandScroll(win, scroller);
            Assert.True(lyrics.IsAutoFollowPaused);
            mini.ToggleLyricsFormCommand.Execute(null); // back to the card
            Tick(700);
            Assert.NotEqual(MiniPlayerForm.Lyrics, mini.Form);
            Assert.False(lyrics.IsAutoFollowPaused); // the page does not inherit the pause
        }
        finally
        {
            win.Close();
            lyrics.Dispose();
        }
    }

    private static PointerWheelEventArgs Wheel(Visual source, Visual root, double deltaY, KeyModifiers modifiers) =>
        new(source,
            new Avalonia.Input.Pointer(0, PointerType.Mouse, true),
            root,
            new Point(10, 10),
            0,
            new PointerPointProperties(
                modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : RawInputModifiers.None,
                PointerUpdateKind.Other),
            modifiers,
            new Vector(0, deltaY))
        { RoutedEvent = InputElement.PointerWheelChangedEvent };

    [AvaloniaFact]
    public async Task CtrlWheel_ShiftsTheLyricsTiming_LikeThePage()
    {
        var (lyrics, player, mini) = await LoadSong();
        var win = ShowMiniLyrics(mini);
        try
        {
            GoTo(player, lyrics, 20, 1500);
            var scroller = win.FindControl<ScrollViewer>("LyricsScroll")!;
            var items = win.FindControl<ItemsControl>("LyricsItems")!;
            var before = scroller.Offset.Y;
            var track = player.CurrentTrack!;

            var up = Wheel(items, win, +1, KeyModifiers.Control);
            items.RaiseEvent(up);
            Tick(300);
            Assert.True(up.Handled);
            Assert.Equal(-500, track.LyricsOffsetMs); // the page's step (LyricsViewModel)
            Assert.Equal(before, scroller.Offset.Y, 0.5);
            Assert.False(lyrics.IsAutoFollowPaused);

            var down = Wheel(items, win, -1, KeyModifiers.Control);
            items.RaiseEvent(down);
            items.RaiseEvent(Wheel(items, win, -1, KeyModifiers.Control));
            Tick(300);
            Assert.Equal(500, track.LyricsOffsetMs);
        }
        finally
        {
            win.Close();
            lyrics.Dispose();
        }
    }
}
