using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone shell mounts headlessly, and the mini bar appears once a track plays.</summary>
public class MobileShellViewTests
{
    private sealed class NoPicker : IFolderPicker
    {
        public System.Threading.Tasks.Task<string?> PickFolderAsync() => System.Threading.Tasks.Task.FromResult<string?>(null);
    }

    private static ShellViewModel MakeShell(string root, out FakeLibraryService library, params Track[] tracks)
    {
        library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        return new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
    }

    [Fact]
    public void TryHandleBack_ClosesLyricsBeforeNowPlaying()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var shell = MakeShell(root, out _);

        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        Assert.True(shell.IsLyricsOpen);

        Assert.True(shell.TryHandleBack());
        Assert.False(shell.IsLyricsOpen);
        Assert.True(shell.IsNowPlayingOpen);

        shell.ToggleLyricsCommand.Execute(null);
        shell.CloseNowPlayingCommand.Execute(null);        // closing Now Playing closes its overlays
        Assert.False(shell.IsLyricsOpen);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>
    /// Android Back must close the topmost overlay rather than finish the activity — both
    /// pages are full-screen overlays inside the single activity, so the system default reads
    /// as the app quitting. The activity delegates the whole decision here.
    /// </summary>
    [Fact]
    public void TryHandleBack_ClosesTheTopmostOverlay_ThenFallsThrough()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var shell = MakeShell(root, out _);

        Assert.False(shell.TryHandleBack());               // Library page: the system handles it

        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleQueueCommand.Execute(null);
        Assert.True(shell.IsQueueOpen);

        Assert.True(shell.TryHandleBack());                // innermost first
        Assert.False(shell.IsQueueOpen);
        Assert.True(shell.IsNowPlayingOpen);

        Assert.True(shell.TryHandleBack());
        Assert.False(shell.IsNowPlayingOpen);

        Assert.False(shell.TryHandleBack());               // back at the Library page
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void Shell_Mounts_AndMiniBarFollowsPlayback()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var track = new Track { Id = Guid.NewGuid(), Title = "Mounted Song", Artist = "Tester", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };
        var shell = MakeShell(root, out _, track);
        shell.Library.InitializeAsync().GetAwaiter().GetResult();

        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();

        var miniBar = view.FindControl<Border>("MiniBar")!;
        Assert.False(miniBar.IsVisible);
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Mounted Song");

        shell.PlaySongCommand.Execute(track);
        window.UpdateLayout();

        Assert.True(miniBar.IsVisible);
        Assert.Contains(miniBar.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Mounted Song");

        shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();
        var nowPlaying = view.FindControl<NowPlayingPage>("NowPlaying")!;
        Assert.True(nowPlaying.IsVisible);
        Assert.False(miniBar.IsVisible);

        // Pins the TimeSpan format on the seek-bar labels: a mis-escaped format string
        // throws FormatException at bind time and silently leaves the label empty.
        Assert.Contains(nowPlaying.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "01:30");

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>
    /// Android device run, 2026-09-22: an x-sweep across the mini bar found only x≈80–270 of
    /// the ~730 px row opened Now Playing — Fluent's Button theme sets HorizontalAlignment to
    /// Left, so the title button collapsed onto its text and the rest of the bar swallowed
    /// taps. Measures the button against its own star column rather than a pixel count, so it
    /// holds at any width.
    /// </summary>
    [AvaloniaFact]
    public void MiniBar_TitleButton_FillsTheWholeRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var track = new Track { Id = Guid.NewGuid(), Title = "Tone A", Artist = "Noctis Test", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(8) };
        var shell = MakeShell(root, out _, track);
        shell.Library.InitializeAsync().GetAwaiter().GetResult();

        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        shell.PlaySongCommand.Execute(track);
        window.UpdateLayout();

        var miniBar = view.FindControl<Border>("MiniBar")!;
        var grid = (Grid)miniBar.Child!;
        var title = grid.Children.OfType<Button>().First(b => Grid.GetColumn(b) == 0);

        // The star column's own width, from the first fixed-width sibling's left edge.
        var transports = grid.Children.OfType<Button>().Where(b => Grid.GetColumn(b) > 0).ToList();
        var columnWidth = transports.Min(b => b.Bounds.Left);

        Assert.Equal(columnWidth, title.Bounds.Width, 1);
        Assert.True(title.Bounds.Height >= grid.Bounds.Height - 1,
            $"title button is {title.Bounds.Height} tall in a {grid.Bounds.Height} row");

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
