using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Liquid Glass for every menu, submenu, drop-down list and tooltip of every window (GitHub
/// #104 island menus, extended to the main window by #169 and then to the whole app).
///
/// While <see cref="AppGlass"/> is on, each open of a right-click <see cref="ContextMenu"/>, a
/// <see cref="MenuFlyoutPresenter"/>, a tracked <see cref="FlyoutPresenter"/> or a
/// <see cref="ToolTip"/> is tagged with <see cref="GlassClass"/>, and Styles.axaml gives the card
/// a GlassPanel chrome (its submenus, and the card Border of a drop-down list or a custom popup,
/// through <see cref="SubmenuGlassProperty"/> / <see cref="ApplyCardPopup"/>). While glass is
/// off none of it happens, so every menu opens exactly as before.
///
/// Where the card opens: the <see cref="GlassPanel"/> blur only sees the surface it renders
/// into. In a <see cref="HostProperty"/> window (the main window) the card is hosted in the
/// window's overlay layer (and tagged <see cref="OverlayClass"/>), so the glass frosts the live
/// app. Every other window (mini player, dialogs, plugin windows) keeps the native popup, which
/// may reach past a small window, and the card's <see cref="GlassMenuBackdrop"/> paints a
/// snapshot of the window behind it into the popup for the glass to frost.
///
/// Decided per open, before the popup opens, so a Liquid Glass switch applies to the next
/// open: the global menu style sets <see cref="TrackProperty"/>; flyouts and tooltips detach
/// their card from the logical tree on every close and re-attach it at the next open
/// (Avalonia 12.1.3 PopupFlyoutBase.ShowAtCore / ToolTip.Open call SetPopupParent first). A
/// ContextMenu set on a control keeps its popup parented between opens (ContextMenu.PopupClosed
/// only unparents it when no control holds the menu), so it is decided when its IsOpen turns
/// true, which ContextMenu.Open sets just before it opens the popup. Cost per open: a class toggle and one property write. The frost itself costs only
/// while the card is on screen (GlassPanel keeps one backdrop copy the size of the card and
/// drops it when the card closes) — the same cost the island menus have had since #104.
/// </summary>
public static class GlassMenus
{
    /// <summary>Card class while the menu or tooltip is frosted.</summary>
    public const string GlassClass = "glass-menu";

    /// <summary>Card class while a frosted card is hosted in its window's overlay layer (its
    /// submenus then open there too).</summary>
    public const string OverlayClass = "glass-overlay";

    /// <summary>Blur of every glass menu card (the island menus' value since #104).</summary>
    public const double BlurRadius = 14;

    /// <summary>A window whose menus and tooltips frost in its overlay layer (the main window).
    /// Others (mini player, dialogs) keep native popups, frosted over a snapshot of the window
    /// (<see cref="GlassMenuBackdrop"/>): an overlay-layer menu can't leave its window, which
    /// would cramp or clip menus in a small window.</summary>
    public static readonly AttachedProperty<bool> HostProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Host", typeof(GlassMenus));

    public static void SetHost(Window window, bool value) => window.SetValue(HostProperty, value);
    public static bool GetHost(Window window) => window.GetValue(HostProperty);

    /// <summary>Set by the global menu/tooltip style: decide the glass on every open.</summary>
    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Track", typeof(GlassMenus));

    public static void SetTrack(Control control, bool value) => control.SetValue(TrackProperty, value);
    public static bool GetTrack(Control control) => control.GetValue(TrackProperty);

    /// <summary>Set (by the glass style) on a submenu card, the Border inside MenuItem's popup:
    /// its rows move into a GlassPanel and the Border's own fill, edge and padding step aside.
    /// The Fluent MenuItem template offers no other way in: the card is a plain Border.</summary>
    public static readonly AttachedProperty<bool> SubmenuGlassProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("SubmenuGlass", typeof(GlassMenus));

    public static void SetSubmenuGlass(Border border, bool value) => border.SetValue(SubmenuGlassProperty, value);
    public static bool GetSubmenuGlass(Border border) => border.GetValue(SubmenuGlassProperty);

    /// <summary>Marks a popup / tooltip owner whose overlay flag this class set, so turning
    /// glass off clears only its own value.</summary>
    private static readonly AttachedProperty<bool> ForcedOverlayProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("ForcedOverlay", typeof(GlassMenus));

    static GlassMenus()
    {
        TrackProperty.Changed.AddClassHandler<Control>((card, e) =>
        {
            card.AttachedToLogicalTree -= OnCardAttached;
            card.AttachedToVisualTree -= OnCardShown;
            if (!e.GetNewValue<bool>()) return;
            card.AttachedToLogicalTree += OnCardAttached;
            card.AttachedToVisualTree += OnCardShown;
            Apply(card);
        });

        MenuBase.IsOpenProperty.Changed.AddClassHandler<ContextMenu>((menu, e) =>
        {
            if (e.GetNewValue<bool>() && GetTrack(menu)) Apply(menu);
        });

        // Drop-down lists: decided before the template binding opens the popup (a class
        // handler runs before binding subscribers; ComboBoxDropDownAnimator calls it too, ahead
        // of its open motion).
        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((box, e) =>
        {
            if (e.GetNewValue<bool>() && DropDownPopup(box) is { } popup) ApplyCardPopup(popup);
        });
        AutoCompleteBox.IsDropDownOpenProperty.Changed.AddClassHandler<AutoCompleteBox>((box, e) =>
        {
            if (e.GetNewValue<bool>() && DropDownPopup(box) is { } popup) ApplyCardPopup(popup);
        });

        SubmenuGlassProperty.Changed.AddClassHandler<Border>((card, e) =>
        {
            if (e.GetNewValue<bool>()) FrostSubmenu(card);
            else UnfrostSubmenu(card);
        });
    }

    private static void OnCardAttached(object? sender, LogicalTreeAttachmentEventArgs e)
    {
        if (sender is Control card) Apply(card);
    }

    /// <summary>The card is on screen. A card that stayed in the logical tree since it was decided
    /// (a flyout whose owner moved to another window while it was closed) is re-checked here: the
    /// glass follows the setting now. Only the class: the popup is already open, so its overlay
    /// choice waits for the next open.</summary>
    private static void OnCardShown(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control card || card.Parent is not Popup popup) return;
        var on = ShouldFrost(popup);
        if (card.Classes.Contains(GlassClass) == on) return;
        card.Classes.Set(GlassClass, on);
        if (!on) card.Classes.Remove(OverlayClass);
    }

    /// <summary>The window <paramref name="popup"/> opens for: its own visual tree's, else its
    /// owner's (a ContextMenu's popup is not in the tree), through any popups it sits in.</summary>
    internal static Window? OwnerWindow(Popup popup) => GlassMenuBackdrop.OwnerWindow(OpenerTopLevel(popup));

    /// <summary>The popup frosts: glass is on and it opens for a window.</summary>
    internal static bool ShouldFrost(Popup popup) => AppGlass.IsActive && OwnerWindow(popup) is not null;

    /// <summary>The surface <paramref name="popup"/> opens from: the top level its opener sits in
    /// (a native popup's PopupRoot when the opener is inside a flyout, menu or tooltip).</summary>
    internal static TopLevel? OpenerTopLevel(Popup popup) =>
        TopLevel.GetTopLevel(popup)
        ?? (popup.Parent is Visual owner ? TopLevel.GetTopLevel(owner) : null)
        ?? (popup.PlacementTarget is { } target ? TopLevel.GetTopLevel(target) : null);

    /// <summary>The frosted popup opens in its window's overlay layer: a host window's, and only
    /// when it opens from the window itself. Opened from inside a native popup (a tooltip or list
    /// in a plain Flyout) it keeps its own popup: the window's overlay layer lies under that popup
    /// window, and a PopupRoot has no overlay layer at all (Avalonia 12.1.3
    /// OverlayPopupHost.CreatePopupHost then throws "no overlay layer is found").</summary>
    internal static bool ShouldUseOverlay(Popup popup) =>
        AppGlass.IsActive && OpenerTopLevel(popup) is Window window && GetHost(window);

    /// <summary>Sets up one open of <paramref name="card"/> (a ContextMenu, MenuFlyoutPresenter,
    /// FlyoutPresenter or ToolTip): <see cref="GlassClass"/> while it should frost, plus the
    /// overlay layer in a host window; the stock popup and plain card otherwise. Cards not hosted
    /// by a Popup (the tray menu's own window, see TrayMenuFrost) are left alone.</summary>
    public static void Apply(Control card)
    {
        if (card.Parent is not Popup popup) return;
        var on = ShouldFrost(popup);
        var overlay = on && ShouldUseOverlay(popup);
        card.Classes.Set(GlassClass, on);
        card.Classes.Set(OverlayClass, overlay);

        // A tooltip's popup binds its overlay flag to its owner's ToolTip.ShouldUseOverlayLayer
        // on every open (ToolTip.Open), so the owner carries it.
        var isTip = card is ToolTip;
        Control? holder = isTip ? popup.Parent as Control : popup;
        if (holder is null) return;
        var property = isTip ? ToolTip.ShouldUseOverlayLayerProperty : Popup.ShouldUseOverlayLayerProperty;
        SetForcedOverlay(holder, property, overlay);
    }

    /// <summary>Sets <paramref name="property"/> on <paramref name="holder"/> while
    /// <paramref name="on"/>, and clears it again only where this class set it (a popup that asks
    /// for the overlay layer itself keeps it).</summary>
    private static void SetForcedOverlay(Control holder, StyledProperty<bool> property, bool on)
    {
        if (on)
        {
            if (holder.IsSet(property) && !holder.GetValue(ForcedOverlayProperty)) return;
            holder.SetValue(property, true);
            holder.SetValue(ForcedOverlayProperty, true);
        }
        else if (holder.GetValue(ForcedOverlayProperty))
        {
            holder.ClearValue(property);
            holder.ClearValue(ForcedOverlayProperty);
        }
    }

    /// <summary>The ComboBox / AutoCompleteBox list popup (the Fluent template's PART_Popup).</summary>
    internal static Popup? DropDownPopup(TemplatedControl box)
    {
        foreach (var v in box.GetVisualDescendants())
            if (v is Popup { Name: "PART_Popup" } popup) return popup;
        return null;
    }

    /// <summary>
    /// Sets up one open of a popup whose child is a plain card <see cref="Border"/>: a ComboBox or
    /// AutoCompleteBox list, or a custom menu popup (the mini player's "…" menu). Call it before
    /// the popup opens. While glass is on the card frosts like a submenu card
    /// (<see cref="SubmenuGlassProperty"/>; its open/close motion then fades
    /// <see cref="MenuOpenAnimation.FadeProperty"/>, see <see cref="MenuOpenAnimation.UseFadeProperty"/>)
    /// and, in a host window, opens in the overlay layer. Off, the card and popup are left as
    /// the template made them.
    /// </summary>
    public static void ApplyCardPopup(Popup popup)
    {
        if (popup.Child is not Border card) return;
        var on = ShouldFrost(popup);
        var overlay = on && ShouldUseOverlay(popup);
        card.Classes.Set(GlassClass, on);
        card.Classes.Set(OverlayClass, overlay);
        SetForcedOverlay(popup, Popup.ShouldUseOverlayLayerProperty, overlay);
        if (on)
        {
            MenuOpenAnimation.SetUseFade(card, true);
            SetSubmenuGlass(card, true);
        }
        else if (GetSubmenuGlass(card))
        {
            card.ClearValue(SubmenuGlassProperty);
            card.ClearValue(MenuOpenAnimation.UseFadeProperty);
        }
    }

    /// <summary>The GlassPanel a frosted card Border (<see cref="SubmenuGlassProperty"/>) hands
    /// its rows to, or null.</summary>
    internal static GlassPanel? FrostOf(Border card) =>
        card.Child is GlassMenuBackdrop { Child: GlassPanel glass } ? glass : null;

    private static void FrostSubmenu(Border card)
    {
        if (FrostOf(card) != null || card.Child is not Control rows) return;
        var glass = new GlassPanel { BlurRadius = BlurRadius, Padding = MenuChrome.GetPadding(card) ?? card.Padding };
        glass.Bind(GlassPanel.BackgroundProperty, card.GetResourceObservable("AppSidebarBackground"));
        glass.Bind(GlassPanel.GlassTintOpacityProperty, card.GetResourceObservable("GlassMenuTintOpacity", v => v as double?));
        glass.Bind(GlassPanel.CornerRadiusProperty, card.GetObservable(Border.CornerRadiusProperty));
        // The open/close motion fades the card's Fade (UseFade, set by the same style): the
        // frost and the rows follow it, as on the menu cards.
        glass.Bind(GlassPanel.FadeProperty, card.GetObservable(MenuOpenAnimation.FadeProperty));
        rows.Bind(Visual.OpacityProperty, card.GetObservable(MenuOpenAnimation.FadeProperty));
        // In a native popup window the backdrop paints the window behind for the glass to frost.
        var backdrop = new GlassMenuBackdrop { Child = glass };
        backdrop.Bind(GlassMenuBackdrop.BackgroundProperty, card.GetResourceObservable("AppSidebarBackground"));
        backdrop.Bind(GlassMenuBackdrop.CornerRadiusProperty, card.GetObservable(Border.CornerRadiusProperty));
        backdrop.Bind(GlassMenuBackdrop.FadeProperty, card.GetObservable(MenuOpenAnimation.FadeProperty));
        // The glass paints the card now: the Border keeps only its size and motion. Its own
        // fill, edge and padding are masked above every other value (not overwritten): a card
        // whose fill is its own local value (the mini player's "…" menu) gets it back unfrosted.
        card.Child = null;
        Masks.AddOrUpdate(card, new[]
        {
            card.SetValue(Border.BackgroundProperty, null, BindingPriority.Animation),
            card.SetValue(Border.BorderThicknessProperty, default, BindingPriority.Animation),
            card.SetValue(Decorator.PaddingProperty, default, BindingPriority.Animation),
        });
        glass.Child = rows;
        card.Child = backdrop;
    }

    /// <summary>The masks <see cref="FrostSubmenu"/> laid over a card's fill, edge and padding.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, IDisposable?[]> Masks = new();

    private static void UnfrostSubmenu(Border card)
    {
        if (card.Child is not GlassMenuBackdrop { Child: GlassPanel { Child: Control rows } glass } backdrop) return;
        card.Child = null;
        backdrop.Child = null;
        glass.Child = null;
        rows.ClearValue(Visual.OpacityProperty);
        card.Child = rows;
        if (Masks.TryGetValue(card, out var masks))
        {
            Masks.Remove(card);
            foreach (var mask in masks) mask?.Dispose();
        }
        // What MenuChrome skipped while the glass painted the card.
        if (MenuChrome.GetPadding(card) is { } padding) card.Padding = padding;
        if (MenuChrome.GetSubmenuCard(card)) MenuChrome.ApplySubmenuCard(card, true);
    }
}
