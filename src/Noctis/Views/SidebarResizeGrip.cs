using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Models;

namespace Noctis.Views;

/// <summary>
/// Discord (Luwi 11-25): drag the sidebar's right edge to switch between the expanded sidebar
/// and the icon rail without opening Settings; dragging it pins ("Keep sidebar expanded") or
/// unpins. Discord 11-27 (Halil): the expanded sidebar resizes like Spotify's library: while
/// it is expanded its edge follows the pointer within <see cref="SidebarWidthMetrics"/>'s
/// range, and the width is saved on release. Pulled well under the narrowest width it snaps
/// to the rail; pulled out from the rail it snaps open at the narrowest width and follows
/// from there. The state follows the pointer for the whole held drag, every time it turns
/// (11-25 flipped at most once per drag). Passing the stock width it holds there for a few
/// pixels, so it is easy to get back to. Discord 11-27 (Duckyvictus): a click (press and
/// release within <see cref="DragSlop"/>) folds or unfolds it, like Ctrl/⌘+B. The strip sits
/// in the gap between the rail and the page, so it never covers either.
/// </summary>
internal sealed class SidebarResizeGrip
{
    /// <summary>The rail's left margin (SidebarWrapper Margin.Left).</summary>
    public const double RailLeft = 8;

    /// <summary>Half the dead band around the snap point, in DIPs: the sidebar does not flicker
    /// between rail and expanded while the pointer hovers at the snap point.</summary>
    public const double SnapHysteresis = 10;

    /// <summary>Halfway between the rail and the narrowest expanded sidebar: the state whose
    /// edge is nearer the pointer wins.</summary>
    public const double SnapPoint = (SidebarEdgePeek.RailWidth + SidebarWidthMetrics.Min) / 2;

    /// <summary>An expanded sidebar snaps to the rail once the pointer asks for less than this.</summary>
    public const double CollapseBelow = SnapPoint - SnapHysteresis;

    /// <summary>The rail snaps open once the pointer asks for more than this.</summary>
    public const double ExpandAbove = SnapPoint + SnapHysteresis;

    /// <summary>How far the pointer may wander from the press, in DIPs, and the press still be
    /// a click (it toggles); past it the press is a drag for good, and never toggles.</summary>
    public const double DragSlop = 4;

    /// <summary>A dragged width within this of the stock width lands on it.</summary>
    public const double DefaultDetent = 6;

    private readonly Border _grip;
    private readonly Func<bool> _isHidden;
    private readonly Func<bool> _pinned;
    private readonly Func<bool, bool> _setPinned;
    private readonly Func<double> _savedWidth;
    private readonly Action<double> _resize;
    private readonly Action<double> _commitWidth;
    private bool _dragging;
    // Past the slop: a drag, not a click.
    private bool _moved;
    private double _pressX;
    // Where in the strip the press landed, so the edge keeps that offset under the pointer.
    private double _grab;

    /// <param name="savedWidth">The saved expanded width (AppSettings.SidebarExpandedWidth).</param>
    /// <param name="resize">Lays the pinned sidebar out at a width at once, mid-drag.</param>
    /// <param name="commitWidth">Saves the width a drag ended at.</param>
    public SidebarResizeGrip(Border grip, Func<bool> isHidden, Func<bool> pinned, Func<bool, bool> setPinned,
        Func<double> savedWidth, Action<double> resize, Action<double> commitWidth)
    {
        _grip = grip;
        _isHidden = isHidden;
        _pinned = pinned;
        _setPinned = setPinned;
        _savedWidth = savedWidth;
        _resize = resize;
        _commitWidth = commitWidth;
        _grip.PointerPressed += OnPressed;
        _grip.PointerMoved += OnMoved;
        _grip.PointerReleased += OnReleased;
        _grip.PointerCaptureLost += (_, _) => End(click: false);
    }

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => _dragging;

    /// <summary>The width an expanded sidebar has under the drag, until it is saved on release;
    /// null when not dragging or while the drag holds the rail.</summary>
    public double? LiveWidth { get; private set; }

    /// <summary>The width the expanded sidebar lays out at now: the drag's, else the saved one.</summary>
    public double ExpandedWidth => LiveWidth ?? SidebarWidthMetrics.Clamp(_savedWidth());

    /// <summary>
    /// Whether the sidebar should be expanded (pinned) when the pointer asks for a rail
    /// <paramref name="width"/>, given whether it is now. A dead band around
    /// <see cref="SnapPoint"/> keeps the current state, so it never flickers there.
    /// </summary>
    public static bool Decide(bool pinned, double width) =>
        pinned ? width >= CollapseBelow : width > ExpandAbove;

    /// <summary>The expanded width a drag asking for <paramref name="width"/> lays out at: in
    /// range, on whole pixels, and held at the stock width near it.</summary>
    public static double Resized(double width)
    {
        var clamped = SidebarWidthMetrics.Clamp(width);
        return Math.Abs(clamped - SidebarWidthMetrics.Default) <= DefaultDetent ? SidebarWidthMetrics.Default : clamped;
    }

    /// <summary>Places the strip at the rail's right edge for the current state, and hides it
    /// while the sidebar is hidden (fullscreen lyrics, "Hide sidebar until hover").</summary>
    public void Sync()
    {
        var hidden = _isHidden();
        _grip.IsVisible = !hidden;
        if (hidden) return;
        var width = _pinned() ? ExpandedWidth : SidebarEdgePeek.RailWidth;
        var m = _grip.Margin;
        _grip.Margin = new Thickness(RailLeft + width, m.Top, m.Right, m.Bottom);
    }

    /// <summary>Pointer X in the strip's parent: the strip itself moves with the edge.</summary>
    private double X(PointerEventArgs e) => e.GetPosition(_grip.GetVisualParent() ?? _grip).X;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // Touch and pen contact report as the left button too.
        if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed || _isHidden() || _dragging) return;
        e.Handled = true;
        var pinned = _pinned();
        var width = pinned ? ExpandedWidth : SidebarEdgePeek.RailWidth;
        _pressX = X(e);
        _grab = _pressX - (RailLeft + width);
        LiveWidth = pinned ? width : null;
        _moved = false;
        _dragging = true;
        e.Pointer.Capture(_grip);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var x = X(e);
        if (!_moved)
        {
            if (Math.Abs(x - _pressX) < DragSlop) return; // still a click
            _moved = true;
        }
        var want = x - RailLeft - _grab; // the rail width the pointer asks for
        var pinned = _pinned();
        var target = Decide(pinned, want);
        if (target != pinned)
        {
            // Opening lays out at the drag's width (the narrowest, as the pointer is short of
            // it), not the saved one; the pin handler reads it from LiveWidth.
            var before = LiveWidth;
            LiveWidth = target ? Resized(want) : null;
            if (!_setPinned(target))
            {
                LiveWidth = before;
                return;
            }
            Sync();
            return;
        }
        if (!pinned) return;
        var width = Resized(want);
        if (LiveWidth == width) return; // pinned at the min / max, or under a pixel
        LiveWidth = width;
        _resize(width);
        Sync();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        End(click: !_moved);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Ends a press. A click (released within the slop) folds or unfolds the sidebar
    /// at its saved width, as Ctrl/⌘+B does; a drag saves the width an expanded sidebar was
    /// left at (one that ends on the rail keeps the saved width). Capture lost ends it as a
    /// drag, never a click.</summary>
    private void End(bool click)
    {
        if (!_dragging) return;
        _dragging = false;
        var live = LiveWidth;
        LiveWidth = null;
        if (click)
        {
            if (!_isHidden() && _setPinned(!_pinned())) Sync();
            return;
        }
        if (live is { } width && _pinned() && width != SidebarWidthMetrics.Clamp(_savedWidth()))
            _commitWidth(width);
    }
}
