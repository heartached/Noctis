using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #207 "Improve Ctrl+A for all files in queue" (and #206, whose ReplayGain shortcut
/// reads the queue selection under the same rule). Real clicks and key presses on a window
/// laid out like MainWindow: a focusable root panel, the page, the queue panel (header text +
/// rows) and the player island. Before the fix only keyboard focus in the panel counted, and
/// a click on the header or the island moved focus out of it.
/// </summary>
public class QueueKeyFocusTests
{
    private sealed class HostWindow : Window, IPageKeyOverlayHost
    {
        public QueueKeyFocus? Queue { get; set; }
        public bool PageHasNothingToSelect { get; set; }
        public bool IsOverlayCapturingKeys => Queue?.IsKeyTarget(PageHasNothingToSelect) == true;
    }

    private sealed class Layout
    {
        public required HostWindow Window { get; init; }
        public required TextBlock QueueHeader { get; init; }
        public required ListBox QueueList { get; init; }
        public required Border QueuePanel { get; init; }
        public required Button IslandButton { get; init; }
        public required ListBox PageList { get; init; }
        public required TextBox PageSearch { get; init; }
        public required QueueKeyFocus Queue { get; init; }
        public int QueueCtrlA { get; set; }
        public int PageCtrlA { get; set; }
    }

    private static Layout Build()
    {
        var window = new HostWindow { Width = 800, Height = 500 };
        var header = new TextBlock { Text = "Queue", Height = 40 };
        var queueList = new ListBox { ItemsSource = new[] { "a", "b", "c" }, Height = 200 };
        DockPanel.SetDock(header, Dock.Top);
        // The drawer has its own fill (Border#QueuePopupPanel style), so the header area hits it.
        var panel = new Border { Width = 220, Background = Avalonia.Media.Brushes.Black, Child = new DockPanel { Children = { header, queueList } } };
        var islandButton = new Button { Content = "Play", Width = 80, Height = 40 };
        var island = new Border { Name = "Island", Child = islandButton };
        var search = new TextBox { Width = 200 };
        var pageList = new ListBox { ItemsSource = new[] { "x", "y" }, Height = 120 };
        var page = new StackPanel { Children = { search, pageList } };
        // MainWindow's RootPanel is focusable: a click on plain text lands focus there.
        var root = new DockPanel { Focusable = true, Background = Avalonia.Media.Brushes.Transparent };
        DockPanel.SetDock(panel, Dock.Right);
        DockPanel.SetDock(island, Dock.Bottom);
        root.Children.Add(panel);
        root.Children.Add(island);
        root.Children.Add(page);

        Layout? layout = null;
        var queue = new QueueKeyFocus(window, () => panel, () => page,
            v => v.GetSelfAndVisualAncestors().Contains(island));
        window.Queue = queue;

        // As MainWindow: the queue-row keys tunnel at the window (constructor time) ...
        window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Source is TextBox || !queue.IsKeyTarget(window.PageHasNothingToSelect)) return;
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control) { layout!.QueueCtrlA++; e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        // ... and the page's Ctrl+A arrives later through its forwarder (so it runs first).
        _ = new WindowKeyForwarder(page, (_, e) =>
        {
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control) { layout!.PageCtrlA++; e.Handled = true; }
        });

        window.Content = root;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        layout = new Layout
        {
            Window = window, QueueHeader = header, QueueList = queueList, QueuePanel = panel,
            IslandButton = islandButton, PageList = pageList, PageSearch = search, Queue = queue,
        };
        return layout;
    }

    private static Point Center(Window w, Visual v) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), w)!.Value;

    private static void Click(Window w, Visual v)
    {
        var p = Center(w, v);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CtrlA(Window w)
    {
        w.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        w.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
    }

    private static Visual Row(ListBox list, int i) => list.ContainerFromIndex(i)!;

    [AvaloniaFact]
    public void ClickOnQueueHeader_FocusLeavesThePanel_ButCtrlAIsTheQueues()
    {
        var l = Build();
        Click(l.Window, l.QueueHeader);

        // The root cause: plain header text isn't focusable, so focus went to the root panel.
        Assert.False(l.Queue.HasFocus);
        Assert.True(l.Queue.IsKeyTarget());

        CtrlA(l.Window);
        Assert.Equal(1, l.QueueCtrlA);
        Assert.Equal(0, l.PageCtrlA);
    }

    [AvaloniaFact]
    public void ClickOnTheIsland_AfterTheQueue_KeepsCtrlAOnTheQueue()
    {
        var l = Build();
        Click(l.Window, Row(l.QueueList, 1));
        Assert.True(l.Queue.HasFocus);

        Click(l.Window, l.IslandButton);
        Assert.False(l.Queue.HasFocus); // the island's button took focus
        CtrlA(l.Window);
        Assert.Equal(1, l.QueueCtrlA);
        Assert.Equal(0, l.PageCtrlA);
    }

    [AvaloniaFact]
    public void ClickOnThePage_HandsCtrlABackToThePage()
    {
        var l = Build();
        Click(l.Window, l.QueueHeader);
        Click(l.Window, Row(l.PageList, 0));
        CtrlA(l.Window);
        Assert.Equal(0, l.QueueCtrlA);
        Assert.Equal(1, l.PageCtrlA);

        // The island alone never arms the queue.
        Click(l.Window, l.IslandButton);
        CtrlA(l.Window);
        Assert.Equal(0, l.QueueCtrlA);
        Assert.Equal(2, l.PageCtrlA);
    }

    [AvaloniaFact]
    public void KeyboardFocusMovedIntoThePage_WinsOverAnArmedQueue()
    {
        var l = Build();
        Click(l.Window, l.QueueHeader);
        // Tab into the page's list.
        Assert.True(((InputElement)Row(l.PageList, 0)).Focus(NavigationMethod.Tab));
        Assert.False(l.Queue.IsKeyTarget());
        CtrlA(l.Window);
        Assert.Equal(1, l.PageCtrlA);
        Assert.Equal(0, l.QueueCtrlA);
    }

    [AvaloniaFact]
    public void PageWithNothingToSelect_CtrlAAlwaysSelectsTheQueue()
    {
        var l = Build();
        l.Window.PageHasNothingToSelect = true; // Lyrics / Visualizer
        Click(l.Window, l.IslandButton);
        CtrlA(l.Window);
        Assert.Equal(1, l.QueueCtrlA);
    }

    [AvaloniaFact]
    public void TextBoxFocused_CtrlAStaysInTheTextBox()
    {
        var l = Build();
        l.Window.PageHasNothingToSelect = true;
        Click(l.Window, l.QueueHeader);
        Click(l.Window, l.PageSearch);
        Assert.True(l.PageSearch.IsFocused);
        CtrlA(l.Window);
        Assert.Equal(0, l.QueueCtrlA);
        Assert.Equal(0, l.PageCtrlA);
    }

    [AvaloniaFact]
    public void HiddenOrClosedPanel_IsNeverTheTarget()
    {
        var l = Build();
        Click(l.Window, l.QueueHeader);
        l.Queue.Disarm();
        Assert.False(l.Queue.IsKeyTarget());

        Click(l.Window, l.QueueHeader);
        l.QueuePanel.IsVisible = false;
        Assert.False(l.Queue.IsKeyTarget(pageHasNothingToSelect: true));
    }
}
