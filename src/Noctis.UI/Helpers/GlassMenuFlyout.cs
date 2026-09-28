using System;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Liquid Glass for a <see cref="MenuFlyout"/> (GitHub #104: the island's menus).
///
/// A native popup window cannot be frosted: the <c>GlassPanel</c> blur only sees the surface
/// it renders into. So while <see cref="AppGlass"/> is on, each open hosts the flyout in the
/// window's overlay layer and tags its presenter with <see cref="GlassClass"/>; a style for that
/// class (PlaybackBarView styles) gives the presenter a GlassPanel chrome and turns on
/// <see cref="MenuOpenAnimation.UseFadeProperty"/>. While glass is off neither happens, so the
/// menu opens exactly as before. Decided per open, so a Liquid Glass switch applies to the next
/// open. Right-click ContextMenus are not covered (their popup is private).
/// </summary>
public static class GlassMenuFlyout
{
    /// <summary>Presenter class while the menu is frosted.</summary>
    public const string GlassClass = "glass-menu";

    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<MenuFlyout, bool>("Enable", typeof(GlassMenuFlyout));

    public static void SetEnable(MenuFlyout flyout, bool value) => flyout.SetValue(EnableProperty, value);
    public static bool GetEnable(MenuFlyout flyout) => flyout.GetValue(EnableProperty);

    static GlassMenuFlyout()
    {
        EnableProperty.Changed.AddClassHandler<MenuFlyout>((flyout, args) =>
        {
            flyout.Opening -= OnOpening;
            if (args.GetNewValue<bool>())
                flyout.Opening += OnOpening;
        });
    }

    private static void OnOpening(object? sender, EventArgs e)
    {
        if (sender is MenuFlyout flyout)
            Apply(flyout, AppGlass.IsActive);
    }

    /// <summary>
    /// Sets up one open: overlay layer + <see cref="GlassClass"/> on the presenter when
    /// <paramref name="glassActive"/>, the stock native popup and plain presenter otherwise.
    /// Runs from Opening, after MenuFlyout has created the presenter and before the popup opens.
    /// </summary>
    public static void Apply(MenuFlyout flyout, bool glassActive)
    {
        flyout.Popup.ShouldUseOverlayLayer = glassActive;
        if (flyout.Popup.Child is Control presenter)
            presenter.Classes.Set(GlassClass, glassActive);
    }
}
