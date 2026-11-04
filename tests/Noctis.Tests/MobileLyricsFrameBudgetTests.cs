using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone word-by-word lyrics, per-frame cost: while a line sweeps, a frame must not run a
/// layout pass, invalidate a measure, or allocate more than a small bounded amount. Beta
/// testers reported mini-stutters on a mid-range phone (Galaxy A55); these count the work a
/// frame does (layout passes, invalidated measures, bytes, property notifications).
/// </summary>
public class MobileLyricsFrameBudgetTests
{
    private readonly ITestOutputHelper _o;
    public MobileLyricsFrameBudgetTests(ITestOutputHelper o) => _o = o;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>Four lines of eight timed words, one held word, every line a few seconds
    /// long; line 2 is a second voice that starts while line 1 is still being sung.</summary>
    internal static string Ttml()
    {
        var words = new[] { "I'm", "tellin'", "you", "all", "it", "all", "falls", "down" };
        var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xml:lang=\"en\"><body><div>");
        for (var l = 0; l < 6; l++)
        {
            var start = 1.0 + l * 3.0;
            var voice = l % 2 == 1 ? "v2" : "v1";
            sb.Append($"<p begin=\"{start:F3}\" end=\"{start + 3.4:F3}\" ttm:agent=\"{voice}\">");
            for (var w = 0; w < words.Length; w++)
            {
                var ws = start + w * 0.3;
                var we = w == 7 ? start + 3.4 : ws + 0.3;   // the last word is held 1.0 s
                sb.Append($"<span begin=\"{ws:F3}\" end=\"{we:F3}\">{words[w]}{(w < 7 ? " " : "")}</span>");
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

    private static (ShellViewModel Shell, FakeAudioPlayer Player, ShellView View, Window Window) Mount(string ttml)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = ttml;
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var win = new Window { Width = 412, Height = 915, Content = view };
        win.Show();
        win.UpdateLayout();
        return (shell, player, view, win);
    }

    private static async Task OpenLyrics(ShellViewModel shell, FakeAudioPlayer player, Window win)
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };
        shell.Player.PlayTracks(new[] { track }, 0);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(shell.Lyrics.IsSynced);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        win.UpdateLayout();
        // The first poll tick is what flips the player to Playing (and shows the transport).
        player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000));
        Pump();
        Assert.True(shell.Player.IsPlaying);
    }

    /// <summary>
    /// Plays 3 s of the first line at 60 fps with the song's 250 ms position poll, the way the
    /// phone does (the page runs its own frame loop off the wall clock), with the transport
    /// already faded. Returns the layout passes that ran after the first 1.5 s (the scroll
    /// glide onto the line is over by then) and prints the cost of the frames.
    /// </summary>
    private async Task<int> SweepAfterTheGlide(bool hideTransport)
    {
        var (shell, player, view, win) = Mount(Ttml());
        try
        {
            await OpenLyrics(shell, player, win);
            var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            if (hideTransport)
            {
                page.HideControlsIfPlaying();
                Assert.False(page.ControlsShown);
            }

            var layouts = 0;
            var frames = new List<double>();
            var bytes = 0L;
            var clock = Stopwatch.StartNew();
            double nextPoll = 0, nextFrame = 0;
            var counted = 0;
            while (clock.Elapsed.TotalMilliseconds < 3000)
            {
                var t = clock.Elapsed.TotalMilliseconds;
                if (t < nextFrame) { Thread.Sleep(1); continue; }
                nextFrame = t + 1000.0 / 60;
                if (t >= nextPoll)
                {
                    nextPoll = t + 250;
                    player.RaisePositionChanged(TimeSpan.FromMilliseconds(1000 + t));
                }
                var before = layouts;
                void OnLayout(object? s, EventArgs e) => layouts++;
                win.LayoutUpdated += OnLayout;
                var b0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                Pump();
                var ms = sw.Elapsed.TotalMilliseconds;
                win.LayoutUpdated -= OnLayout;
                if (t < 1500) { layouts = before; continue; }
                frames.Add(ms);
                bytes += GC.GetAllocatedBytesForCurrentThread() - b0;
                counted++;
            }
            frames.Sort();
            _o.WriteLine($"transportHidden={hideTransport} frames={counted} layoutPassesDuringSweep={layouts} " +
                         $"frame ms p50={frames[frames.Count / 2]:F2} p95={frames[frames.Count * 95 / 100]:F2} max={frames[^1]:F2} " +
                         $"bytes/frame={bytes / counted}");
            return layouts;
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>
    /// A faded transport still measured: its elapsed and remaining labels changed once a second,
    /// each a layout pass (and a repaint) in the middle of the word sweep, a periodic hitch on a
    /// mid-range phone. Faded, it now leaves layout, so a sweep frame runs none.
    /// </summary>
    [AvaloniaFact]
    public async Task FadedTransport_DoesNotRelayoutTheLyricsEverySecond()
    {
        var layouts = await SweepAfterTheGlide(hideTransport: true);
        Assert.Equal(0, layouts);
    }

    [AvaloniaFact]
    public async Task FadedTransport_LeavesLayout_AndComesBackOnATouch()
    {
        var (shell, player, view, win) = Mount(Ttml());
        try
        {
            await OpenLyrics(shell, player, win);
            var page = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            var layer = page.FindControl<Control>("ControlsLayer")!;
            page.ShowControls();
            page.HideControlsIfPlaying();
            Assert.False(page.ControlsShown);
            Assert.True(layer.IsVisible);   // still there while it fades
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (layer.IsVisible && DateTime.UtcNow < deadline) { await Task.Delay(20); Pump(); }
            Assert.False(layer.IsVisible);

            page.ShowControls();
            Assert.True(layer.IsVisible);
            Assert.True(page.ControlsShown);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>The lyrics page is opaque: once it has faded in, Now Playing underneath is not
    /// drawn or laid out (its time labels changed once a second under the sweep); closing the
    /// lyrics brings it back before the fade starts.</summary>
    [AvaloniaFact]
    public async Task NowPlayingUnderTheLyrics_LeavesTheTree_AndReturnsOnClose()
    {
        var (shell, player, view, win) = Mount(Ttml());
        try
        {
            await OpenLyrics(shell, player, win);
            var nowPlaying = view.GetVisualDescendants().OfType<NowPlayingPage>().Single();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (nowPlaying.IsVisible && DateTime.UtcNow < deadline) { await Task.Delay(20); Pump(); }
            Assert.False(nowPlaying.IsVisible);

            shell.ToggleLyricsCommand.Execute(null);
            Assert.True(nowPlaying.IsVisible);
            deadline = DateTime.UtcNow.AddSeconds(3);
            var lyrics = view.GetVisualDescendants().OfType<LyricsPage>().Single();
            while (lyrics.IsVisible && DateTime.UtcNow < deadline) { await Task.Delay(20); Pump(); }
            Assert.False(lyrics.IsVisible);
            Assert.True(nowPlaying.IsVisible);
        }
        finally
        {
            win.Close();
        }
    }
}
