using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Back in front after a while in the background (home, screen off) with the lyrics open: the
/// frames stopped while away, so the page still sat on the line it showed when it left, then
/// glided there for up to a second in front of the user. It now opens on the sung line, as it
/// does when the page itself is shown (OnPropertyChanged's snap).
/// </summary>
public class MobileLyricsResumeTests
{
    private readonly ITestOutputHelper _o;
    public MobileLyricsResumeTests(ITestOutputHelper o) => _o = o;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    [AvaloniaFact]
    public async Task BackFromTheBackground_TheLyricsAreOnTheSungLine_NotGlidingThere()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"[{i * 2 / 60:00}:{i * 2 % 60:00}.50]walking down the road {i}"));
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()), nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        shell.Player.PlayTracks(new[] { new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(180) } }, 0);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
        player.RaisePositionChanged(TimeSpan.FromSeconds(6.6));   // line 3
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var scroll = page.FindControl<ScrollViewer>("LyricsScroll")!;
        void Run(double ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Thread.Sleep(16);
            } while (clock.Elapsed.TotalMilliseconds < ms);
        }
        Run(1500);
        var before = scroll.Offset.Y;

        shell.IsSuspended = true;                                   // away for a minute
        player.RaisePositionChanged(TimeSpan.FromSeconds(80.6));  // line 40 by now
        Run(100);
        shell.IsSuspended = false;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var onReturn = scroll.Offset.Y;
        Run(1500);                                                  // any glide is over
        var settled = scroll.Offset.Y;
        _o.WriteLine($"offset before {before:0}, on return {onReturn:0}, settled {settled:0}");

        Assert.Equal(40, shell.Lyrics.ScrollLineIndex);
        Assert.True(settled - before > 500, "the page moved on to the later line");
        Assert.Equal(settled, onReturn, 0);
        window.Close();
    }
}
