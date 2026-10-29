using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>
/// Follows one press (moves and the release) on the whole window, whatever is under the pointer
/// and whoever holds its capture. A gesture that lives only on its control's pointer capture
/// ends silently when the platform takes the capture away (Windows: another window grabbing the
/// mouse sends WM_CAPTURECHANGED, Avalonia then drops the capture); the window still gets the
/// moves and the release, so the gesture carries on from here.
/// </summary>
internal sealed class PointerTracker
{
    private TopLevel? _top;
    private Action<PointerEventArgs>? _moved;
    private Action<PointerReleasedEventArgs>? _released;
    private readonly EventHandler<PointerEventArgs> _onMoved;
    private readonly EventHandler<PointerReleasedEventArgs> _onReleased;

    public PointerTracker()
    {
        _onMoved = (_, e) => _moved?.Invoke(e);
        _onReleased = (_, e) => _released?.Invoke(e);
    }

    public bool IsActive => _top is not null;

    /// <summary>Starts following on <paramref name="owner"/>'s window (no window: nothing to follow).</summary>
    public void Start(Visual owner, Action<PointerEventArgs> moved, Action<PointerReleasedEventArgs> released)
    {
        Stop();
        if (TopLevel.GetTopLevel(owner) is not { } top) return;
        _top = top;
        _moved = moved;
        _released = released;
        // Tunnel and handled ones too: seen first, even when the control under the pointer handles them.
        top.AddHandler(InputElement.PointerMovedEvent, _onMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(InputElement.PointerReleasedEvent, _onReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void Stop()
    {
        if (_top is { } top)
        {
            top.RemoveHandler(InputElement.PointerMovedEvent, _onMoved);
            top.RemoveHandler(InputElement.PointerReleasedEvent, _onReleased);
        }
        _top = null;
        _moved = null;
        _released = null;
    }
}
