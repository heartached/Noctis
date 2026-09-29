using Avalonia.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Ensures only one context menu is visible at a time.
///
/// Tile/row views (Favorites, Home, Albums, Album detail) attach a separate
/// declarative <see cref="ContextMenu"/> to every item and let Avalonia open it
/// on right-click, relying on light-dismiss to close any menu that is already
/// open. Rapid successive right-clicks (or a right-click followed by an
/// options-button click) open a new menu before the previous one finishes
/// dismissing, leaving duplicate popups stacked on screen.
///
/// Each menu's <c>Opening</c> handler calls <see cref="NotifyOpening"/>, which
/// closes the previously opened menu before the new one is shown.
///
/// The reference is dropped when the menu closes: a closed menu needs no closing, and
/// held in this static it kept its owner tile — and through it the whole page and its
/// view-model (an album page long since left) — alive until some other menu opened.
/// </summary>
public static class ContextMenuCoordinator
{
    private static ContextMenu? _current;

    /// <summary>The menu being tracked (tests).</summary>
    internal static ContextMenu? Current => _current;

    public static void NotifyOpening(ContextMenu? menu)
    {
        if (menu == null)
            return;

        if (_current != null && !ReferenceEquals(_current, menu) && _current.IsOpen)
            _current.Close();

        _current = menu;
        menu.Closed -= OnMenuClosed;
        menu.Closed += OnMenuClosed;
    }

    private static void OnMenuClosed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnMenuClosed;
            if (ReferenceEquals(_current, menu))
                _current = null;
        }
    }
}
