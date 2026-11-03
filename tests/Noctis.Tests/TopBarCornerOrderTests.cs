using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-02: the round buttons at the top-right of every library page follow one order
/// (page option · Filter · Sort · View · shuffle icon + primary pill · Cover Flow), the same
/// function wears the same glyph on every page, and every button is named. The corner
/// panel is lifted out of an unshown MainWindow (showing it runs app startup) and fed a
/// TopBarViewModel set up the way MainWindowViewModel sets it up for each page.
/// Renders: NOCTIS_TEST_SKIA=1 plus NOCTIS_TOPBAR_SHOTS=&lt;prefix&gt; saves one PNG per page to
/// NOCTIS_TOPBAR_SHOTS_DIR (default: &lt;repo&gt;/renders).
/// </summary>
public class TopBarCornerOrderTests
{
    private readonly ITestOutputHelper _o;
    public TopBarCornerOrderTests(ITestOutputHelper o) => _o = o;

    private static readonly ICommand Noop = new RelayCommand(() => { });

    private static readonly string[] IconKeys =
    {
        "FilterIcon", "SortIcon", "ViewIcon", "ShuffleIcon", "FolderIcon", "CoverFlowIcon",
        "GridViewIcon", "ListViewIcon", "ReleaseTypeIcon", "ViewOptionsIcon", "SettingsIcon",
        "AlbumCarouselIcon", "LayoutIcon",
    };

    internal static readonly (string Page, Action<TopBarViewModel> Setup)[] Pages =
    {
        ("Home", _ => { }),
        ("Songs", vm =>
        {
            vm.ShowPageActions(Noop, Noop, false, true, "Date Added", Noop, Noop, Noop, Noop);
            vm.ShowSongsFilters("", "All", Noop);
        }),
        ("Albums", vm =>
        {
            vm.IsPageTitleVisible = true;
            vm.ShowReleaseTypeChips(new ObservableCollection<ReleaseTypeChip>(), Noop,
                sortCommand: Noop, releaseTypeFilterCommand: Noop, qualityFilterCommand: Noop);
        }),
        ("Artists", vm => vm.ShowArtistSort(Noop, "Name", "name", true, Noop, false)),
        ("Folders", vm => vm.ShowFoldersActions(Noop, Noop, Noop, Noop)),
        ("Favorites", vm => vm.ShowFavoritesActions(Noop, Noop)),
        ("Playlists", vm => vm.ShowPlaylistActions(Noop, Noop, Noop, Noop, "Default")),
    };

    private static void Pump(int ms = 0)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (ms > 0) Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Mounts the real corner panel (with MainWindow's corner styles) for one page.</summary>
    private static (Window Window, StackPanel Corner) Mount(Action<TopBarViewModel> setup, string? label = null)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        var corner = main.FindControl<StackPanel>("CornerActions")!;
        ((Panel)corner.Parent!).Children.Remove(corner);
        var styles = main.Styles.ToList();
        main.Styles.Clear();
        main.Close();

        var vm = new TopBarViewModel();
        vm.ShowViewModeToggle(Noop, Noop, false);
        setup(vm);
        corner.DataContext = vm;
        corner.IsVisible = true;
        corner.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(corner, 1);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 56 };
        if (label != null)
            grid.Children.Add(new TextBlock
            {
                Text = label, FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(20, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        grid.Children.Add(corner);
        var window = new Window
        {
            Width = 560, Height = 56, Content = grid,
            Background = Application.Current!.TryGetResource("AppMainBackground", Application.Current.ActualThemeVariant, out var bg)
                ? bg as IBrush : null,
        };
        window.Styles.AddRange(styles);
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        return (window, corner);
    }

    private static List<Button> VisibleButtons(StackPanel corner)
        => corner.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();

    private static string Kind(Button b)
    {
        if (b.Classes.Contains("corner-primary"))
            return "Pill:" + string.Join("", b.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        var data = b.GetLogicalDescendants().OfType<PathIcon>().FirstOrDefault()?.Data;
        foreach (var key in IconKeys)
            if (Application.Current!.TryGetResource(key, null, out var g) && ReferenceEquals(g, data))
                return key.Replace("Icon", "");
        return "?";
    }

    private static string Name(Button b)
        => ControlAutomationPeer.CreatePeerForElement(b).GetName() ?? "";

    [AvaloniaTheory]
    [InlineData("Home", "CoverFlow")]
    [InlineData("Songs", "Filter|Sort|View|Pill:Shuffle|CoverFlow")]
    [InlineData("Albums", "Filter|Sort|View|CoverFlow")]
    [InlineData("Artists", "Sort|View|CoverFlow")]
    [InlineData("Folders", "Folder|Sort|Shuffle|Pill:Play|CoverFlow")]
    [InlineData("Favorites", "Shuffle|Pill:Play|CoverFlow")]
    [InlineData("Playlists", "Sort|Pill:New|CoverFlow")]
    public void CornerButtons_OneOrderAndGlyphPerFunction_NamedAndTabbedLeftToRight(string page, string expected)
    {
        var (window, corner) = Mount(Pages.Single(p => p.Page == page).Setup);
        try
        {
            var buttons = VisibleButtons(corner);
            var kinds = buttons.Select(Kind).ToList();
            // The pill's text is localized; compare its word against the resource string.
            var want = expected.Split('|').Select(k => k switch
            {
                "Pill:Shuffle" => "Pill:" + Noctis.Localization.Loc.T("Main.ShuffleTip"),
                "Pill:Play" => "Pill:" + Noctis.Localization.Loc.T("Main.PlayTip"),
                "Pill:New" => "Pill:" + Noctis.Localization.Loc.T("Main.New"),
                _ => k,
            }).ToList();
            Assert.Equal(want, kinds);

            // Every button names itself (explicit name, tooltip, or its text).
            Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(Name(b)), $"{page}: unnamed {Kind(b)}"));

            // Same size for every round icon, and left-to-right on screen = tree order, with no
            // TabIndex overrides, so Tab walks them in the order they are seen.
            Assert.All(buttons.Where(b => b.Classes.Contains("corner-icon")), b => Assert.Equal(34, b.Bounds.Width));
            var xs = buttons.Select(b => b.TranslatePoint(new Point(0, 0), window)!.Value.X).ToList();
            Assert.Equal(xs.OrderBy(x => x).ToList(), xs);
            Assert.All(buttons, b => Assert.False(b.IsSet(KeyboardNavigation.TabIndexProperty), $"{page}: TabIndex set on {Kind(b)}"));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ArtistsViewMenu_PicksALayout_TogglingOnlyWhenItDiffers()
    {
        var toggles = 0;
        var vm = new TopBarViewModel();
        vm.ShowArtistSort(Noop, "Name", "name", true, new RelayCommand(() => toggles++), isListView: false);

        vm.SetArtistViewCommand.Execute("grid");
        Assert.Equal(0, toggles);
        vm.SetArtistViewCommand.Execute("list");
        Assert.Equal(1, toggles);

        vm.IsArtistListView = true; // MainWindowViewModel mirrors the artists VM's ViewMode
        vm.SetArtistViewCommand.Execute("list");
        Assert.Equal(1, toggles);
        vm.SetArtistViewCommand.Execute("grid");
        Assert.Equal(2, toggles);
    }

    [AvaloniaFact]
    public void Shots_SaveOneHeaderPerPage()
    {
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_TOPBAR_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(prefix)) return;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_TOPBAR_SHOTS_DIR") ?? Path.Combine(FindRepoRoot(), "renders");
        Directory.CreateDirectory(dir);
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            foreach (var (page, setup) in Pages)
            {
                var (window, _) = Mount(setup, page);
                try
                {
                    Pump(300);
                    var path = Path.Combine(dir, $"{prefix}-{page.ToLowerInvariant()}.png");
                    window.CaptureRenderedFrame()!.Save(path);
                    _o.WriteLine(path);
                }
                finally { window.Close(); }
            }
        });
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
