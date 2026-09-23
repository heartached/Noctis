using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// Up Next: drag a row by its handle to reorder, swipe it left to remove (spec §5 item 6).
/// Rows are resolved by container index, never by Track: "Add to Queue" of a queued song puts
/// the same Track in the list twice, and IndexOf would act on the first copy. The row's Track
/// is captured too: Up Next can be rebuilt mid-gesture (the track ends, a lock-screen Next), and
/// the release must then not act on whatever row now sits at the pressed index.
/// </summary>
public partial class QueuePage : UserControl
{
    private enum Gesture { None, Pending, Swipe, Drag }

    private Gesture _gesture;
    private Control? _moving;   // the row (swipe) or its container (drag) being translated
    private int _index = -1;
    private Track? _track;      // UpNext[_index] at press
    private Point _start;

    public QueuePage()
    {
        InitializeComponent();
    }

    private int IndexOf(Control element) =>
        element.FindAncestorOfType<ContentPresenter>() is { } container ? QueueList.IndexFromContainer(container) : -1;

    private Track? TrackAt(int index) =>
        DataContext is ShellViewModel vm && index >= 0 && index < vm.Player.UpNext.Count ? vm.Player.UpNext[index] : null;

    /// <summary>Whether Up Next still holds <paramref name="pressed"/> at <paramref name="index"/>.</summary>
    private bool StillAt(int index, Track? pressed) => pressed != null && ReferenceEquals(TrackAt(index), pressed);

    private void ResetGesture()
    {
        if (_moving != null)
        {
            _moving.RenderTransform = null;
            _moving.ZIndex = 0;
        }
        _moving = null;
        _index = -1;
        _track = null;
        _gesture = Gesture.None;
    }

    // ── Swipe to remove ────────────────────────────────────────────

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || _gesture is Gesture.Swipe or Gesture.Drag) return;
        ResetGesture();   // a press whose release landed elsewhere left a stale Pending
        _moving = row;
        _index = IndexOf(row);
        _track = TrackAt(_index);
        _start = e.GetPosition(this);
        _gesture = Gesture.Pending;
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender, _moving) || _gesture is not (Gesture.Pending or Gesture.Swipe)) return;
        var p = e.GetPosition(this);
        var dx = p.X - _start.X;
        if (_gesture == Gesture.Pending)
        {
            // Vertical travel belongs to the ScrollViewer; only a clearly horizontal drag is a swipe.
            if (Math.Abs(dx) < QueueGesture.SlopDip || Math.Abs(dx) <= Math.Abs(p.Y - _start.Y)) return;
            _gesture = Gesture.Swipe;
            e.Pointer.Capture(_moving);
        }
        _moving!.RenderTransform = new TranslateTransform(Math.Min(0, dx), 0);
        e.PreventGestureRecognition();   // a committed swipe keeps its vertical jitter from scrolling
        e.Handled = true;
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(sender, _moving)) return;
        if (_gesture != Gesture.Swipe)
        {
            ResetGesture();
            return;
        }
        var dx = e.GetPosition(this).X - _start.X;
        var width = _moving!.Bounds.Width;
        var (index, track) = (_index, _track);
        ResetGesture();
        CommitSwipe(index, track, dx, width);
        e.Handled = true;
    }

    private void OnRowCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(sender, _moving) && _gesture != Gesture.Drag) ResetGesture();
    }

    /// <summary>The release half of a swipe; a no-op if Up Next no longer holds
    /// <paramref name="pressed"/> at <paramref name="index"/>. Internal for tests.</summary>
    internal void CommitSwipe(int index, Track? pressed, double dx, double rowWidth)
    {
        if (DataContext is ShellViewModel vm && StillAt(index, pressed) && QueueGesture.ShouldRemove(dx, rowWidth))
            vm.Player.RemoveFromQueue(index);
    }

    // ── Drag to reorder (handle) ───────────────────────────────────

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.FindAncestorOfType<ContentPresenter>() is not { } container) return;
        ResetGesture();
        _gesture = Gesture.Drag;
        _moving = container;
        _index = QueueList.IndexFromContainer(container);
        _track = TrackAt(_index);
        _start = e.GetPosition(this);
        container.ZIndex = 1;               // drawn above the rows it passes
        e.Pointer.Capture(handle);
        // The ScrollViewer's touch ScrollGestureRecognizer sees handled events too: once Up Next
        // outgrows the viewport it would take this vertical drag (capture and all) and scroll.
        e.PreventGestureRecognition();
        e.Handled = true;                   // not the start of a swipe on the row
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_gesture != Gesture.Drag || _moving == null) return;
        _moving.RenderTransform = new TranslateTransform(0, e.GetPosition(this).Y - _start.Y);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gesture != Gesture.Drag || _moving == null) return;
        var dy = e.GetPosition(this).Y - _start.Y;
        var rowHeight = _moving.Bounds.Height;
        var (from, track) = (_index, _track);
        ResetGesture();
        CommitDrag(from, track, dy, rowHeight);
        e.Handled = true;
    }

    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_gesture == Gesture.Drag) ResetGesture();
    }

    /// <summary>The release half of a handle drag; a no-op if Up Next no longer holds
    /// <paramref name="pressed"/> at <paramref name="from"/>. Internal for tests.</summary>
    internal void CommitDrag(int from, Track? pressed, double dy, double rowHeight)
    {
        if (DataContext is not ShellViewModel vm || !StillAt(from, pressed)) return;
        var to = QueueGesture.TargetIndex(from, dy, rowHeight, vm.Player.UpNext.Count);
        if (to != from) vm.Player.MoveInQueue(from, to);
    }

    // ── ✕ fallback ────────────────────────────────────────────────

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button || DataContext is not ShellViewModel vm) return;
        var index = IndexOf(button);
        if (index >= 0) vm.Player.RemoveFromQueue(index);
    }
}
