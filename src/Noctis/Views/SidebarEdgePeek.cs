using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Views;

/// <summary>
/// GitHub #171: the sidebar hides while the lyrics page is up in fullscreen
/// (<c>MainWindow.UpdateImmersiveLyricsState</c>), and with it hidden there was nothing left
/// to bring it back — the hover handler skips a hidden sidebar and the rail itself is
/// collapsed, so the mouse at the left edge found nothing. A thin edge strip stands in while
/// it is hidden: touching it slides the rail in over the page (no reflow, the page stays
/// full width), and leaving both strip and rail tucks it away again. While it peeks it
/// follows the same settings as on other pages: pinned open, or expanding on hover.
/// </summary>
internal sealed class SidebarEdgePeek
{
    public const double RailWidth = 60;
    public const double ExpandedWidth = 220;

    private readonly Border _edge;
    private readonly Border _rail;
    private readonly Func<bool> _isHidden;
    private readonly Func<bool> _hoverExpand;
    private readonly Func<bool> _pinned;
    private readonly Action<bool> _setExpanded;
    private bool _closeQueued;

    public SidebarEdgePeek(Border edge, Border rail, Func<bool> isHidden, Func<bool> hoverExpand,
        Func<bool> pinned, Action<bool> setExpanded)
    {
        _edge = edge;
        _rail = rail;
        _isHidden = isHidden;
        _hoverExpand = hoverExpand;
        _pinned = pinned;
        _setExpanded = setExpanded;
        _edge.PropertyChanged += OnPointerOverChanged;
        _rail.PropertyChanged += OnPointerOverChanged;
    }

    /// <summary>Whether the hidden rail is currently slid in.</summary>
    public bool IsPeeking { get; private set; }

    /// <summary>The shell's hidden state changed: the edge strip exists only while hidden,
    /// and any peek ends (the caller lays the rail out for the new state).</summary>
    public void SyncHidden()
    {
        IsPeeking = false;
        _edge.IsVisible = _isHidden();
    }

    private void OnPointerOverChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != InputElement.IsPointerOverProperty || !_isHidden()) return;
        if (_edge.IsPointerOver || _rail.IsPointerOver)
        {
            IsPeeking = true;
            Layout();
        }
        else if (IsPeeking && !_closeQueued)
        {
            // Crossing from the strip onto the rail clears one before setting the other.
            _closeQueued = true;
            Dispatcher.UIThread.Post(CloseIfLeft, DispatcherPriority.Background);
        }
    }

    private void CloseIfLeft()
    {
        _closeQueued = false;
        if (!IsPeeking || !_isHidden() || _edge.IsPointerOver || _rail.IsPointerOver) return;
        // A playlist's context menu takes the pointer off the rail: stay until it closes.
        var menu = _rail.GetVisualDescendants().OfType<Control>()
            .Select(c => c.ContextMenu).FirstOrDefault(m => m is { IsOpen: true });
        if (menu != null)
        {
            void Reclose(object? s, Avalonia.Interactivity.RoutedEventArgs a)
            {
                menu.Closed -= Reclose;
                CloseIfLeft();
            }
            menu.Closed += Reclose;
            return;
        }
        IsPeeking = false;
        Layout();
    }

    private void Layout()
    {
        if (!IsPeeking)
        {
            _rail.Width = 0;
            _rail.IsVisible = false;
            _setExpanded(false);
            return;
        }
        var expanded = _pinned() || (_rail.IsPointerOver && _hoverExpand());
        _rail.IsVisible = true;
        _rail.Width = expanded ? ExpandedWidth : RailWidth;
        _setExpanded(expanded);
    }
}
