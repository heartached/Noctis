using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone lyrics, managed allocation per frame while synced lyrics flow. On a Galaxy S23 the
/// page allocated ~4 MB a second while a song played, a GC0 every second and each a 60-80 ms
/// stall. A frame of the flowing lyrics (sweep, glide, line restyle, render) must stay within
/// a small budget.
/// </summary>
public class MobileLyricsAllocationTests
{
    private readonly ITestOutputHelper _o;
    public MobileLyricsAllocationTests(ITestOutputHelper o) => _o = o;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>Thirty plain LRC lines <paramref name="gap"/> s apart.</summary>
    internal static string Lrc(double gap = 1.5)
    {
        var sb = new StringBuilder();
        for (var l = 0; l < 30; l++)
        {
            var t = TimeSpan.FromSeconds(1.0 + l * gap);
            sb.Append($"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]Line number {l} of the song goes here\n");
        }
        return sb.ToString();
    }

    /// <summary>Word-timed TTML: thirty lines <paramref name="gap"/> s apart, each of
    /// <paramref name="count"/> words sung 0.25 s apart (a pop vocal's pace), the last held.</summary>
    internal static string Ttml(double gap = 1.5, int count = 5)
    {
        var words = new[] { "I'm", "tellin'", "you", "all", "it", "all", "falls", "down" };
        var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xml:lang=\"en\"><body><div>");
        for (var l = 0; l < 30; l++)
        {
            var start = 1.0 + l * gap;
            var end = start + gap - 0.05;
            sb.Append($"<p begin=\"{start:F3}\" end=\"{end:F3}\" ttm:agent=\"v1\">");
            for (var w = 0; w < count; w++)
            {
                var ws = start + w * 0.25;
                var we = w == count - 1 ? end : ws + 0.25;
                sb.Append($"<span begin=\"{ws:F3}\" end=\"{we:F3}\">{words[w % words.Length]}{(w < count - 1 ? " " : "")}</span>");
            }
            sb.Append("</p>");
        }
        return sb.Append("</div></body></tt>").ToString();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static (ShellViewModel Shell, FakeAudioPlayer Player, ShellView View, Window Window) Mount(string ext, string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[ext] = text;
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var win = new Window { Width = 360, Height = 780, Content = view };
        win.Show();
        win.UpdateLayout();
        return (shell, player, view, win);
    }

    internal readonly record struct FrameCost(long BytesPerFrame, long MedianBytes, int Frames, int RowsPrepared, long RowFramesBytes,
        double MedianMs, double P95Ms, long BytesPerSecond = 0)
    {
        public override string ToString() =>
            $"frames={Frames} bytes/frame mean={BytesPerFrame} median={MedianBytes} rows prepared={RowsPrepared} ({RowFramesBytes / 1024} KB in those frames)"
            + $" frame ms median={MedianMs:F2} p95={P95Ms:F2} {BytesPerSecond / 1024} KB a second";
    }

    /// <summary>
    /// Plays <paramref name="seconds"/> of the song at 60 fps with the phone's 250 ms poll and
    /// the transport faded, as on the phone (the page runs its own frame loop off the wall
    /// clock), and counts the UI thread's allocations per frame. The first second (the page
    /// opening, the first glide) is not counted.
    /// </summary>
    internal static async Task<FrameCost> Measure(string ext, string text, double seconds, double fps = 60, Action<LyricsPage>? setup = null)
    {
        var (shell, player, view, win) = Mount(ext, text);
        try
        {
            var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };
            shell.Player.PlayTracks(new[] { track }, 0);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(shell.Lyrics.IsSynced);
            shell.OpenNowPlayingCommand.Execute(null);
            shell.ToggleLyricsCommand.Execute(null);
            win.UpdateLayout();
            player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000));
            Pump();
            Assert.True(shell.Player.IsPlaying);
            var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            page.HideControlsIfPlaying();
            setup?.Invoke(page);
            var prepared = 0;
            page.FindControl<ItemsControl>("LyricsList")!.ContainerPrepared += (_, _) => prepared++;

            long bytes = 0, rowBytes = 0;
            var rows = 0;
            var perFrame = new List<long>();
            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            double nextPoll = 0, nextFrame = 0;
            while (clock.Elapsed.TotalMilliseconds < seconds * 1000)
            {
                var t = clock.Elapsed.TotalMilliseconds;
                if (t < nextFrame)
                {
                    // Sleep(1) on Windows can take 15 ms: spin the last stretch to hold the rate.
                    if (nextFrame - t > 3) Thread.Sleep(1); else Thread.SpinWait(100);
                    continue;
                }
                nextFrame = Math.Max(nextFrame + 1000.0 / fps, t);
                if (t >= nextPoll)
                {
                    nextPoll = t + 250;
                    player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000 + t));
                }
                prepared = 0;
                var b0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                Pump();
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var frame = GC.GetAllocatedBytesForCurrentThread() - b0;
                if (t < 1000) continue;
                times.Add(ms);
                bytes += frame;
                perFrame.Add(frame);
                if (prepared > 0)
                {
                    rows += prepared;
                    rowBytes += frame;
                }
            }
            perFrame.Sort();
            times.Sort();
            return new FrameCost(bytes / Math.Max(1, perFrame.Count), perFrame.Count > 0 ? perFrame[perFrame.Count / 2] : 0,
                perFrame.Count, rows, rowBytes,
                times.Count > 0 ? times[times.Count / 2] : 0, times.Count > 0 ? times[times.Count * 95 / 100] : 0,
                (long)(bytes / Math.Max(0.001, seconds - 1)));
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>
    /// A word-timed line sweeping (no line change in the window): only the moving words' brush
    /// stops, their float and the bindings that drive them. Measured 29 KB a frame before the
    /// sweep stopped redrawing every word every frame, 11 KB after that and the held-note glow
    /// being built under held notes only (11-14), 9.1 KB before and 5.7 KB after the cells
    /// followed their words instead of binding them and floated on the compositor (11-23).
    /// </summary>
    [AvaloniaFact]
    public async Task WordSweep_AllocatesLittlePerFrame()
    {
        var cost = await Measure(".ttml", Ttml(gap: 20, count: 40), 5);
        _o.WriteLine(cost.ToString());
        Assert.InRange(cost.BytesPerFrame, 0, WordSweepBudget);
    }

    internal const long WordSweepBudget = 8 * 1024;

    /// <summary>Plain synced lines between line changes: nothing moves, nothing allocates.</summary>
    [AvaloniaFact]
    public async Task PlainLines_AllocateAlmostNothingPerFrame()
    {
        var cost = await Measure(".lrc", Lrc(gap: 20), 4);
        _o.WriteLine(cost.ToString());
        Assert.InRange(cost.BytesPerFrame, 0, 6 * 1024);
    }

    /// <summary>The held-note glow (a second, blurred word) is built under held notes only: on
    /// every other word it was hidden yet took each frame's progress and float.</summary>
    [AvaloniaFact]
    public async Task HeldNoteGlow_IsBuiltUnderHeldNotesOnly()
    {
        var (shell, _, view, win) = Mount(".ttml", Ttml(gap: 3, count: 5));
        try
        {
            var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };
            shell.Player.PlayTracks(new[] { track }, 0);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
            shell.OpenNowPlayingCommand.Execute(null);
            shell.ToggleLyricsCommand.Execute(null);
            win.UpdateLayout();
            Pump();
            var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            var words = page.GetVisualDescendants().OfType<Noctis.Controls.KaraokeWordText>().ToList();
            var sweeps = words.Where(w => w.Classes.Contains("word-sweep")).ToList();
            var glows = words.Where(w => w.Classes.Contains("word-glow")).ToList();
            var held = sweeps.Count(w => w.DataContext is WordTiming { IsEmphasis: true });
            Assert.True(held > 0, "the test lines end on a held note");
            Assert.True(sweeps.Count > held);
            Assert.Equal(held, glows.Count);
            Assert.All(glows, g => Assert.True(g.DataContext is WordTiming { IsEmphasis: true } && g.IsEffectivelyVisible));
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>
    /// A line change scrolls a line in. Its row used to be built from scratch (button, texts,
    /// word layers, styles, bindings: ~330 KB for a plain line and ~1 MB for a word-timed one
    /// in an optimized build); rows are now kept and handed the next line (RowItemsControl).
    /// A word-timed row keeps its word cells too (WordItemsControl). A row handed a new line
    /// shows that line's dimming and blur at once, not faded from the line it showed before.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(".lrc", 200)]
    [InlineData(".ttml", 300)]
    public async Task RowsScrollingIn_AreReused_AndShowTheirLinesLookAtOnce(string ext, int budgetKb)
    {
        var (shell, player, view, win) = Mount(ext, ext == ".lrc" ? Lrc(gap: 1.5) : Ttml(gap: 1.5, count: 5));
        try
        {
            var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };
            shell.Player.PlayTracks(new[] { track }, 0);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
            player.RaisePositionChanged(TimeSpan.FromSeconds(8));
            shell.OpenNowPlayingCommand.Execute(null);
            shell.ToggleLyricsCommand.Execute(null);
            win.UpdateLayout();
            var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            var list = page.FindControl<ItemsControl>("LyricsList")!;
            var scroll = page.FindControl<ScrollViewer>("LyricsScroll")!;
            var settle = Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 1500) { Pump(); Thread.Sleep(16); }

            var prepared = new List<Control>();
            list.ContainerPrepared += (_, e) => prepared.Add(e.Container);
            var built = 0L;
            var rows = 0;
            for (var step = 0; step < 6; step++)
            {
                prepared.Clear();
                var b0 = GC.GetAllocatedBytesForCurrentThread();
                scroll.Offset = new Avalonia.Vector(0, scroll.Offset.Y + 120);
                win.UpdateLayout();
                built += GC.GetAllocatedBytesForCurrentThread() - b0;
                rows += prepared.Count;
                foreach (var row in prepared)
                {
                    var button = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lyric-line"));
                    var line = Assert.IsType<LyricLine>(button.DataContext);
                    Assert.Equal(line.LineOpacity, button.Opacity, 3);
                    Assert.Equal(line.BlurRadius, (button.Effect as Avalonia.Media.IBlurEffect)?.Radius ?? 0, 1);
                }
                Pump();
            }
            _o.WriteLine($"rows scrolled in: {rows}, {built / Math.Max(1, rows) / 1024} KB a row");
            Assert.True(rows >= 3);
            Assert.True(shell.Lyrics.Lines.Select(l => l.LineOpacity).Distinct().Count() > 2, "the lines are dimmed by distance");
            // Debug build, built from scratch: ~420 KB a plain row, ~1.2 MB a word-timed one.
            Assert.InRange(built / rows, 0, budgetKb * 1024);
        }
        finally
        {
            win.Close();
        }
    }
}
