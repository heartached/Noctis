using System;
using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// Process-wide Liquid Glass state, published by MainWindow.ApplyLiquidGlass and
/// consumed by every <see cref="Noctis.Controls.GlassPanel"/> (dialog windows never read it).
/// Hosts never read settings themselves: the window decides (platform gate, theme
/// tint), this flag carries the decision. UI thread only.
/// </summary>
public static class AppGlass
{
    private static bool _isActive;
    private static Color _surfaceTint = Color.Parse("#252525");
    private static Color _sidebarTint = Color.Parse("#141414");

    /// <summary>True while Liquid Glass is on and applicable on this platform.</summary>
    public static bool IsActive => _isActive;

    /// <summary>Theme main-surface colour glass panels tint with (page cards, sheets, island).</summary>
    public static Color SurfaceTint => _surfaceTint;

    /// <summary>Theme sidebar colour glass panels tint with (sidebar rail).</summary>
    public static Color SidebarTint => _sidebarTint;

    /// <summary>Raised on every <see cref="Set"/>, even when nothing changed, so a theme
    /// switch while glass is on re-tints live.</summary>
    public static event EventHandler? Changed;

    public static void Set(bool isActive, Color surfaceTint, Color sidebarTint)
    {
        _isActive = isActive;
        _surfaceTint = surfaceTint;
        _sidebarTint = sidebarTint;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Turns glass off, keeping the last tints.</summary>
    public static void Clear() => Set(false, _surfaceTint, _sidebarTint);
}
