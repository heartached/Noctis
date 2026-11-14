using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// A long submenu (Link Tracks From ▸, or any submenu taller than the window) scrolls like a
/// list in the app, not like the Fluent menu (owner 11-14: "remove the arrows on top and
/// bottom, theres also a bug that it autoscrolls sometimes", and the wheel should glide like
/// the Albums page). Set by the <c>MenuItem /template/ Popup ScrollViewer</c> style:
/// <list type="bullet">
/// <item>The Fluent MenuItem template gives its ScrollViewer the FluentMenuScrollViewer theme: a
/// RepeatButton arrow strip above and below the rows, each shown only while there is more that
/// way, so the viewport grows and shrinks by a strip as the list moves. The plain ScrollViewer
/// theme replaces it (a local value: the template's Theme outranks a style setter), which
/// brings the app's thin pill scrollbar, the one the Add to Playlist list and ComboBox lists show.</item>
/// <item>Hovering a row focuses it (the menu's pointer handling), and a focused row asks to be
/// brought into view: resting on the half row at the bottom edge scrolled the list up, and the
/// next row then under the pointer did the same. Only a row reached with the keyboard is
/// brought into view now; the wheel (SmoothScrollBehavior, same Step and SettleMs as the
/// Albums page) and the scrollbar still scroll it.</item>
/// </list>
/// </summary>
public static class SubmenuScroller
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SubmenuScroller));

    public static void SetIsEnabled(ScrollViewer sv, bool value) => sv.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(ScrollViewer sv) => sv.GetValue(IsEnabledProperty);

    /// <summary>True from a key press inside the scroller until the pointer next moves in it.</summary>
    private static readonly AttachedProperty<bool> KeyboardProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Keyboard", typeof(SubmenuScroller));

    static SubmenuScroller()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((sv, e) =>
        {
            sv.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            sv.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            sv.AttachedToLogicalTree -= OnAttached;
            if (!e.GetNewValue<bool>())
            {
                sv.ClearValue(StyledElement.ThemeProperty);
                return;
            }
            sv.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            sv.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            sv.AttachedToLogicalTree += OnAttached;
            ApplyTheme(sv);
        });

        // The row is the request's source, so this runs before the ScrollViewer's presenter
        // (further up the route) would scroll to it.
        Control.RequestBringIntoViewEvent.AddClassHandler<MenuItem>((row, e) =>
        {
            if (!ReferenceEquals(e.Source, row)) return;
            var sv = row.FindAncestorOfType<ScrollViewer>();
            if (sv != null && GetIsEnabled(sv) && !sv.GetValue(KeyboardProperty))
                e.Handled = true;
        });
    }

    private static void OnAttached(object? sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        if (sender is ScrollViewer sv) ApplyTheme(sv);
    }

    /// <summary>The app's plain ScrollViewer theme, as a local value.</summary>
    private static void ApplyTheme(ScrollViewer sv)
    {
        if (sv.TryFindResource(typeof(ScrollViewer), sv.ActualThemeVariant, out var theme) && theme is ControlTheme plain
            && !ReferenceEquals(sv.Theme, plain))
            sv.Theme = plain;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is ScrollViewer sv) sv.SetValue(KeyboardProperty, true);
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is ScrollViewer sv) sv.SetValue(KeyboardProperty, false);
    }
}
