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
    /// the layout already put it). The finger landing does not stop the glide (beta 5 regression,
    /// 11-23: a touch froze the lines where they stood, for as long as it rested); the lift does,
    /// on the line as shown, so the line Button sees the tap where the finger is and the seek
    /// glides on from there.</summary>
    [AvaloniaFact]
    public async Task ATapDuringTheGlide_SeeksToTheLineUnderTheFinger_AndTheGlideRunsOn()
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
            var shown = ShownTranslationY(rig);
            Assert.True(shown > delta * 0.2, $"{shown:0} of {delta:0} px still to go");
            var at = LaidOutCentre(rig, 13) + new Point(0, shown);
            Assert.Equal(13, LineAt(rig, at));
            var finger = rig.Window.TouchBegin(at);
            Frames(rig, 1);
            Assert.True(rig.Page.IsGlidingOnCompositor);    // the finger alone stops nothing
            var before = ShownTranslationY(rig);
            Frames(rig, 2);
            Thread.Sleep(10);
            Frames(rig, 1);
            Assert.True(ShownTranslationY(rig) < before);   // still moving
            var underFinger = LaidOutCentre(rig, 13).Y + ShownTranslationY(rig);
            rig.Window.TouchEnd(finger, at);
            Frames(rig, 1);
            Assert.Equal(rig.Shell.Lyrics.Lines[13].Timestamp, rig.Player.Seeks.LastOrDefault());
            Assert.False(rig.Shell.Lyrics.IsBrowsing);
            // The lift stopped the glide on the line as shown (the seek's own glide goes on from
            // there): no jump under the finger. The lift falls between two frames, so the lines
            // stop where the glide has them at that moment: at most a frame's motion on from the
            // last frame shown (the hand-over itself is exact, AnInterruptedGlide_...).
            Assert.InRange(LaidOutCentre(rig, 13).Y + ShownTranslationY(rig) - underFinger, -3, 3);
            Assert.DoesNotContain(":pressed", LineButton(rig, 13).Classes);   // the Button let go
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>A drag mid-glide takes the lines over: the glide stops where they stand once the
    /// finger has moved far enough to scroll, and the list follows the finger from there (the
    /// line under it stays under it), browsing.</summary>
    [AvaloniaFact]
    public async Task ADragDuringTheGlide_StopsItWhereTheLinesStand_AndTheListFollowsTheFinger()
    {
        var rig = await Open();
        try
        {
            var startOffset = rig.Scroll.Offset.Y;
            var clock = Stopwatch.StartNew();
            rig.Page.ScrollToLine(11, animate: true);
            var delta = rig.Scroll.Offset.Y - startOffset;
            Frames(rig, 1);
            RunUntil(rig, clock, 100);
            var at = LaidOutCentre(rig, 13) + new Point(0, ShownTranslationY(rig));
            Assert.Equal(13, LineAt(rig, at));
            var finger = rig.Window.TouchBegin(at);
            Frames(rig, 1);
            Assert.True(rig.Page.IsGlidingOnCompositor);
            // Small moves (a shaky tap) do not take over.
            rig.Window.TouchMove(finger, at + new Point(0, 2));
            Frames(rig, 1);
            Assert.True(rig.Page.IsGlidingOnCompositor);
            // A real drag does: 60 px down in steps. The glide runs on until the finger has moved
            // far enough for the scroller to start (it then moves the list by the finger's travel
            // past that start distance: 55 px here), and stops on the lines as shown.
            var startLine = LaidOutCentre(rig, 13).Y + ShownTranslationY(rig);
            for (var y = 10; y <= 60; y += 10)
            {
                rig.Window.TouchMove(finger, at + new Point(0, y));
                Frames(rig, 1);
            }
            Assert.False(rig.Page.IsGlidingOnCompositor);
            Assert.True(rig.Shell.Lyrics.IsBrowsing);
            Assert.Equal(0, ShownTranslationY(rig), 3);
            var moved = LaidOutCentre(rig, 13).Y + ShownTranslationY(rig) - startLine;
            rig.Window.TouchEnd(finger, at + new Point(0, 60));
            Frames(rig, 1);
            _o.WriteLine($"finger moved 60 px: line 13 moved {moved:0.0} px (55 by the scroller, less what the glide still moved it up before the take-over)");
            // No jump at the take-over: the glide's last bit up (a few px at most) and the drag.
            Assert.InRange(moved, 55 - 5, 55.5);
            _o.WriteLine($"glide of {delta:0} px taken over by a drag");
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>
    /// S23 (11-23, round 4 build): after Lyrics was opened and closed again with its Lyrics
    /// button, Now Playing ran at 1.2-1.4 MB/s and 56% render thread (6% before Lyrics was ever
    /// opened), the stall monitor showing the lyrics' glide, frame loop and keep-alive still
    /// running under it. Closed, the lyrics page does nothing: through line changes no glide
    /// starts, no line's look animates, the frame loop and keep-alive are off; opened again, it
    /// stands on the sung line at once.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosedAgain_TheLyricsDoNothingThroughLineChanges_AndOpenOnTheSungLine()
    {
        var rig = await Open();
        try
        {
            var keepAlive = rig.Page.FindControl<RenderKeepAlive>("KeepAlive")!;
            rig.Shell.Player.TogglePlayPauseCommand.Execute(null);   // playing
            Assert.True(rig.Shell.Player.IsPlaying);
            rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(21.5));
            var clock = Stopwatch.StartNew();
            RunUntil(rig, clock, 300);
            // A glide in flight, then back to Now Playing with the lyrics page's own Lyrics
            // button, as on the phone: the glide ends with the page.
            rig.Page.ScrollToLine(rig.Shell.Lyrics.ScrollLineIndex + 1, animate: true);
            Assert.True(rig.Page.IsGlidingOnCompositor);
            rig.Page.FindControl<Button>("LyricsButton")!.Command!.Execute(null);
            Assert.False(rig.Shell.IsLyricsOpen);
            RunUntil(rig, clock, 1300);                              // the fade out has run
            Motion.FinishAll();
            Frames(rig, 2);
            Assert.False(rig.Page.IsVisible);
            Assert.False(rig.Page.IsGlidingOnCompositor);
            Assert.Equal(0, ShownTranslationY(rig), 3);

            var pulses = keepAlive.Pulses;
            var glides = 0;
            rig.Shell.Lyrics.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LyricsPageViewModel.ScrollLineIndex) && rig.Page.IsGlidingOnCompositor) glides++;
            };
            var looks = 0;
            foreach (var c in rig.List.GetRealizedContainers())
                c.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lyric-line")).PropertyChanged += (_, e) =>
                {
                    if (e.Property == Visual.OpacityProperty || e.Property == Visual.EffectProperty) looks++;
                };
            // Three line changes while closed (a line a second from 0:10).
            for (var s = 22; s <= 24; s++)
            {
                rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(s + 0.5));
                clock.Restart();
                RunUntil(rig, clock, 400);
                Assert.False(rig.Page.IsGlidingOnCompositor);
                Assert.False(rig.Page.IsFrameLoopRunning);
                Assert.False(keepAlive.IsRunning);
            }
            Assert.Equal(0, glides);
            Assert.Equal(pulses, keepAlive.Pulses);
            _o.WriteLine($"closed: {looks} line look changes, {keepAlive.Pulses - pulses} keep-alive pulses");

            // Opened again: on the sung line at once, no glide to it.
            rig.Shell.ToggleLyricsCommand.Execute(null);
            Frames(rig, 3);
            rig.Window.UpdateLayout();
            Frames(rig, 2);
            var sung = rig.Shell.Lyrics.ScrollLineIndex;
            Assert.True(sung >= 13, $"sung line {sung}");
            var row = Row(rig, sung);
            var top = row.TranslatePoint(default, rig.Scroll)!.Value.Y;
            var expected = rig.Scroll.Viewport.Height * LyricsPage.AnchorRatio - row.Bounds.Height / 2;
            Assert.InRange(top, expected - 2, expected + 2);
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>A line's button (the visual its opacity and blur are on).</summary>
    private static Button LineButton(Rig rig, int index) =>
        Row(rig, index).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lyric-line"));

    /// <summary>The render thread's opacity of a line (what is on screen).</summary>
    private static double ShownOpacity(Rig rig, int index)
    {
        var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(LineButton(rig, index))!;
        var server = typeof(Avalonia.Rendering.Composition.CompositionVisual)
            .GetProperty("Server", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)!
            .GetValue(visual)!;
        var serverVisual = typeof(Avalonia.Rendering.Composition.CompositionVisual).Assembly
            .GetType("Avalonia.Rendering.Composition.Server.ServerCompositionVisual")!;
        return (float)serverVisual.GetProperty("Opacity")!.GetValue(server)!;
    }

    /// <summary>
    /// A line's dimming fades on the compositor (LineFade): the line's Opacity changes once, at
    /// once, and the render thread eases what is shown over the old transition's 0.5 s on its
    /// curve. The blur steps through three radii over the old 0.4 s (one effect change each,
    /// then the target) instead of a new effect on every frame. A row shown afresh takes both at once.
    /// </summary>
    [AvaloniaFact]
    public async Task TheLineFade_RunsOnTheCompositor_AndTheBlurSteps()
    {
        var rig = await Open();
        try
        {
            var line = rig.Shell.Lyrics.Lines[14];
            var button = LineButton(rig, 14);
            line.LineOpacity = 0.9;
            line.BlurRadius = 0;
            var settle = Stopwatch.StartNew();
            RunUntil(rig, settle, 700);
            Assert.Equal(0.9, ShownOpacity(rig, 14), 3);
            Assert.Null(button.Effect);

            var opacityChanges = 0;
            var effects = new List<double>();
            button.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty) opacityChanges++;
                else if (e.Property == Visual.EffectProperty) effects.Add((e.NewValue as Avalonia.Media.IBlurEffect)?.Radius ?? 0);
            };
            var clock = Stopwatch.StartNew();
            line.LineOpacity = 0.3;
            line.BlurRadius = 6;
            Assert.Equal(0.3, button.Opacity, 3);                 // the UI side at once
            Frames(rig, 1);
            RunUntil(rig, clock, 250);
            var t = clock.Elapsed.TotalMilliseconds;
            var shown = ShownOpacity(rig, 14);
            var ease = new Avalonia.Animation.Easings.CubicEaseInOut();
            _o.WriteLine($"at {t:0} ms: shown opacity {shown:0.000}");
            // On the curve, a frame of slack for the render thread's start.
            Assert.InRange(shown, 0.9 - 0.6 * ease.Ease(Math.Min(1, t / 500)) - 0.01, 0.9 - 0.6 * ease.Ease(Math.Max(0, (t - 40) / 500)) + 0.01);
            RunUntil(rig, clock, 650);
            Assert.Equal(0.3, ShownOpacity(rig, 14), 3);
            Assert.Equal(1, opacityChanges);

            _o.WriteLine($"blur radii shown: {string.Join(", ", effects.Select(r => r.ToString("0.0")))}");
            var blurEase = new Avalonia.Animation.Easings.CubicEaseOut();
            // At once, then every 133 ms: the eased radius at the middle of each third, then 6.
            var expected = new[] { 0.5 / 3, 1.5 / 3, 2.5 / 3 }.Select(t => Math.Round(6 * blurEase.Ease(t), 1)).Append(6.0).Distinct();
            Assert.Equal(expected, effects.Select(r => Math.Round(r, 1)));

            // Far away and back: a row handed another line shows that line's look at once.
            rig.Page.ScrollToLine(45, animate: false);
            Frames(rig, 3);
            rig.Page.ScrollToLine(10, animate: false);
            Frames(rig, 2);
            foreach (var i in new[] { 11, 12, 13 })
            {
                var l = rig.Shell.Lyrics.Lines[i];
                Assert.Equal(l.LineOpacity, ShownOpacity(rig, i), 3);
                Assert.Equal(l.BlurRadius, (LineButton(rig, i).Effect as Avalonia.Media.IBlurEffect)?.Radius ?? 0, 1);
            }
        }
        finally
        {
            rig.Window.Close();
        }
    }

    /// <summary>The render thread's Translation of the lyric list (what is on screen).</summary>
    private static double ShownTranslationY(Rig rig)
    {
        var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(rig.List)!;
        var server = typeof(Avalonia.Rendering.Composition.CompositionVisual)
            .GetProperty("Server", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)!
            .GetValue(visual)!;
        var serverVisual = typeof(Avalonia.Rendering.Composition.CompositionVisual).Assembly
            .GetType("Avalonia.Rendering.Composition.Server.ServerCompositionVisual")!;
        return ((Vector3D)serverVisual.GetProperty("Translation")!.GetValue(server)!).Y;
    }

    /// <summary>
    /// A glide stopped mid-way (here by browsing) hands over to the scroll offset without a jump,
    /// even when its first frame came late: the glide's clock starts when the compositor's does
    /// (at the commit that carries it), not when it was asked for. Measured on screen: the active
    /// line's place in the frame before the stop and the frame after. Before (11-23) the page's
    /// clock ran up to a frame ahead of the render thread's, a jump of up to ~3 px on the phone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(12)]
    public async Task AnInterruptedGlide_HandsOverWithoutAJump(int lateMs)
    {
        var rig = await Open();
        try
        {
            var clock = Stopwatch.StartNew();
            rig.Page.ScrollToLine(11, animate: true);
            Assert.True(rig.Page.IsGlidingOnCompositor);
            Thread.Sleep(lateMs);                            // the first frame comes late
            Frames(rig, 1);
            RunUntil(rig, clock, 150 + lateMs);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var tick = LyricsPageViewModel.NowMs();
            // Where the content is shown, as a scroll offset (the layout rounds the list to whole
            // pixels wherever it stops; that is not the hand-over's).
            var target = rig.Scroll.Offset.Y;
            var before = target - ShownTranslationY(rig);
            rig.Page.BeginBrowse();
            var gap = LyricsPageViewModel.NowMs() - tick;
            var after = rig.Scroll.Offset.Y;
            Frames(rig, 2);
            Assert.False(rig.Page.IsGlidingOnCompositor);
            Assert.Equal(0, ShownTranslationY(rig), 6);
            // The stop falls a moment after the last frame shown: the lines may have moved on by
            // the glide's speed over that moment (at most ~0.4 px/ms on this travel).
            var slack = 0.5 + 0.4 * gap;
            _o.WriteLine($"first frame {lateMs} ms late: shown at {before:0.00} before the stop, {after:0.00} after ({after - before:+0.00;-0.00} px, stop {gap:0.0} ms after the frame)");
            Assert.InRange(after - before, -slack, slack);
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

    /// <summary>The render thread's scale of a line's button (what is on screen).</summary>
    private static double ShownScale(Rig rig, int index)
    {
        var button = Row(rig, index).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lyric-line"));
        var visual = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(button)!;
        var server = typeof(Avalonia.Rendering.Composition.CompositionVisual)
            .GetProperty("Server", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)!
            .GetValue(visual)!;
        var serverVisual = typeof(Avalonia.Rendering.Composition.CompositionVisual).Assembly
            .GetType("Avalonia.Rendering.Composition.Server.ServerCompositionVisual")!;
        return ((Vector3D)serverVisual.GetProperty("Scale")!.GetValue(server)!).X;
    }

    /// <summary>The sung line grows to full size and the line it leaves shrinks back to 0.96 on
    /// the line curve over the glide's second, on the compositor (LineScale); a row handed
    /// another line shows that line's size at once.</summary>
    [AvaloniaFact]
    public async Task TheSungLine_GrowsOnTheCurve_OnTheCompositor()
    {
        var rig = await Open();
        try
        {
            var lines = rig.Shell.Lyrics.Lines;
            lines[11].IsActive = true;
            Frames(rig, 3);
            var clock = Stopwatch.StartNew();
            RunUntil(rig, clock, 1100);
            Assert.Equal(1, ShownScale(rig, 11), 3);
            Assert.Equal(0.96, ShownScale(rig, 12), 3);

            clock.Restart();
            lines[11].IsActive = false;
            lines[12].IsActive = true;
            Frames(rig, 1);
            RunUntil(rig, clock, 100);
            var t = clock.Elapsed.TotalMilliseconds;
            var grow = ShownScale(rig, 12);
            var shrink = ShownScale(rig, 11);
            _o.WriteLine($"at {t:0} ms: growing {grow:0.0000}, shrinking {shrink:0.0000}");
            Assert.InRange(grow, 0.9605, 0.9995);
            Assert.InRange(shrink, 0.9605, 0.9995);
            // On the curve (the render thread's clock starts with its next frame: a frame of slack).
            Assert.InRange(grow, 0.96 + 0.04 * LineMotion.Ease((t - 40) / LineMotion.DurationMs) - 0.002,
                0.96 + 0.04 * LineMotion.Ease(t / LineMotion.DurationMs) + 0.002);
            RunUntil(rig, clock, 1100);
            Assert.Equal(1, ShownScale(rig, 12), 3);
            Assert.Equal(0.96, ShownScale(rig, 11), 3);

            // Far away and back: the rows realised for other lines show their own size at once.
            rig.Page.ScrollToLine(45, animate: false);
            Frames(rig, 3);
            rig.Page.ScrollToLine(10, animate: false);
            Frames(rig, 2);
            Assert.Equal(1, ShownScale(rig, 12), 3);
            Assert.Equal(0.96, ShownScale(rig, 13), 3);
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
