using Avalonia;
using Avalonia.Controls;

namespace Noctis.Helpers;

public static class DialogHelper
{
    /// <summary>
    /// Sizes and positions a transparent overlay dialog so it covers the entire owner window.
    /// </summary>
    public static void SizeToOwner(Window dialog, Window owner)
    {
        var screen = owner.Screens.ScreenFromWindow(owner);
        var scaling = screen?.Scaling ?? 1.0;

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
            // Use client area to avoid Windows 11 invisible border mismatch
            dialog.Width = owner.ClientSize.Width;
            dialog.Height = owner.ClientSize.Height;

            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            var clientOrigin = owner.PointToScreen(new Point(0, 0));
            dialog.Position = new PixelPoint(clientOrigin.X, clientOrigin.Y);
        }
    }
}
