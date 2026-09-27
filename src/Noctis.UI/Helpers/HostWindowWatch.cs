using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Tracks whether the window hosting a visual is minimized — the one signal every
/// animated control parks on. A minimized window keeps IsEffectivelyVisible true on
/// everything inside it, so frame loops that only gated on visibility kept ticking (and
/// re-rendering a window nobody could see) for as long as music played.
///
/// Follows the owner across attach/detach: on attach it finds the owner's Window and
/// listens to its WindowState; on detach it lets go. <c>minimizedChanged</c> runs on the
/// UI thread each time the host window goes into or comes out of Minimized. A TopLevel
/// that is not a Window (Android) never reports minimized.
/// </summary>
public sealed class HostWindowWatch
{
    private readonly Visual _owner;
    private readonly Action _minimizedChanged;
    private Window? _window;
    private bool _minimized;

    /// <param name="owner">The control whose host window is watched.</param>
    /// <param name="minimizedChanged">Called when <see cref="IsMinimized"/> flips.</param>
    public HostWindowWatch(Visual owner, Action minimizedChanged)
    {
        _owner = owner;
        _minimizedChanged = minimizedChanged;
        owner.AttachedToVisualTree += (_, _) => Hook();
        owner.DetachedFromVisualTree += (_, _) => Unhook();
        // A window watching itself (the mini player's backdrop), or an owner built into an
        // already-attached tree, has no attach event still to come.
        if (owner is TopLevel || owner.IsAttachedToVisualTree())
            Hook();
    }

    /// <summary>The window hosting the owner, while attached.</summary>
    public Window? Window => _window;

    /// <summary>True while the host window is minimized.</summary>
    public bool IsMinimized => _minimized;

    private void Hook()
    {
        Unhook();
        _window = TopLevel.GetTopLevel(_owner) as Window;
        if (_window == null) return;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _minimized = _window.WindowState == WindowState.Minimized;
    }

    private void Unhook()
    {
        if (_window != null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }
        _minimized = false;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        var minimized = _window?.WindowState == WindowState.Minimized;
        if (minimized == _minimized) return;
        _minimized = minimized;
        _minimizedChanged();
    }
}
