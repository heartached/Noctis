using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #102: Ctrl/⌘+wheel over the lyrics shifts the song's lyrics by half a second per
/// notch (up = earlier, down = later). The offset is per track, kept in the library (never in
/// the lyric files), applied where the timeline is read, and compensated when a line is
/// clicked. The wheel must shift, not scroll, while a plain wheel keeps scrolling.
/// </summary>
public class LyricsOffsetTests
{
    // ── Harness (mirrors LyricsBackwardSeekTests) ──

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

    private const int LineCount = 31;
    private const int LineSpacingSeconds = 3;

    /// <summary>Synced LRC with a line every 3s from 0:00 to 1:30.</summary>
    private static string MakeLrc() => string.Join("\n",
        Enumerable.Range(0, LineCount).Select(i =>
        {
            var t = TimeSpan.FromSeconds(i * LineSpacingSeconds);
            return $"[{t.Minutes:00}:{t.Seconds:00}.00]Line {i}";
        }));

    private sealed record Mounted(LyricsViewModel Vm, PlayerViewModel Player, Track Playing, Track InLibrary, FakeLibraryService Library);

    /// <summary>Loads lyrics through the real embedded-tag path (the sidecar probes miss because
    /// FilePath does not exist). The player holds its own instance of the song and the library
    /// another, as after a reload — the library's is the one that gets saved.</summary>
    private static async Task<Mounted> Mount(string? synced = null, string? plain = null)
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var library = new FakeLibraryService();
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);

        var id = Guid.NewGuid();
        var filePath = Path.Combine(Path.GetTempPath(), "noctis-lyrics-offset-no-such-file.mp3");
        var playing = new Track { Id = id, Title = "Offset", Artist = "Test", FilePath = filePath };
        var inLibrary = new Track { Id = id, Title = "Offset", Artist = "Test", FilePath = filePath };
        if (synced != null) playing.SyncedLyrics = synced;
        if (plain != null) playing.Lyrics = plain;
        library.TrackList.Add(inLibrary);

        player.Duration = TimeSpan.FromSeconds(LineCount * LineSpacingSeconds);
        player.CurrentTrack = playing;
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && vm.LyricLines.Count == 0 && vm.UnsyncedLines.Count == 0)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(vm.LyricLines.Count > 0 || vm.UnsyncedLines.Count > 0, "harness failed to load lyrics");
        return new Mounted(vm, player, playing, inLibrary, library);
    }

    /// <summary>Timestamp of the line the view would highlight, or null if none is active.</summary>
    private static TimeSpan? ActiveTimestamp(LyricsViewModel vm) =>
        vm.ActiveLineIndex >= 0 && vm.ActiveLineIndex < vm.LyricLines.Count
            ? vm.LyricLines[vm.ActiveLineIndex].Timestamp
            : null;

    private static void Nudge(LyricsViewModel vm, double delta, int times)
    {
        for (var i = 0; i < times; i++)
            Assert.True(vm.NudgeLyricsOffset(delta));
    }

    private static async Task PumpUntil(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ── Timeline ──

    [AvaloniaFact]
    public async Task WheelUp_ShowsTheLyricsEarlier()
    {
        var m = await Mount(MakeLrc());
        m.Player.Position = TimeSpan.FromSeconds(29);
        Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(m.Vm));

        Nudge(m.Vm, +1, 4); // 2 s earlier: at 0:29 the 0:30 line is already up

        Assert.Equal(-2000, m.Playing.LyricsOffsetMs);
        Assert.Equal(TimeSpan.FromSeconds(30), ActiveTimestamp(m.Vm));
    }

    [AvaloniaFact]
    public async Task WheelDown_ShowsTheLyricsLater()
    {
        var m = await Mount(MakeLrc());
        m.Player.Position = TimeSpan.FromSeconds(29);
        Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(m.Vm));

        Nudge(m.Vm, -1, 6); // 3 s later: at 0:29 the 0:27 line has not come up yet

        Assert.Equal(3000, m.Playing.LyricsOffsetMs);
        Assert.Equal(TimeSpan.FromSeconds(24), ActiveTimestamp(m.Vm));

        // Playback moving on keeps the shift.
        m.Player.Position = TimeSpan.FromSeconds(33.5);
        Assert.Equal(TimeSpan.FromSeconds(30), ActiveTimestamp(m.Vm));
    }

    [AvaloniaFact]
    public async Task ClickingALine_SeeksToWhereTheLineIsShown()
    {
        var m = await Mount(MakeLrc());
        Nudge(m.Vm, +1, 4); // 2 s earlier

        var line = m.Vm.LyricLines.First(l => l.Timestamp == TimeSpan.FromSeconds(30));
        m.Vm.SeekToLineCommand.Execute(line);

        Assert.Equal(28_000, m.Player.Position.TotalMilliseconds, 1.0);
        Assert.Equal(TimeSpan.FromSeconds(30), ActiveTimestamp(m.Vm));
    }

    [AvaloniaFact]
    public async Task FractionalNotches_StepOncePerWholeNotch_AndTheOffsetIsClamped()
    {
        var m = await Mount(MakeLrc());

        for (var i = 0; i < 4; i++) m.Vm.NudgeLyricsOffset(+0.25);
        Assert.Equal(-LyricsViewModel.LyricsOffsetStepMs, m.Playing.LyricsOffsetMs);

        for (var i = 0; i < 200; i++) m.Vm.NudgeLyricsOffset(-1);
        Assert.Equal(LyricsViewModel.MaxLyricsOffsetMs, m.Playing.LyricsOffsetMs);
    }

    [AvaloniaFact]
    public async Task UnsyncedLyrics_IgnoreTheWheel()
    {
        var plain = await Mount(plain: "first line\nsecond line");
        Assert.False(plain.Vm.IsSynced);
        Assert.False(plain.Vm.NudgeLyricsOffset(+1));
        Assert.Equal(0, plain.Playing.LyricsOffsetMs);

        // Synced lyrics shown on the Plain tab have no timestamps on screen either.
        var synced = await Mount(MakeLrc());
        synced.Vm.SelectPlainLyricsCommand.Execute(null);
        Assert.False(synced.Vm.NudgeLyricsOffset(+1));
        Assert.Equal(0, synced.Playing.LyricsOffsetMs);
    }

    [AvaloniaFact]
    public void NoTrack_IgnoresTheWheel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        Assert.False(vm.NudgeLyricsOffset(+1));
    }

    // ── Persistence + feedback ──

    [AvaloniaFact]
    public async Task Nudges_UpdateTheLibraryTrack_SaveOnceDebounced_AndConfirm()
    {
        var m = await Mount(MakeLrc());
        var notices = new List<string>();
        m.Vm.ShowNotice = notices.Add;

        Nudge(m.Vm, +1, 3);

        Assert.Equal(-1500, m.Playing.LyricsOffsetMs);
        Assert.Equal(-1500, m.InLibrary.LyricsOffsetMs);
        Assert.Equal(Loc.T("Lyrics.OffsetEarlier", 1.5), notices[^1]);
        Assert.DoesNotContain("Lyrics.Offset", notices[^1]); // the key resolves

        // A burst is one library save, after the wheel stops.
        Assert.Equal(0, m.Library.SaveCount);
        await PumpUntil(() => m.Library.SaveCount > 0, 3000);
        await PumpUntil(() => false, 300);
        Assert.Equal(1, m.Library.SaveCount);

        Nudge(m.Vm, -1, 4);
        Assert.Equal(Loc.T("Lyrics.OffsetLater", 0.5), notices[^1]);
        Nudge(m.Vm, +1, 1);
        Assert.Equal(0, m.InLibrary.LyricsOffsetMs);
        Assert.Equal(Loc.T("Lyrics.OffsetInSync"), notices[^1]);
    }

    [Fact]
    public async Task Offset_SurvivesRestartAndRescan()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            // Restart: the real library.json save/load path.
            var track = new Track { Id = Guid.NewGuid(), Title = "T", FilePath = "x.mp3", LyricsOffsetMs = -1500 };
            await new PersistenceService(root).SaveLibraryAsync(new List<Track> { track });
            var reloaded = Assert.Single((await new PersistenceService(root).LoadLibraryAsync())!);
            Assert.Equal(-1500, reloaded.LyricsOffsetMs);

            // Rescan of a changed file: the fresh tag read inherits the user's state.
            var copy = typeof(LibraryService).GetMethod("CopyMutableTrackState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var fresh = new Track { Id = track.Id, Title = "T", FilePath = "x.mp3" };
            copy.Invoke(null, new object[] { reloaded, fresh });
            Assert.Equal(-1500, fresh.LyricsOffsetMs);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── Views: Ctrl+wheel shifts, a plain wheel scrolls ──

    private static PointerWheelEventArgs Wheel(Visual source, Visual root, double deltaY, KeyModifiers modifiers) =>
        new(source,
            new Pointer(0, PointerType.Mouse, true),
            root,
            new Point(10, 10),
            0,
            new PointerPointProperties(
                modifiers.HasFlag(KeyModifiers.Control) ? RawInputModifiers.Control : RawInputModifiers.None,
                PointerUpdateKind.Other),
            modifiers,
            new Vector(0, deltaY))
        { RoutedEvent = InputElement.PointerWheelChangedEvent };

    private static void Tick(int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Mounts a lyrics surface with the app's smooth scrolling on its ScrollViewer
    /// (the app turns it on for every ScrollViewer from Styles.axaml), parks playback mid-song
    /// and waits for the view to settle on the active line.</summary>
    private static async Task<(Mounted M, Window Win, ScrollViewer Scroller, Control Lines)> MountView(
        Func<Control> create, string scrollerName, string linesName, double width)
    {
        var m = await Mount(MakeLrc());
        var view = create();
        view.DataContext = m.Vm;
        var win = new Window { Width = width, Height = 900, Content = view };
        win.Show();
        var scroller = view.FindControl<ScrollViewer>(scrollerName)!;
        var lines = view.FindControl<Control>(linesName)!;
        SmoothScrollBehavior.SetIsEnabled(scroller, true);

        // 0:28 sits inside the 0:27 line even half a second either way, so a notch moves no
        // line and therefore no auto-follow scroll.
        m.Player.Position = TimeSpan.FromSeconds(28);
        Tick(1200);
        Assert.Equal(TimeSpan.FromSeconds(27), ActiveTimestamp(m.Vm));
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "lyrics must be scrollable");
        return (m, win, scroller, lines);
    }

    private static void AssertCtrlWheelShiftsAndPlainWheelScrolls(
        Mounted m, Window win, ScrollViewer scroller, Control lines)
    {
        var before = scroller.Offset.Y;

        var up = Wheel(lines, win, +1, KeyModifiers.Control);
        lines.RaiseEvent(up);
        Tick(600);
        Assert.True(up.Handled);
        Assert.Equal(-500, m.Playing.LyricsOffsetMs);
        Assert.Equal(before, scroller.Offset.Y, 0.5);
        Assert.False(m.Vm.IsAutoFollowPaused);

        var down = Wheel(lines, win, -1, KeyModifiers.Control);
        lines.RaiseEvent(down);
        down = Wheel(lines, win, -1, KeyModifiers.Control);
        lines.RaiseEvent(down);
        Tick(600);
        Assert.Equal(500, m.Playing.LyricsOffsetMs);
        Assert.Equal(before, scroller.Offset.Y, 0.5);
        Assert.False(m.Vm.IsAutoFollowPaused);

        // A plain notch still scrolls (toward whichever end has room) and shifts nothing.
        var plain = Wheel(lines, win, before > 0 ? +1 : -1, KeyModifiers.None);
        lines.RaiseEvent(plain);
        Tick(600);
        Assert.True(plain.Handled);
        Assert.NotEqual(before, scroller.Offset.Y);
        Assert.Equal(500, m.Playing.LyricsOffsetMs);
    }

    [AvaloniaFact]
    public async Task LyricsPage_CtrlWheelShiftsWithoutScrolling_PlainWheelScrolls()
    {
        var (m, win, scroller, lines) = await MountView(
            () => new LyricsView(), "LyricsScrollViewer", "LyricsItemsControl", 1600);
        try
        {
            AssertCtrlWheelShiftsAndPlainWheelScrolls(m, win, scroller, lines);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public async Task LyricsPanel_CtrlWheelShiftsWithoutScrolling_PlainWheelScrolls()
    {
        var (m, win, scroller, lines) = await MountView(
            () => new LyricsPanelView(), "PanelScrollViewer", "PanelItemsControl", 420);
        try
        {
            AssertCtrlWheelShiftsAndPlainWheelScrolls(m, win, scroller, lines);
        }
        finally
        {
            win.Close();
        }
    }

    // ── SmoothScrollBehavior contract ──

    private static (Window Win, ScrollViewer Scroller, Border Content) MountScroller()
    {
        var content = new Border { Height = 2000 };
        var scroller = new ScrollViewer { Content = content };
        var win = new Window { Width = 400, Height = 300, Content = scroller };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        SmoothScrollBehavior.SetIsEnabled(scroller, true);
        return (win, scroller, content);
    }

    [AvaloniaFact]
    public void CtrlWheelClaimedByAnOuterHandler_DoesNotScroll()
    {
        var (win, scroller, content) = MountScroller();
        try
        {
            win.AddHandler(InputElement.PointerWheelChangedEvent,
                (object? _, PointerWheelEventArgs e) => e.Handled = true, RoutingStrategies.Tunnel);

            content.RaiseEvent(Wheel(content, win, -1, KeyModifiers.Control));
            Tick(500);

            Assert.Equal(0, scroller.Offset.Y);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void UnclaimedCtrlWheel_StillScrolls()
    {
        var (win, scroller, content) = MountScroller();
        try
        {
            var wheel = Wheel(content, win, -1, KeyModifiers.Control);
            content.RaiseEvent(wheel);
            Tick(500);

            Assert.True(wheel.Handled);
            Assert.True(scroller.Offset.Y > 0, $"offset {scroller.Offset.Y} should have moved");
        }
        finally
        {
            win.Close();
        }
    }
}
