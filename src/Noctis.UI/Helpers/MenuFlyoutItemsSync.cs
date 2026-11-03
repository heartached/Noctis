using Avalonia;
using Avalonia.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Keeps a <see cref="MenuFlyoutPresenter"/> showing its <see cref="MenuFlyout"/>'s items when the
/// flyout was empty until it opened.
///
/// Avalonia's <see cref="ItemCollection"/> creates its backing list on the first Add. MenuFlyout
/// hands its Items to the presenter (<c>ItemsSource = Items</c>) when the popup is created, on the
/// first open and BEFORE <see cref="Avalonia.Controls.Primitives.FlyoutBase.Opening"/>, and the
/// presenter's own ItemCollection takes the source's current list (an ItemsSourceView source is
/// unwrapped to its Source). So a menu that starts empty and fills itself in Opening (Album
/// Roulette's Genre and Decade chips) has its rows added to a new list the presenter never sees:
/// an empty card, every time it opens. Re-assigning ItemsSource when the presenter is attached
/// (after Opening) makes it take the flyout's real list.
///
/// Set by a style: <c>&lt;Setter Property="(helpers:MenuFlyoutItemsSync.Enable)" Value="True" /&gt;</c>.
/// </summary>
public static class MenuFlyoutItemsSync
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<MenuFlyoutPresenter, bool>("Enable", typeof(MenuFlyoutItemsSync));

    public static void SetEnable(MenuFlyoutPresenter presenter, bool value) => presenter.SetValue(EnableProperty, value);
    public static bool GetEnable(MenuFlyoutPresenter presenter) => presenter.GetValue(EnableProperty);

    static MenuFlyoutItemsSync()
    {
        EnableProperty.Changed.AddClassHandler<MenuFlyoutPresenter>((presenter, args) =>
        {
            presenter.AttachedToVisualTree -= OnAttached;
            if (!args.GetNewValue<bool>()) return;
            presenter.AttachedToVisualTree += OnAttached;
            // The setter is often applied after the presenter attached.
            if (TopLevel.GetTopLevel(presenter) is not null) Sync(presenter);
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is MenuFlyoutPresenter presenter) Sync(presenter);
    }

    /// <summary>Points the presenter at the flyout's live list when it lost track of it.</summary>
    internal static void Sync(MenuFlyoutPresenter presenter)
    {
        if (presenter.ItemsSource is not ItemCollection source || presenter.Items.Count == source.Count)
            return;
        presenter.ItemsSource = null;
        presenter.ItemsSource = source;
    }
}
