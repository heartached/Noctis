using Avalonia;
using Avalonia.Controls;
using RangeBase = Avalonia.Controls.Primitives.RangeBase;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// Swipe sideways to change the song, as Apple Music and Spotify do: on the mini player's row and
/// on the Now Playing cover (the record and CD styles too). The content follows the finger and the
/// neighbouring song (its cover, and on the mini player its title and artist) slides in beside it;
/// let go past <see cref="TrackSwipe.CommitFraction"/> of the width or with a flick and the
/// content carries on out while the neighbour settles in, then the song changes; otherwise both
/// spring back. Towards a song that is not there (the end of the queue with Repeat off, nothing
/// played before) the content only gives a little, as a rubber band, and nothing happens.
///
/// It takes the press only once it has moved <see cref="TrackSwipe.SlopDip"/> sideways before it
/// moved that far up or down: a downward drag stays the shell's swipe-down to close (same slop, decided on the
/// same move), a tap stays the row's tap, and a press on a slider is left to the slider. Once it
/// is a swipe the pointer is captured, so the button under the finger lets go without clicking.
/// No haptic tick: the app has no haptics path yet.
/// </summary>
internal sealed class TrackSwipeGesture
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(260);

    private readonly Control _surface;
    private readonly Control _content;
    private readonly Control _ghost;
    private readonly Control _ghostData;
    private readonly Visual _frame;
    private readonly double _gap;
    private readonly Func<NowPlayingViewModel?> _player;
    private readonly Func<bool> _allowed;

    private bool _tracking;
    private SwipeAxis _axis;
    private Point _start;
    private double _dx, _lastX, _velocity;
    private ulong _lastTime;
    private Track? _next, _previous;

    /// <param name="surface">Takes the presses (and the capture).</param>
    /// <param name="content">Slides with the finger.</param>
    /// <param name="ghost">The neighbour's look, slid in beside the content.</param>
    /// <param name="ghostData">Gets the neighbour Track as its DataContext.</param>
    /// <param name="frame">An unscaled ancestor whose coordinates the finger is measured in.</param>
    /// <param name="gap">Space between the content and the ghost.</param>
    public TrackSwipeGesture(Control surface, Control content, Control ghost, Control ghostData, Visual frame,
        double gap, Func<NowPlayingViewModel?> player, Func<bool>? allowed = null)
    {
        _surface = surface;
        _content = content;
        _ghost = ghost;
        _ghostData = ghostData;
        _frame = frame;
        _gap = gap;
        _player = player;
        _allowed = allowed ?? (() => true);
        // Not the page's view model: its bindings are typed for a Track.
        ghostData.DataContext = null;
        surface.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        surface.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>A swipe is under the finger now (internal for tests).</summary>
    internal bool IsSwiping => _tracking && _axis == SwipeAxis.Horizontal;

    /// <summary>How far the content travels to leave: its width and the gap.</summary>
    private double Distance => Math.Max(1, _surface.Bounds.Width) + _gap;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _tracking = false;
        if (_player() is not { CurrentTrack: not null } player || !_allowed() || Motion.IsRunning(_content)) return;
        if ((e.Source as Visual)?.FindAncestorOfType<RangeBase>(includeSelf: true) != null) return;
        _tracking = true;
        _axis = SwipeAxis.Undecided;
        _start = e.GetPosition(_frame);
        _lastX = _start.X;
        _lastTime = e.Timestamp;
        _velocity = 0;
        _dx = 0;
        _next = player.PeekNext();
        _previous = player.PeekPrevious();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_tracking) return;
        var p = e.GetPosition(_frame);
        var dx = p.X - _start.X;
        if (_axis == SwipeAxis.Undecided)
        {
            _axis = TrackSwipe.Lock(dx, p.Y - _start.Y);
            if (_axis == SwipeAxis.Vertical) { _tracking = false; return; }
            if (_axis == SwipeAxis.Undecided) return;
            e.Pointer.Capture(_surface);   // the button under the finger lets go and does not click
            Motion.Stop(_content);
            Motion.Stop(_ghost);
        }
        if (e.Timestamp > _lastTime)
        {
            _velocity = (p.X - _lastX) / (e.Timestamp - _lastTime);
            _lastX = p.X;
            _lastTime = e.Timestamp;
        }
        _dx = dx;
        Follow(dx);
        e.Handled = true;
    }

    /// <summary>Direct sets per move, no transitions (they would stall under the finger).</summary>
    private void Follow(double dx)
    {
        var offset = TrackSwipe.Offset(dx, _next != null, _previous != null);
        _content.RenderTransform = Motion.TranslateX(offset);
        var neighbour = TrackSwipe.Direction(dx) switch
        {
            SwipeOutcome.Next => _next,
            SwipeOutcome.Previous => _previous,
            _ => null,
        };
        if (neighbour == null)
        {
            _ghost.IsVisible = false;
            return;
        }
        if (!ReferenceEquals(_ghostData.DataContext, neighbour)) _ghostData.DataContext = neighbour;
        _ghost.RenderTransform = Motion.TranslateX(offset + (dx < 0 ? Distance : -Distance));
        _ghost.IsVisible = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsSwiping)
        {
            _tracking = false;
            return;
        }
        _tracking = false;
        var dx = e.GetPosition(_frame).X - _start.X;
        var outcome = TrackSwipe.Decide(dx, _velocity, Math.Max(1, _surface.Bounds.Width), _next != null, _previous != null);
        e.Pointer.Capture(null);
        e.Handled = true;
        Settle(outcome);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Taken away mid-swipe (the system shade, another gesture): back where it was.
        if (!IsSwiping) return;
        _tracking = false;
        Settle(SwipeOutcome.None);
    }

    private void Settle(SwipeOutcome outcome)
    {
        var player = _player();
        if (outcome == SwipeOutcome.None || player == null)
        {
            var back = _dx < 0 ? Distance : -Distance;
            Motion.To(_content, SettleTime, Motion.TranslateX(0), done: () => _content.RenderTransform = null);
            if (_ghost.IsVisible)
                Motion.To(_ghost, SettleTime, Motion.TranslateX(back), done: HideGhost);
            return;
        }
        var away = outcome == SwipeOutcome.Next ? -Distance : Distance;
        Motion.To(_ghost, SettleTime, Motion.TranslateX(0));
        Motion.To(_content, SettleTime, Motion.TranslateX(away), done: () =>
        {
            // The song changes once the neighbour has landed where the content was: the content
            // comes back in place showing it, and the ghost goes.
            if (outcome == SwipeOutcome.Next) player.NextCommand.Execute(null);
            else player.SkipToPrevious();
            _content.RenderTransform = null;
            Motion.Stop(_ghost);
            HideGhost();
        });
    }

    private void HideGhost()
    {
        _ghost.IsVisible = false;
        _ghost.RenderTransform = null;
        _ghostData.DataContext = null;
    }
}
