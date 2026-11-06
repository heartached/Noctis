using Avalonia;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Beta 2 request ("on iOS you can press and hold and drag the pill from one side to the other"):
/// a finger that slides sideways along the tab bar carries the selection pill with it, lifted as
/// it is on a tap, and letting go selects the tab under the finger. Only the release navigates:
/// a tab change rebuilds the shown page (SyncPages, FadeInTab) and pops it to its root, so passing
/// over a tab must not do that.
///
/// It takes the press only once it has moved <see cref="TrackSwipe.SlopDip"/> sideways before it
/// moved that far up or down (the cover swipe's rule): a tap stays the tab's tap, and a vertical
/// move is left alone. Once it is a drag the pointer is captured, so the tab under the finger lets
/// go without clicking. Let go over the tab already selected and the pill only settles back: a
/// drag is not the re-tap that pops a tab to its root. No haptic tick: the app has no haptics path.
/// </summary>
public partial class ShellView
{
    private bool _tabTracking, _tabDragging;
    private Point _tabStart;
    private int _tabDragIndex;
    private Transitions? _pillHostTransitions;

    /// <summary>A pill drag is under the finger now (internal for tests).</summary>
    internal bool IsDraggingTabPill => _tabDragging;

    private void AttachTabDrag()
    {
        TabRow.AddHandler(PointerPressedEvent, OnTabRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabRow.AddHandler(PointerMovedEvent, OnTabRowMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabRow.AddHandler(PointerReleasedEvent, OnTabRowReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabRow.AddHandler(PointerCaptureLostEvent, OnTabRowCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The tab under <paramref name="x"/> (dp across a bar <paramref name="barWidth"/> wide).</summary>
    internal static int TabAt(double barWidth, double x)
        => Math.Clamp((int)Math.Floor(x / Math.Max(1, barWidth / TabCount)), 0, TabCount - 1);

    /// <summary>Where the pill starts while dragged: centred under the finger, but never past the
    /// first or last tab's place, at the width of the tab under the finger.</summary>
    internal static double DraggedPillX(double barWidth, double x)
    {
        var tab = barWidth / TabCount;
        var centre = Math.Clamp(x, tab / 2, barWidth - tab / 2);
        return centre - SelectionPillWidth(barWidth, TabAt(barWidth, x)) / 2;
    }

    private void OnTabRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _tabTracking = _vm is { IsTabBarCollapsed: false } && TabRow.IsHitTestVisible && TabRow.Bounds.Width > 0;
        _tabDragging = false;
        if (_tabTracking) _tabStart = e.GetPosition(TabRow);
    }

    private void OnTabRowMoved(object? sender, PointerEventArgs e)
    {
        if (!_tabTracking) return;
        var p = e.GetPosition(TabRow);
        if (!_tabDragging)
        {
            var axis = TrackSwipe.Lock(p.X - _tabStart.X, p.Y - _tabStart.Y);
            if (axis == SwipeAxis.Vertical) { _tabTracking = false; return; }
            if (axis == SwipeAxis.Undecided) return;
            _tabDragging = true;
            e.Pointer.Capture(TabRow);   // the tab under the finger lets go and does not click
            // Direct sets per move: the slide's spring would restart on every one.
            _pillHostTransitions = SelectionPillHost.Transitions;
            SelectionPillHost.Transitions = null;
            _settleTimer?.Stop();
            SelectionPill.Lift = 1;
            _tabDragIndex = -1;
        }
        FollowTabDrag(p.X);
        e.Handled = true;
    }

    private void FollowTabDrag(double x)
    {
        var width = TabRow.Bounds.Width;
        var index = TabAt(width, x);
        if (index != _tabDragIndex)
        {
            _tabDragIndex = index;
            SelectionPill.Width = SelectionPillWidth(width, index);   // eases (its Width transition)
        }
        SelectionPillHost.RenderTransform = Motion.TranslateX(DraggedPillX(width, x));
    }

    private void OnTabRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tabDragging)
        {
            _tabTracking = false;
            return;
        }
        var index = TabAt(TabRow.Bounds.Width, e.GetPosition(TabRow).X);
        EndTabDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (_vm is not { } vm) return;
        var tab = TabFromIndex(index);
        if (tab != vm.SelectedTab) vm.SelectTabCommand.Execute(tab);   // ApplyChromeLayout lands the pill
        else
        {
            ApplyChromeLayout();
            SelectionPill.Lift = 0;
        }
    }

    private void OnTabRowCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Taken away mid-drag (the system shade, another gesture): back on the selected tab.
        if (!_tabDragging) return;
        EndTabDrag();
        ApplyChromeLayout();
        SelectionPill.Lift = 0;
    }

    private void EndTabDrag()
    {
        _tabTracking = false;
        _tabDragging = false;
        if (_pillHostTransitions != null) SelectionPillHost.Transitions = _pillHostTransitions;
        _pillHostTransitions = null;
    }

    private static MobileTab TabFromIndex(int index) => index switch
    {
        0 => MobileTab.Home,
        1 => MobileTab.Favorites,
        2 => MobileTab.Playlists,
        3 => MobileTab.Library,
        _ => MobileTab.Search,
    };
}
