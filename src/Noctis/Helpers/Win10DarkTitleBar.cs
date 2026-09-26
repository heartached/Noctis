using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;
using Noctis.Services;

namespace Noctis.Helpers;

/// <summary>
/// Dark native title bar on Windows 10 (Discord "Windows 10 acting Weird", 2026-09-25).
/// Avalonia 12's Win32 <c>WindowImpl.SetFrameThemeVariant</c> only sets
/// DWMWA_USE_IMMERSIVE_DARK_MODE when <c>Build &gt;= 22000</c>, so on Windows 10 the
/// caption stays white under the dark theme. Windows 10 has honoured the attribute since
/// 1809 — as 19 before build 18985, as 20 from then on — so we set it ourselves there.
/// Windows 11 is left to Avalonia.
/// </summary>
public static class Win10DarkTitleBar
{
    private const int WM_NCACTIVATE = 0x0086;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The immersive-dark-mode attribute id for a Windows build, or null where Noctis
    /// shouldn't set it (pre-1809 has no dark caption; 22000+ Avalonia already handles).
    /// </summary>
    public static uint? AttributeForBuild(int build) => build switch
    {
        < 17763 => null,
        < 18985 => 19u,
        < 22000 => 20u,
        _ => null,
    };

    /// <summary>
    /// Paints <paramref name="window"/>'s caption to match its <see cref="Window.ActualThemeVariant"/>.
    /// Called before the window is first shown and again on every theme switch. Never throws.
    /// </summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AttributeForBuild(Environment.OSVersion.Version.Build) is not { } attr) return;

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

            var dark = window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, attr, ref dark, sizeof(int)) != 0) return;

            // Windows 10 doesn't repaint an already-visible caption for this attribute;
            // bouncing the non-client activation state forces the redraw.
            if (window.IsVisible)
            {
                var active = window.IsActive;
                SendMessage(hwnd, WM_NCACTIVATE, active ? IntPtr.Zero : 1, IntPtr.Zero);
                SendMessage(hwnd, WM_NCACTIVATE, active ? 1 : IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "Win10DarkTitleBar", ex.Message);
        }
    }
}
