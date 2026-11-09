using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Noctis.Views;

namespace Noctis.Helpers;

/// <summary>
/// Frosts the tray icon's right-click menu (GitHub #150) while Liquid Glass is on (Windows).
///
/// The menu is not a native Win32 menu: Avalonia.Win32's TrayIconImpl answers WM_RBUTTONUP
/// with its own borderless, per-pixel transparent Window (TrayPopupRoot, Name
/// "AvaloniaTrayPopupRoot_…") holding a MenuFlyoutPresenter, so the app's menu styles,
/// keyboard handling (arrows/Enter/Escape), screen-edge flipping and close-on-deactivate
/// already apply to it (Avalonia 12.1.3 TrayIconImpl.OnRightClicked / TrayPopupRoot). That
/// window is private to Avalonia, so it is recognised when it opens and given the same frost
/// as the mini player: the OS blur behind it, clipped to the card's rounded outline plus
/// <see cref="MiniPlayerWindow.FrostRegionBleed"/> with a window region, and a translucent
/// card so the blur shows.
/// </summary>
public static class TrayMenuFrost
{
    /// <summary>The Name prefix Avalonia.Win32 gives its tray menu window.</summary>
    internal const string TrayPopupNamePrefix = "AvaloniaTrayPopupRoot_";

    /// <summary>Card fill alpha while frosted: the Liquid Glass window tint's (0.65) plus a
    /// little, since the menu's text sits straight on it.</summary>
    internal const double CardTintOpacity = 0.72;

    private static bool _registered;
    private static Func<bool>? _isOn;
    private static Func<Color>? _tint;

    /// <summary>Starts watching for the tray menu. <paramref name="isOn"/> is asked on every
    /// open (Liquid Glass can change any time); <paramref name="tint"/> gives the card colour.</summary>
    public static void Register(Func<bool> isOn, Func<Color> tint)
    {
        _isOn = isOn;
        _tint = tint;
        if (_registered) return;
        _registered = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnWindowOpened);
    }

    /// <summary>Windows Settings › Personalization › Colors › Transparency effects. Off, DWM
    /// draws the blur behind the window as solid black: the frost shows nothing but black
    /// slivers at the card's corners (the region bleed), so the menu stays unfrosted. Read on
    /// every open; tests replace it.</summary>
    internal static Func<bool> TransparencyEffectsOn = ReadTransparencyEffects;

    private static bool ReadTransparencyEffects()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Missing value = Windows default, which is on.
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsTrayPopup(Window window) =>
        window.Name?.StartsWith(TrayPopupNamePrefix, StringComparison.Ordinal) == true
        && window.Content is MenuFlyoutPresenter;

    private static void OnWindowOpened(Window window, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || _isOn?.Invoke() != true || !IsTrayPopup(window)
            || !TransparencyEffectsOn()) return;
        try
        {
            Apply(window, _tint?.Invoke() ?? Color.Parse("#141414"));
        }
        catch (Exception ex)
        {
            // Cosmetic only: the menu works unfrosted.
            Noctis.Services.DebugLogger.Error(Noctis.Services.DebugLogger.Category.UI, "TrayMenuFrost", ex.Message);
        }
    }

    /// <summary>Frosts one tray menu window. Its card is laid out by now (Opened follows the
    /// first layout); the region follows later size changes (a relabelled Play/Pause).</summary>
    internal static void Apply(Window window, Color tint)
    {
        if (window.Content is not MenuFlyoutPresenter card) return;
        card.Background = new SolidColorBrush(tint, CardTintOpacity);
        window.TransparencyLevelHint = MiniPlayerWindow.TransparencyLevels(true);
        void Clip() => ApplyRegion(window, card);
        Clip();
        card.SizeChanged += (_, _) => Clip();
    }

    /// <summary>The card's outline in window DIPs (layout rects, so the open pop's scale never
    /// leaks in), or null before layout. Interface Size wraps the window content in a
    /// LayoutTransformControl (UiScaleHost); its scale is applied on the way up, or at 150%
    /// the region cut the menu to its 100% size and hid the bottom rows (Discord report).</summary>
    internal static MiniPlayerWindow.RegionShape? CardShape(Control card)
    {
        if (card.Bounds is not { Width: > 0, Height: > 0 } bounds) return null;
        var rect = new Rect(bounds.Size);
        var radius = card is TemplatedControl t ? t.CornerRadius.TopLeft : 0;
        for (Visual? v = card; v != null && v is not TopLevel; v = v.GetVisualParent())
        {
            rect = rect.Translate(v.Bounds.Position);
            if (v.GetVisualParent() is LayoutTransformControl { LayoutTransform: { } transform })
            {
                var m = transform.Value;
                rect = rect.TransformToAABB(m);
                radius *= Math.Abs(m.M11);
            }
        }
        return new MiniPlayerWindow.RegionShape(rect, radius, false);
    }

    private static void ApplyRegion(Window window, Control card)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        if (CardShape(card) is not { } shape) return;
        MiniPlayerWindow.Win32Region.Set(handle.Handle,
            MiniPlayerWindow.RegionPixels(new[] { shape }, window.RenderScaling));
    }
}
