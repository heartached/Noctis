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
/// </summary>
public static class ContextMenuCoordinator
{
    private static ContextMenu? _current;

    public static void NotifyOpening(ContextMenu? menu)
    {
        if (menu == null)
            return;

        if (_current != null && !ReferenceEquals(_current, menu) && _current.IsOpen)
            _current.Close();

        _current = menu;
    }
}
