using Avalonia.Controls;
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
/// Now Playing's and the lyrics page's seek bar and times are bound to the player's position,
/// which ticks four times a second. Hidden (Now Playing closed, under the lyrics, the lyrics'
/// transport faded), each tick still marked them for redraw: a render pass on the UI thread every
/// tick and, once a second, both pages' times laid out again, for as long as music played. They
/// are now fed only while their page shows them, and read the current position before they show.
/// </summary>
public class MobileHiddenTimelineTests
{
    private readonly ITestOutputHelper _o;
    public MobileHiddenTimelineTests(ITestOutputHelper o) => _o = o;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private static (ShellViewModel Shell, FakeAudioPlayer Player, ShellView View, Window Window) Mount(string? lrc = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        if (lrc != null) files.Sidecars[".lrc"] = lrc;
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()), nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        shell.Player.PlayTracks(new[] { new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(240) } }, 0);
        return (shell, player, view, window);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>The position ticks of <paramref name="seconds"/> of playback from <paramref name="from"/>,
    /// each followed by the UI jobs it queued; the UI thread's bytes per tick.</summary>
    private static long BytesPerTick(FakeAudioPlayer player, double from, double seconds)
    {
        var ticks = (int)(seconds * 4);
        player.RaisePositionChanged(TimeSpan.FromSeconds(from));
        Dispatcher.UIThread.RunJobs();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= ticks; i++)
        {
            player.RaisePositionChanged(TimeSpan.FromSeconds(from + i * 0.25));
            Dispatcher.UIThread.RunJobs();
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before) / ticks;
    }

    private static T Named<T>(Control root, string name) where T : Control => root.FindControl<T>(name)!;

    [AvaloniaFact]
    public void ClosedPages_AreNotFed_AndShowTheCurrentPositionWhenOpened()
    {
        var (shell, player, view, window) = Mount();
        Settle(window);
        var nowPlaying = view.GetLogicalDescendants().OfType<NowPlayingPage>().Single();
        var lyrics = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.False(nowPlaying.IsVisible);
        Assert.False(lyrics.IsVisible);

        var perTick = BytesPerTick(player, 10, 8);
        _o.WriteLine($"library showing, Now Playing and lyrics closed: {perTick} B a position tick");
        Assert.True(perTick < 1000, $"{perTick} B a tick");   // 1.76 KB fed, 0.42 KB not (11-22, headless)
        Assert.NotEqual(18, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);
        Assert.NotEqual("00:18", Named<TextBlock>(nowPlaying, "ElapsedText").Text);
        Assert.NotEqual("00:18", Named<TextBlock>(lyrics, "ElapsedText").Text);

        shell.OpenNowPlayingCommand.Execute(null);
        Settle(window);
        Assert.Equal(18, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);
        Assert.Equal(240, Named<CapsuleSlider>(nowPlaying, "SeekBar").Maximum);
        Assert.Equal("00:18", Named<TextBlock>(nowPlaying, "ElapsedText").Text);
        Assert.Equal("-03:42", Named<TextBlock>(nowPlaying, "RemainingText").Text);
        player.RaisePositionChanged(TimeSpan.FromSeconds(19));
        Assert.Equal(19, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);   // and follows it again

        // Away (home, screen off) with Now Playing on screen: still fed, so the system's
        // snapshot of the app never shows a blank bar.
        shell.IsSuspended = true;
        player.RaisePositionChanged(TimeSpan.FromSeconds(42));
        Assert.Equal(42, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);
        shell.IsSuspended = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task UnderTheLyrics_NeitherHiddenTimelineIsFed_ATouchShowsTheLyricsOneCurrent()
    {
        var (shell, player, view, window) = Mount(string.Join('\n', Enumerable.Range(0, 60).Select(i => $"[{i * 4 / 60:00}:{i * 4 % 60:00}.00]line {i}")));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!shell.Lyrics.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(10);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5));
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        Settle(window);
        var nowPlaying = view.GetLogicalDescendants().OfType<NowPlayingPage>().Single();
        var lyrics = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        lyrics.HideControlsIfPlaying();
        var layer = lyrics.FindControl<Control>("ControlsLayer")!;
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (layer.IsVisible && DateTime.UtcNow < deadline) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.False(layer.IsVisible);
        Assert.False(nowPlaying.IsVisible);

        var perTick = BytesPerTick(player, 6, 8);
        _o.WriteLine($"lyrics showing, transport faded, Now Playing under them: {perTick} B a position tick");
        Assert.True(perTick < 1000, $"{perTick} B a tick");   // 1.78 KB fed, 0.42 KB not
        Assert.NotEqual(14, Named<CapsuleSlider>(lyrics, "SeekBar").Value);
        Assert.NotEqual(14, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);

        lyrics.ShowControls();                                       // a touch
        Assert.Equal(14, Named<CapsuleSlider>(lyrics, "SeekBar").Value);
        Assert.Equal("00:14", Named<TextBlock>(lyrics, "ElapsedText").Text);
        window.Close();
    }

    /// <summary>Now Playing on screen: a position tick feeds the seek bar and the labels. The seek
    /// bar read Position.TotalSeconds (a new boxed TimeSpan source and property reader per tick)
    /// and every binding formatted both labels again on every tick: 5738 B a tick before, 5460
    /// after (11-23, headless, with layout and the UI's render pass; most of the rest is the
    /// labels laid out again once a second and the seek bar's drawing).</summary>
    [AvaloniaFact]
    public void NowPlayingShown_ATickCostsLittle()
    {
        var (shell, player, view, window) = Mount();
        shell.OpenNowPlayingCommand.Execute(null);
        Settle(window);
        var nowPlaying = view.GetLogicalDescendants().OfType<NowPlayingPage>().Single();
        Assert.True(nowPlaying.IsVisible);
        BytesPerTick(player, 10, 2);   // warm-up
        var perTick = BytesPerTick(player, 20, 8);
        _o.WriteLine($"Now Playing showing: {perTick} B a position tick");
        Assert.True(perTick < 5600, $"{perTick} B a tick");
        Assert.Equal(28, Named<CapsuleSlider>(nowPlaying, "SeekBar").Value);
        Assert.Equal("00:28", Named<TextBlock>(nowPlaying, "ElapsedText").Text);
        window.Close();
    }
}
