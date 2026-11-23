using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone lyrics, the line glide on the compositor (LyricsPage.StartCompositorGlide): the scroll
/// offset jumps to the target at once and the render thread plays the motion back on the
/// list's composition Translation, so the UI thread does no per-frame work. What the user sees
/// and touches must still be the glide: the lines stand where they stood when it starts, move
/// on the line curve, land on the anchor, and a tap during the glide seeks to the line under
/// the finger (Avalonia hit-tests the compositor's last frame).
/// </summary>
public class MobileLyricsCompositorGlideTests
{
    private readonly ITestOutputHelper _o;
    public MobileLyricsCompositorGlideTests(ITestOutputHelper o) => _o = o;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed record Rig(ShellViewModel Shell, FakeAudioPlayer Player, Window Window, LyricsPage Page,
        ScrollViewer Scroll, ItemsControl List);

    private static async Task<Rig> Open()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        // Sixty plain lines a second apart, from 0:10 (after the parser's intro line).
        files.Sidecars[".lrc"] = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"[{(10 + i) / 60:00}:{(10 + i) % 60:00}.00]line {i}"));
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()), nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        shell.Player.PlayTracks(new[] { new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) } }, 0);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
        // Paused: only the test moves the lines (no frame loop of the page's own).
        shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.False(shell.Player.IsPlaying);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        page.HideControlsIfPlaying();
        var rig = new Rig(shell, player, window, page, page.FindControl<ScrollViewer>("LyricsScroll")!,
            page.FindControl<ItemsControl>("LyricsList")!);
        Motion.FinishAll();
        Frames(rig, 5);
        window.UpdateLayout();
        page.ScrollToLine(10, animate: false);
        window.UpdateLayout();
        Frames(rig, 10);
        Assert.NotNull(rig.List.ContainerFromIndex(13));
        return rig;
    }

    private static void Frames(Rig rig, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Runs frames until <paramref name="ms"/> after <paramref name="clock"/> started.</summary>
    private static void RunUntil(Rig rig, Stopwatch clock, double ms)
    {
        while (clock.Elapsed.TotalMilliseconds < ms)
        {
            Frames(rig, 1);
            Thread.Sleep(4);
        }
    }

    private static Control Row(Rig rig, int index) => rig.List.ContainerFromIndex(index)!;

    /// <summary>The middle of line <paramref name="index"/> in window coordinates, as laid out
    /// (no composition Translation).</summary>
    private static Point LaidOutCentre(Rig rig, int index)
    {
        var row = Row(rig, index);
        return row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), rig.Window)!.Value;
    }

    /// <summary>The lyric line on screen under <paramref name="at"/> (the compositor's hit test).</summary>
    private static int LineAt(Rig rig, Point at)
    {
        var hit = rig.Window.InputHitTest(at) as Visual;
        var button = hit?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("lyric-line"));
        return button?.DataContext is LyricLine line ? rig.Shell.Lyrics.Lines.IndexOf(line) : -1;
    }

    [AvaloniaFact]
    public async Task TheGlide_StartsWhereTheLinesStand_MovesOnTheCurve_AndLandsOnTheAnchor()
    {
        var rig = await Open();
        try
        {
            var before12 = LaidOutCentre(rig, 12);
            Assert.Equal(12, LineAt(rig, before12));
            var startOffset = rig.Scroll.Offset.Y;

            var clock = Stopwatch.StartNew();
            rig.Page.ScrollToLine(11, animate: true);
            Assert.True(rig.Page.IsGlidingOnCompositor);
            var target = rig.Scroll.Offset.Y;          // the layout jumped to the target at once...
            var delta = target - startOffset;
            Assert.True(delta > 20, $"a line's travel ({delta:0} px)");
            Frames(rig, 1);
            Assert.Equal(target, rig.Scroll.Offset.Y);
            // ...and the screen did not: line 12 is still under the point it stood on.
            Assert.Equal(12, LineAt(rig, before12));

            // Mid-glide the lines stand on the line curve between the two places.
            RunUntil(rig, clock, 120);
            var t = clock.Elapsed.TotalMilliseconds;
            var shown = delta * (1 - LineMotion.Ease(t / LineMotion.DurationMs));
            var now11 = LaidOutCentre(rig, 11) + new Point(0, shown);
            _o.WriteLine($"travel {delta:0} px, at {t:0} ms still {shown:0.0} px to go");
            Assert.InRange(shown, delta * 0.2, delta * 0.8);
            Assert.Equal(11, LineAt(rig, now11));
            Assert.True(rig.Page.IsGlidingOnCompositor);
            Assert.False(rig.Page.IsFrameLoopRunning);   // no UI frames for the glide

            // Landed: the line on the anchor, the glide over, the offset where it was sent.
            RunUntil(rig, clock, LineMotion.DurationMs + 8 * LineMotion.CascadeDelayPerLineMs + 150);
            Assert.False(rig.Page.IsGlidingOnCompositor);
            Assert.Equal(target, rig.Scroll.Offset.Y);
            var row = Row(rig, 11);
            var top = row.TranslatePoint(default, rig.Scroll)!.Value.Y;
            var expected = rig.Scroll.Viewport.Height * LyricsPage.AnchorRatio - row.Bounds.Height / 2;
            Assert.InRange(top, expected - 2, expected + 2);
            Assert.Equal(11, LineAt(rig, LaidOutCentre(rig, 11)));
            Assert.Equal(12, LineAt(rig, LaidOutCentre(rig, 12)));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>A tap mid-glide seeks to the line under the finger (what is on screen, not where
    /// the layout already put it), and the glide stops where it stands without a jump.</summary>
    [AvaloniaFact]
    public async Task ATapDuringTheGlide_SeeksToTheLineUnderTheFinger()
    {
        var rig = await Open();
        try
        {
            var startOffset = rig.Scroll.Offset.Y;
            var clock = Stopwatch.StartNew();
            rig.Page.ScrollToLine(11, animate: true);
            var delta = rig.Scroll.Offset.Y - startOffset;
            Frames(rig, 1);
            RunUntil(rig, clock, 80);
            // The finger lands on line 13 where it is on screen now, well short of its new place.
            var shown = delta * (1 - LineMotion.Ease(clock.Elapsed.TotalMilliseconds / LineMotion.DurationMs));
            Assert.True(shown > delta * 0.2, $"{shown:0} of {delta:0} px still to go");
            var at = LaidOutCentre(rig, 13) + new Point(0, shown);
            Assert.Equal(13, LineAt(rig, at));
            rig.Window.MouseDown(at, MouseButton.Left);
            Assert.False(rig.Page.IsGlidingOnCompositor);   // the finger stopped the glide...
            Frames(rig, 1);
            Assert.Equal(13, LineAt(rig, at));              // ...where it stood
            rig.Window.MouseUp(at, MouseButton.Left);
            Frames(rig, 1);
            Assert.Equal(rig.Shell.Lyrics.Lines[13].Timestamp, rig.Player.Seeks.LastOrDefault());
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>A jump longer than the realised buffer (a seek far off) still glides, frame by
    /// frame as before, and lands.</summary>
    [AvaloniaFact]
    public async Task AFarJump_GlidesFrameByFrame_AndLands()
    {
        var rig = await Open();
        try
        {
            var clock = Stopwatch.StartNew();
            rig.Page.ScrollToLine(30, animate: true);
            Assert.False(rig.Page.IsGlidingOnCompositor);
            Assert.True(rig.Page.IsFrameLoopRunning);
            RunUntil(rig, clock, 1300);
            var row = Row(rig, 30);
            var top = row.TranslatePoint(default, rig.Scroll)!.Value.Y;
            var expected = rig.Scroll.Viewport.Height * LyricsPage.AnchorRatio - row.Bounds.Height / 2;
            Assert.InRange(top, expected - 2, expected + 2);
            Assert.Equal(30, LineAt(rig, LaidOutCentre(rig, 30)));
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// Three line-to-line glides, each followed by 170 frames at 120 Hz (the glide and its
    /// cascade, as on the S23), counted with the render: frame by frame, then on the compositor.
    /// The UI thread's part of a frame-by-frame glide (the offset, a layout pass, the scroller's
    /// events, a property change per cascade line, every frame) is gone on the compositor; what
    /// is left is the render thread redrawing the moving lines and, once a glide, its key frames.
    /// </summary>
    [AvaloniaFact]
    public async Task AGlide_OnTheCompositor_AllocatesFarLess()
    {
        long Glides(Rig rig)
        {
            var total = 0L;
            for (var line = 11; line <= 13; line++)
            {
                var clock = Stopwatch.StartNew();
                var before = GC.GetAllocatedBytesForCurrentThread();
                rig.Page.ScrollToLine(line, animate: true);
                for (var frame = 1; frame <= 170; frame++)
                {
                    Frames(rig, 1);
                    while (clock.Elapsed.TotalMilliseconds < frame * 1000.0 / 120) Thread.SpinWait(100);
                }
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            return total / 3;
        }

        long frameByFrame, compositor;
        LyricsPage.CompositorGlide = false;
        var rig = await Open();
        try
        {
            Glides(rig);   // warm-up: first glides realise rows and JIT
            rig.Page.ScrollToLine(10, animate: false);
            Frames(rig, 5);
            frameByFrame = Glides(rig);
        }
        finally
        {
            LyricsPage.CompositorGlide = true;
            rig.Window.Close();
        }
        rig = await Open();
        try
        {
            Glides(rig);
            rig.Page.ScrollToLine(10, animate: false);
            Frames(rig, 5);
            compositor = Glides(rig);
        }
        finally
        {
            rig.Window.Close();
        }
        // 11-23: 1254 KB frame by frame, 192 KB on the compositor (drawing stubbed); with real
        // Skia 2946 KB and 1440 KB.
        _o.WriteLine($"a glide (170 frames with the render): frame by frame {frameByFrame / 1024} KB, on the compositor {compositor / 1024} KB");
        // With real Skia (NOCTIS_TEST_SKIA=1) both carry the render thread's drawing of each
        // frame, which is the same either way: printed only.
        if (!HeadlessTestApp.RealRendering) Assert.InRange(compositor, 0, frameByFrame / 3);
    }

    /// <summary>The cascade lines' lag is played back as straight runs between key frames: every
    /// point of the real curve (the desktop's cascade, 8 lines 50 ms apart) stays within a
    /// third of a pixel of them, forward and backward, short and long, with few key frames.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(-150)]
    public void TheCascadeKeyFrames_FollowTheLagCurve(double delta)
    {
        const int lines = 8;
        const double stepMs = 2;
        var duration = Math.Min(1050, Math.Max(LineMotion.DurationMs, Math.Abs(delta) * 0.85));
        var total = duration + lines * LineMotion.CascadeDelayPerLineMs;
        double[] LagsAt(double ms)
        {
            var eased = LineMotion.Ease(Math.Clamp(ms / duration, 0, 1));
            var lags = new double[lines];
            var prev = 0.0;
            for (var i = 0; i < lines; i++)
            {
                var t = Math.Clamp((ms - (i + 1) * LineMotion.CascadeDelayPerLineMs) / duration, 0, 1);
                prev = lags[i] = LineMotion.CascadeStep(delta * (eased - LineMotion.Ease(t)), prev, 16);
            }
            return lags;
        }
        var steps = (int)Math.Ceiling(total / stepMs);
        var curve = new double[(steps + 1) * lines];
        for (var k = 0; k <= steps; k++) Array.Copy(LagsAt(Math.Min(k * stepMs, total)), 0, curve, k * lines, lines);

        var keys = new List<int>();
        var count = 0;
        var worst = 0.0;
        for (var line = 0; line < lines; line++)
        {
            LyricsPage.LagKeyFrames(curve, steps, lines, line, LyricsPage.LagTolerancePx, keys);
            Assert.Equal(0, keys[0]);
            Assert.Equal(steps, keys[^1]);
            count += keys.Count;
            // Against the real curve every 0.25 ms, not just the samples.
            for (var j = 0; j + 1 < keys.Count; j++)
            {
                double a = keys[j] * stepMs, b = Math.Min(keys[j + 1] * stepMs, total);
                double va = curve[keys[j] * lines + line], vb = curve[keys[j + 1] * lines + line];
                for (var ms = a; ms <= b; ms += 0.25)
                    worst = Math.Max(worst, Math.Abs(va + (vb - va) * (ms - a) / Math.Max(1e-9, b - a) - LagsAt(ms)[line]));
            }
        }
        _o.WriteLine($"travel {delta} px: {count} key frames for {lines} lines, worst {worst:0.000} px off");
        Assert.InRange(worst, 0, 0.35);
        Assert.InRange(count, lines * 2, 400);
    }
}
