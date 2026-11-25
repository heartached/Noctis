using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Noctis.Views;

/// <summary>
/// Discord (Luwi 11-25): drag the sidebar's right edge to switch between the expanded sidebar
/// and the icon rail without opening Settings. A snap between the two existing widths, not a
/// free resize: dragging left past <see cref="DragThreshold"/> unpins an expanded sidebar,
/// dragging right past it expands and pins the rail ("Keep sidebar expanded"). A click
/// without a drag does nothing, and one drag flips it at most once.
/// The strip sits in the gap between the rail and the page, so it never covers either.
/// </summary>
internal sealed class SidebarResizeGrip
{
    /// <summary>The rail's left margin (SidebarWrapper Margin.Left).</summary>
    public const double RailLeft = 8;

    /// <summary>How far the pointer must travel from the press, in DIPs, to flip the sidebar.</summary>
    public const double DragThreshold = 40;

    private readonly Border _grip;
    private readonly Func<bool> _isHidden;
    private readonly Func<bool> _pinned;
    private readonly Func<bool, bool> _setPinned;
    private double? _pressX;
    private bool _flipped;

    public SidebarResizeGrip(Border grip, Func<bool> isHidden, Func<bool> pinned, Func<bool, bool> setPinned)
    {
        _grip = grip;
        _isHidden = isHidden;
        _pinned = pinned;
        _setPinned = setPinned;
        _grip.PointerPressed += OnPressed;
        _grip.PointerMoved += OnMoved;
        _grip.PointerReleased += OnReleased;
        _grip.PointerCaptureLost += (_, _) => _pressX = null;
    }

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => _pressX != null;

    /// <summary>
    /// The pinned state a drag of <paramref name="deltaX"/> DIPs from the press asks for, or
    /// null when it asks for no change (too short, or towards the side it already is on).
    /// </summary>
    public static bool? Decide(bool pinned, double deltaX) =>
        pinned
            ? (deltaX <= -DragThreshold ? false : null)
            : (deltaX >= DragThreshold ? true : null);

    /// <summary>Places the strip at the rail's right edge for the current state, and hides it
    /// while the sidebar is hidden (fullscreen lyrics, "Hide sidebar until hover").</summary>
    public void Sync()
    {
        var hidden = _isHidden();
        _grip.IsVisible = !hidden;
        if (hidden) return;
        var width = _pinned() ? SidebarEdgePeek.ExpandedWidth : SidebarEdgePeek.RailWidth;
        var m = _grip.Margin;
        _grip.Margin = new Thickness(RailLeft + width, m.Top, m.Right, m.Bottom);
    }

    /// <summary>Pointer X in the strip's parent: the strip itself moves when the sidebar flips.</summary>
    private double X(PointerEventArgs e) => e.GetPosition(_grip.GetVisualParent() ?? _grip).X;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed || _isHidden()) return;
        _pressX = X(e);
        _flipped = false;
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressX is not { } start || _flipped) return;
        if (Decide(_pinned(), X(e) - start) is not { } target) return;
        _flipped = true;
        if (_setPinned(target)) Sync();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressX == null) return;
        _pressX = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
