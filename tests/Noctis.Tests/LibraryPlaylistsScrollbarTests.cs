using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #83: with more than 5 playlists the Playlists grid "freaked out" — every tile
/// flipped between two sizes 16 px apart (the vertical scrollbar column: Width 14 +
/// Margin 2). The tile art is square (Height bound to its own width), so the grid's height
/// depends on its width; with VerticalScrollBarVisibility="Auto" the persistent scrollbar
/// then showed/hid on each pass inside a ~6 px window-height band (bar shown → tiles
/// narrower → content fits → bar hidden → tiles wider → content overflows → …). The playback
/// bar's visualizer forces a layout pass every frame while playing, which is why it
/// flickered much faster then. The Albums page pins its bar Visible for the same reason.
/// </summary>
public class LibraryPlaylistsScrollbarTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    [AvaloniaFact]
    public void PlaylistsGrid_ViewportWidth_StaysPut_AcrossWindowHeights()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var vm = new LibraryPlaylistsViewModel(sidebar, player, lib, persistence);
        for (var i = 0; i < 7; i++)
            sidebar.PlaylistItems.Add(new PlaylistNavItem { Key = $"playlist:{i}", Label = $"Playlist {i}", MetaText = "16 tracks · 54 min" });
        Assert.Equal(7, vm.FilteredPlaylists.Count);

        // Stand-in for the playback bar: something outside the grid that re-layouts every frame.
        var pulse = new Border { Height = 40 };
        var bar = new Border { Height = 90, Child = pulse };
        DockPanel.SetDock(bar, Dock.Bottom);
        var view = new LibraryPlaylistsView { DataContext = vm };
        var window = new Window { Width = 1838, Height = 900, Content = new DockPanel { Children = { bar, view } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();

        var unstable = new List<string>();
        for (var h = 900; h <= 1100; h++)
        {
            window.Height = h;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var widths = new HashSet<double>();
            for (var frame = 0; frame < 12; frame++)
            {
                pulse.InvalidateMeasure();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                widths.Add(scroller.Viewport.Width);
            }
            if (widths.Count > 1)
                unstable.Add($"h={h}: {string.Join("/", widths)}");
        }

        Assert.True(unstable.Count == 0, "grid viewport oscillated: " + string.Join("; ", unstable));
    }
}
