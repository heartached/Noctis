using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone lyrics page mounts, shows the layers behind their toggles, scales with
/// the system font, and anchors the active line.</summary>
public class MobileLyricsPageTests
{
    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private static (ShellViewModel Shell, FakeAudioPlayer Player, FakeTrackFiles Files, string Root) MakeShell()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        return (shell, player, files, root);
    }

    private static (ShellView View, Window Window) Mount(ShellViewModel shell)
    {
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static Track NewTrack() =>
        new() { Id = Guid.NewGuid(), Title = "Song", Artist = "Tester", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };

    [AvaloniaFact]
    public void IssueExample_ShowsWordsAndTranslation_AndTheToggleHidesIt()
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        player.RaisePositionChanged(TimeSpan.FromSeconds(2.5));
        shell.Lyrics.OnFrame(1000);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.True(page.IsVisible);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "例えば" && t.IsEffectivelyVisible);
        var translation = page.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "For example, if I were not myself");
        Assert.True(translation.IsEffectivelyVisible);
        Assert.True(page.FindControl<Control>("TranslationToggle")!.IsVisible);

        shell.Lyrics.ShowTranslation = false;
        window.UpdateLayout();
        Assert.False(translation.IsEffectivelyVisible);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void NoLyrics_ShowsTheStatus()
    {
        var (shell, _, _, root) = MakeShell();
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var status = page.FindControl<TextBlock>("StatusText")!;
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal("No lyrics", status.Text);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void SystemFontScale_SizesTheLyrics()
    {
        var (shell, _, _, root) = MakeShell();
        var (view, window) = Mount(shell);

        shell.Lyrics.FontScale = 1.5;
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.5, page.FindControl<ItemsControl>("LyricsList")!.FontSize, 6);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>The index only advances on frames, and frames only run while the page is
    /// visible, so opening it mid-song must catch up before the first snap — otherwise the
    /// page snaps to a stale line and then glides (playing) or stays wrong (paused).</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningMidSong_ShowsTheCurrentLine(bool pauseBeforeOpening)
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        var (_, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // lyrics page closed throughout
        player.RaisePositionChanged(TimeSpan.FromSeconds(5.5));   // second line starts at 5.0 s
        if (pauseBeforeOpening) shell.Player.TogglePlayPauseCommand.Execute(null);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        Assert.Equal(1, shell.Lyrics.ActiveLineIndex);
        Assert.Equal(1.0, shell.Lyrics.Lines[1].LineOpacity, 6);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void ScrollToLine_PutsTheLineOnTheAnchor()
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"[00:{i:00}.50]line {i}"));
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var scroll = page.FindControl<ScrollViewer>("LyricsScroll")!;
        var list = page.FindControl<ItemsControl>("LyricsList")!;
        page.ScrollToLine(30, animate: false);
        window.UpdateLayout();

        var container = list.ContainerFromIndex(30)!;
        var top = container.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        var expected = scroll.Viewport.Height * LyricsPage.AnchorRatio - container.Bounds.Height / 2;
        Assert.InRange(top, expected - 2, expected + 2);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
