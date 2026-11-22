using Avalonia;
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

/// <summary>
/// Sideways, the lyrics page lays its big cover out in code from the screen's safe area (the
/// camera cutout, the status and navigation bars), but only when its own size changed. Android
/// can report the insets after the resize (and the page's size does not change when only they
/// do), so the cover kept the old margins: under the cutout, or leaving the bars' old room.
/// It now follows the safe area too.
/// </summary>
public class MobileLyricsSafeAreaTests
{
    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    [AvaloniaFact]
    public void Landscape_TheBigCover_FollowsASafeAreaThatChangesAfterTheResize()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()), nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 780, Height = 360, Content = view };
        window.Show();
        shell.Player.PlayTracks(new[] { new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "T", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) } }, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        void Settle()
        {
            for (var i = 0; i < 3; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
        Settle();
        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var art = page.FindControl<Control>("SideArtBox")!;
        var split = page.FindControl<Grid>("Split")!;
        Assert.True(art.IsVisible);
        Assert.Equal(24, art.Margin.Left);

        // The S23's insets sideways, reported after the window took its new size.
        view.ApplySafeArea(new Thickness(33, 30, 48, 0));
        Settle();

        Assert.Equal(new Thickness(33 + 24, 30, 24, 8), art.Margin);
        var expected = 33 + 24 + (780 - 33 - 48 - 48) * 5 / 11.0;
        Assert.Equal(expected, split.ColumnDefinitions[0].Width.Value, 3);
        window.Close();
    }
}
