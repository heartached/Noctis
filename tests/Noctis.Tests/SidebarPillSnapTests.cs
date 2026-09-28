using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "When I click Songs/Albums/Artists on the sidebar they flicker" (09-27). The selected pill
/// tweened in 70ms from the hover wash (#14FFFFFF) / Transparent (#00FFFFFF) to the accent.
/// Avalonia lerps colours in linear light, so the frames in between were a pale grey,
/// half-opaque pill on the clicked row AND on the row losing the selection. Transitions are
/// left live here: nulling them would hide the bug.
/// </summary>
public class SidebarPillSnapTests
{
    private static readonly Color Accent = Color.Parse("#C43D49");
    private static readonly Color AccentDark1 = Color.Parse("#A8323D");

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Frame()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { Frame(); Thread.Sleep(4); }
    }

    private static Color PillColor(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter")
            .Background is ISolidColorBrush b ? b.Color : Colors.Transparent;

    /// <summary>Records every distinct pill colour of both rows over ~200ms of wall time
    /// (the tween runs on real time).</summary>
    private static (List<Color> Selected, List<Color> Deselected) Sample(ListBoxItem selected, ListBoxItem deselected)
    {
        var a = new List<Color>();
        var b = new List<Color>();
        var end = Environment.TickCount64 + 200;
        while (Environment.TickCount64 < end)
        {
            Frame();
            var s = PillColor(selected);
            var d = PillColor(deselected);
            if (a.Count == 0 || a[^1] != s) a.Add(s);
            if (b.Count == 0 || b[^1] != d) b.Add(d);
            Thread.Sleep(2);
        }
        return (a, b);
    }

    [AvaloniaTheory]
    [InlineData(true)]  // a real click: the row is hovered, so :selected:pointerover paints it
    [InlineData(false)]
    public void SelectingASection_PillSnaps_WithoutTweeningThroughGrey(bool hovered)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var vm = new SidebarViewModel(new TestPersistenceService(), lib);
        var view = new SidebarView { DataContext = vm, Width = 240 };
        var win = new Window
        {
            Width = 400, Height = 900, Content = view,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
        };
        win.Resources["AccentColorBrush"] = new SolidColorBrush(Accent);
        win.Resources["AccentColorBrushDark1"] = new SolidColorBrush(AccentDark1);
        win.Show();

        var albums = vm.NavItems.First(n => n.Key == "albums");
        var songs = vm.NavItems.First(n => n.Key == "songs");
        vm.SelectedNavItem = albums;
        Settle(150);
        var list = view.FindControl<ListBox>("NavList")!;
        var songsRow = (ListBoxItem)list.ContainerFromItem(songs)!;
        var albumsRow = (ListBoxItem)list.ContainerFromItem(albums)!;
        if (hovered) ((IPseudoClasses)songsRow.Classes).Set(":pointerover", true);
        Settle(150);

        list.SelectedItem = songs;
        var (toSelected, toRest) = Sample(songsRow, albumsRow);

        Assert.Equal(new[] { hovered ? AccentDark1 : Accent }, toSelected);
        Assert.Equal(new[] { Colors.Transparent }, toRest);
        win.Close();
    }
}
