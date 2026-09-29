using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Avalonia calls one element's tunnel handlers newest first. MainWindow adds its queue-row
/// key handler in its constructor; a page's <see cref="WindowKeyForwarder"/> is added later,
/// on page attach, so it runs first. Before the fix the page took Ctrl+A / Escape even with
/// focus in the queue panel (selecting every song behind the panel) and cleared its hidden
/// selection on Escape while the Settings sheet was open instead of letting Escape close it.
/// These tests mirror that layout: window handlers first, page forwarder attached after.
/// </summary>
public class PageKeyOverlayRoutingTests
{
    private sealed class OverlayHostWindow : Window, IPageKeyOverlayHost
    {
        public Border? QueuePanel { get; set; }
        public bool IsModalOpen { get; set; }

        // Same shape as MainWindow: a modal sheet, or focus inside the queue panel.
        public bool IsOverlayCapturingKeys =>
            IsModalOpen
            || (QueuePanel is { } panel
                && FocusManager?.GetFocusedElement() is Avalonia.Visual focused
                && (focused == panel || panel.IsVisualAncestorOf(focused)));
    }

    private sealed class Layout
    {
        public required Window Window { get; init; }
        public required Button SidebarButton { get; init; }
        public required Button QueueRow { get; init; }
        public int PageCtrlA { get; set; }
        public int PageEscape { get; set; }
        public int QueueCtrlA { get; set; }
        public int WindowEscape { get; set; }
    }

    private static Layout Build(Window window, Border queuePanel)
    {
        var sidebarButton = new Button { Content = "Songs" };
        var queueRow = (Button)queuePanel.Child!;
        var page = new Border { Focusable = true };
        Layout? layout = null;

        // Constructor-time window handlers, as in MainWindow: the queue keys (tunnel, only
        // while focus is in the panel) and the Escape that closes the topmost surface (bubble).
        window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            var focused = window.FocusManager?.GetFocusedElement() as Avalonia.Visual;
            if (focused is null || !queuePanel.IsVisualAncestorOf(focused)) return;
            if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
            {
                layout!.QueueCtrlA++;
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            layout!.WindowEscape++;
            e.Handled = true;
        };

        // The page (Songs view) with a selection: Escape clears it, Ctrl+A selects all.
        _ = new WindowKeyForwarder(page, (_, e) =>
        {
            if (e.Key == Key.Escape) { layout!.PageEscape++; e.Handled = true; }
            else if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control) { layout!.PageCtrlA++; e.Handled = true; }
        });

        // Attaching the content now registers the forwarder after the window's handlers.
        window.Width = 600;
        window.Height = 400;
        window.Content = new StackPanel { Children = { sidebarButton, page, queuePanel } };
        window.Show();

        layout = new Layout { Window = window, SidebarButton = sidebarButton, QueueRow = queueRow };
        return layout;
    }

    private static Border NewQueuePanel() => new() { Child = new Button { Content = "Queue row" } };

    private static void Press(Window window, PhysicalKey key, RawInputModifiers mods)
    {
        window.KeyPressQwerty(key, mods);
        window.KeyReleaseQwerty(key, mods);
    }

    [AvaloniaFact]
    public void PlainWindow_PageForwarderRunsBeforeWindowTunnelHandler()
    {
        // Pins the Avalonia ordering the fix works around: without a gate the later-added
        // page forwarder takes Ctrl+A even though focus sits in the queue panel.
        var layout = Build(new Window(), NewQueuePanel());

        layout.QueueRow.Focus();
        Press(layout.Window, PhysicalKey.A, RawInputModifiers.Control);

        Assert.Equal(1, layout.PageCtrlA);
        Assert.Equal(0, layout.QueueCtrlA);
    }

    [AvaloniaFact]
    public void CtrlA_WithFocusInQueuePanel_GoesToQueueNotPage()
    {
        var panel = NewQueuePanel();
        var layout = Build(new OverlayHostWindow { QueuePanel = panel }, panel);

        layout.QueueRow.Focus();
        Press(layout.Window, PhysicalKey.A, RawInputModifiers.Control);

        Assert.Equal(0, layout.PageCtrlA);
        Assert.Equal(1, layout.QueueCtrlA);
    }

    [AvaloniaFact]
    public void Escape_WithFocusInQueuePanel_SkipsPageSelection()
    {
        var panel = NewQueuePanel();
        var layout = Build(new OverlayHostWindow { QueuePanel = panel }, panel);

        layout.QueueRow.Focus();
        Press(layout.Window, PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Equal(0, layout.PageEscape);
        Assert.Equal(1, layout.WindowEscape);
    }

    [AvaloniaFact]
    public void Escape_WithModalOpen_ReachesWindowInsteadOfClearingHiddenPageSelection()
    {
        var panel = NewQueuePanel();
        var window = new OverlayHostWindow { QueuePanel = panel };
        var layout = Build(window, panel);
        window.IsModalOpen = true;

        layout.SidebarButton.Focus();
        Press(layout.Window, PhysicalKey.Escape, RawInputModifiers.None);
        Press(layout.Window, PhysicalKey.A, RawInputModifiers.Control);

        Assert.Equal(0, layout.PageEscape);
        Assert.Equal(0, layout.PageCtrlA);
        Assert.Equal(1, layout.WindowEscape);
    }

    [AvaloniaFact]
    public void CtrlA_WithFocusOutsidePageAndNoOverlay_StillReachesPage()
    {
        // The forwarder's purpose: Ctrl+A works right after navigating from the sidebar,
        // with no priming click inside the page.
        var panel = NewQueuePanel();
        var layout = Build(new OverlayHostWindow { QueuePanel = panel }, panel);

        layout.SidebarButton.Focus();
        Press(layout.Window, PhysicalKey.A, RawInputModifiers.Control);

        Assert.Equal(1, layout.PageCtrlA);
        Assert.Equal(0, layout.QueueCtrlA);
    }
}
