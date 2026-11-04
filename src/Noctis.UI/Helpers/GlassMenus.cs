using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Liquid Glass for every menu, submenu and tooltip of a glass window (GitHub #104 island
/// menus, extended app-wide by #169).
///
/// A native popup window cannot be frosted: the <see cref="GlassPanel"/> blur only sees the
/// surface it renders into. So while <see cref="AppGlass"/> is on, each open of a right-click
/// <see cref="ContextMenu"/>, a <see cref="MenuFlyoutPresenter"/> or a <see cref="ToolTip"/>
/// whose owner sits in a <see cref="HostProperty"/> window is hosted in that window's overlay
/// layer and tagged with <see cref="GlassClass"/>. Styles.axaml then gives the card a
/// GlassPanel chrome (and its submenus, through <see cref="SubmenuGlassProperty"/>). While
/// glass is off neither happens, so every menu opens exactly as before.
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

    /// <summary>Blur of every glass menu card (the island menus' value since #104).</summary>
    public const double BlurRadius = 14;

    /// <summary>A window whose menus and tooltips frost while Liquid Glass is on (the main
    /// window). Others (mini player, dialogs) keep native popups: an overlay-layer menu can't
    /// leave its window, which would cramp menus in a small window.</summary>
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
            if (!e.GetNewValue<bool>()) return;
            card.AttachedToLogicalTree += OnCardAttached;
            Apply(card);
        });

        MenuBase.IsOpenProperty.Changed.AddClassHandler<ContextMenu>((menu, e) =>
        {
            if (e.GetNewValue<bool>() && GetTrack(menu)) Apply(menu);
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

    /// <summary>The popup frosts: glass is on and its owner sits in a host window.</summary>
    internal static bool ShouldFrost(Popup popup) =>
        AppGlass.IsActive
        && popup.Parent is Visual owner
        && TopLevel.GetTopLevel(owner) is Window window
        && GetHost(window);

    /// <summary>Sets up one open of <paramref name="card"/> (a ContextMenu, MenuFlyoutPresenter
    /// or ToolTip): overlay layer + <see cref="GlassClass"/> while it should frost, the stock
    /// native popup and plain card otherwise. Cards not hosted by a Popup (the tray menu's own
    /// window, see TrayMenuFrost) are left alone.</summary>
    public static void Apply(Control card)
    {
        if (card.Parent is not Popup popup) return;
        var on = ShouldFrost(popup);
        card.Classes.Set(GlassClass, on);

        // A tooltip's popup binds its overlay flag to its owner's ToolTip.ShouldUseOverlayLayer
        // on every open (ToolTip.Open), so the owner carries it.
        var isTip = card is ToolTip;
        Control? holder = isTip ? popup.Parent as Control : popup;
        if (holder is null) return;
        var property = isTip ? ToolTip.ShouldUseOverlayLayerProperty : Popup.ShouldUseOverlayLayerProperty;
        if (on)
        {
            holder.SetValue(property, true);
            holder.SetValue(ForcedOverlayProperty, true);
        }
        else if (holder.GetValue(ForcedOverlayProperty))
        {
            holder.ClearValue(property);
            holder.ClearValue(ForcedOverlayProperty);
        }
    }

    private static void FrostSubmenu(Border card)
    {
        if (card.Child is GlassPanel || card.Child is not Control rows) return;
        var glass = new GlassPanel { BlurRadius = BlurRadius, Padding = MenuChrome.GetPadding(card) ?? card.Padding };
        glass.Bind(GlassPanel.BackgroundProperty, card.GetResourceObservable("AppSidebarBackground"));
        glass.Bind(GlassPanel.GlassTintOpacityProperty, card.GetResourceObservable("GlassMenuTintOpacity", v => v as double?));
        glass.Bind(GlassPanel.CornerRadiusProperty, card.GetObservable(Border.CornerRadiusProperty));
        // The open/close motion fades the card's Fade (UseFade, set by the same style): the
        // frost and the rows follow it, as on the menu cards.
        glass.Bind(GlassPanel.FadeProperty, card.GetObservable(MenuOpenAnimation.FadeProperty));
        rows.Bind(Visual.OpacityProperty, card.GetObservable(MenuOpenAnimation.FadeProperty));
        // The glass paints the card now: the Border keeps only its size and motion.
        card.Child = null;
        card.Background = null;
        card.BorderThickness = default;
        card.Padding = default;
        glass.Child = rows;
        card.Child = glass;
    }

    private static void UnfrostSubmenu(Border card)
    {
        if (card.Child is not GlassPanel { Child: Control rows } glass) return;
        card.Child = null;
        glass.Child = null;
        rows.ClearValue(Visual.OpacityProperty);
        card.Child = rows;
        if (MenuChrome.GetPadding(card) is { } padding) card.Padding = padding;
        else card.ClearValue(Decorator.PaddingProperty);
        card.ClearValue(Border.BackgroundProperty);
        card.ClearValue(Border.BorderThicknessProperty);
        MenuChrome.ApplySubmenuCard(card, MenuChrome.GetSubmenuCard(card));
    }
}
