using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics page (11-23 feedback): pressing Tab, or Alt+Tab back into the app, scrolled the
/// lyrics away from the sung line. Every line is a Button; Tab focused one (or the window gave
/// focus back to the last clicked one) and the ScrollViewer brought it into view, which then
/// read as a user scroll and paused auto-follow. Focus never moves the lyrics now, and Tab
/// skips the lines. Also: Settings › Lyrics Page › "Return to the current line automatically".
/// </summary>
public class LyricsKeyboardScrollTests
{
    private static async Task<(LyricsViewModel Vm, LyricsView View, Window Win)> Mount()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var dir = Path.Combine(Path.GetTempPath(), "noctis-lyrics-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lrc = new StringBuilder();
        for (var i = 0; i < 60; i++)
            lrc.Append($"[{i / 60:00}:{i % 60:00}.00]Line number {i}\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "song.lrc"), lrc.ToString());

        var (vm, player) = LyricsPageGenreLinkTests.MakeLyrics();
        player.Duration = TimeSpan.FromSeconds(70);
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

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 800, Content = view };
        win.Show();
        deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline && Lines(view).Count < 50)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        for (var i = 0; i < 20; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        win.UpdateLayout();
        return (vm, view, win);
    }

    private static List<Button> Lines(LyricsView view)
        => view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lyric-line-btn")).ToList();

    [AvaloniaFact]
    public async Task Focusing_a_line_by_Tab_or_window_activation_never_scrolls_the_lyrics()
    {
        var (vm, view, win) = await Mount();
        try
        {
            var scroller = view.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            var lines = Lines(view);

            var before = scroller.Offset.Y;
            lines[^1].Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            Assert.Equal(before, scroller.Offset.Y, 1);

            // What the window does on Alt+Tab back: focus returns to the last focused line.
            lines[^5].Focus(NavigationMethod.Unspecified);
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            Assert.Equal(before, scroller.Offset.Y, 1);
            Assert.False(vm.IsAutoFollowPaused);
            Assert.All(lines, l => Assert.False(l.IsTabStop));
        }
        finally
        {
            win.Close();
            // The view model listens to the app-wide accent event: left subscribed, a later
            // test's accent change runs it on another dispatcher thread and throws.
            vm.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_scroll_returns_to_the_line_by_itself_only_with_the_setting_on(bool autoReturn)
    {
        var (vm, view, win) = await Mount();
        try
        {
            vm.AutoReturnToCurrentLine = autoReturn;
            var scroller = view.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            win.MouseWheel(scroller.TranslatePoint(new Point(200, 200), win)!.Value, new Vector(0, -3));
            // With the app styles loaded (another test may have added them) the wheel glides
            // over render frames (SmoothScrollBehavior), so the user scroll lands a few ticks later.
            var deadline = Environment.TickCount64 + 2000;
            while (!vm.IsAutoFollowPaused && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Assert.True(vm.IsAutoFollowPaused);
            Assert.Equal(autoReturn, LyricsView.AutoFollowResumePending(view));

            // The Follow pill resumes either way.
            vm.ResumeAutoFollowCommand.Execute(null);
            Assert.False(vm.IsAutoFollowPaused);
        }
        finally
        {
            win.Close();
            // The view model listens to the app-wide accent event: left subscribed, a later
            // test's accent change runs it on another dispatcher thread and throws.
            vm.Dispose();
        }
    }
}
