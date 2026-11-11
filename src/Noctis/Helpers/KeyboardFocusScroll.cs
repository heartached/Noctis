using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctis.Helpers;

/// <summary>
/// A page ScrollViewer that follows focus only when the keyboard moved it (GitHub #223).
/// Avalonia's ScrollViewer brings every newly focused descendant into view
/// (BringIntoViewOnFocusChange, on by default), including focus a window restores when it is
/// activated again. On the artist page the clicked tab button keeps focus while the page is
/// scrolled with the wheel and a tile is right-clicked; closing the modal Edit Info (or Find
/// Lyrics) window handed focus back to that button, and the page scrolled up to show it.
/// Tab and arrow-key focus still scroll the focused control into view, as before.
/// </summary>
public static class KeyboardFocusScroll
{
    public static void Apply(ScrollViewer viewer)
    {
        viewer.BringIntoViewOnFocusChange = false;
        viewer.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional && e.Source is Control focused)
            focused.BringIntoView();
    }
}
