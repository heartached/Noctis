using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Noctis.ViewModels;

namespace Noctis.Helpers;

public static class DialogHelper
{
    /// <summary>
    /// Sizes and positions a transparent overlay dialog so it covers the entire owner window.
    /// </summary>
    public static void SizeToOwner(Window dialog, Window owner)
    {
        // A dialog raised while Settings is open is part of the Settings section: Styles.axaml
        // keys its hover rules (no zoom, accent hover shade) off this class.
        if (App.Services?.GetService<MainWindowViewModel>()?.IsSettingsModalOpen == true)
            dialog.Classes.Add("settings-popup");

        var screen = owner.Screens.ScreenFromWindow(owner);
        var scaling = screen?.Scaling ?? 1.0;

        // The title-bar-inset math in the Windows branch below subtracts
        // owner.Position from PointToScreen(0,0); that only yields the title-bar
        // height when both report in the same unit/origin space, which is a
        // Windows-only guarantee. On macOS the two report in different spaces, so
        // the overlay window lands at the wrong offset and size — the reported
        // dim layer "misaligned to the screen". Cover the client area directly:
        // PointToScreen(0,0) is a reliable physical-pixel origin on every backend,
        // and ClientSize is the matching DIP size, so the overlay maps 1:1 onto the
        // owner's content with no cross-platform coordinate assumptions.
        if (!OperatingSystem.IsWindows())
        {
            void Cover()
            {
                dialog.Position = owner.PointToScreen(new Point(0, 0));
                dialog.Width = owner.ClientSize.Width;
                dialog.Height = owner.ClientSize.Height;
            }
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            Cover();

            // The owner stays draggable/resizable under a modal here (Windows disables it),
            // so keep the overlay on it: it used to stay put while the window moved away.
            EventHandler<PixelPointEventArgs> moved = (_, _) => Cover();
            EventHandler<WindowResizedEventArgs> resized = (_, _) => Cover();
            owner.PositionChanged += moved;
            owner.Resized += resized;
            dialog.Closed += (_, _) =>
            {
                owner.PositionChanged -= moved;
                owner.Resized -= resized;
            };
            return;
        }

        // For maximized windows, use the screen working area directly
        // because Position/FrameSize include invisible resize borders on Windows.
        if (owner.WindowState == WindowState.Maximized && screen != null)
        {
            var wa = screen.WorkingArea;
            dialog.Width = wa.Width / scaling;
            dialog.Height = wa.Height / scaling;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Position = new PixelPoint(wa.X, wa.Y);
        }
        else
        {
            // Use client area for width to avoid Windows 11 invisible side-border mismatch,
            // but extend upward to cover the native title bar so the overlay dims it too.
            var clientOrigin = owner.PointToScreen(new Point(0, 0));
            var topInsetPx = clientOrigin.Y - owner.Position.Y;
            if (topInsetPx < 0) topInsetPx = 0;

            dialog.Width = owner.ClientSize.Width;
            dialog.Height = owner.ClientSize.Height + (topInsetPx / scaling);

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Position = new PixelPoint(clientOrigin.X, owner.Position.Y);
        }
    }
}
