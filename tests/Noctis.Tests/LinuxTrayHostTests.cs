using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Services;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit P29: on stock GNOME (no StatusNotifierWatcher) Avalonia still creates the TrayIcon,
/// so start-minimized-at-login, minimize-to-tray and close-to-tray hid Noctis into a tray that
/// isn't there — no window, no taskbar entry, no icon.
/// </summary>
public class LinuxTrayHostTests
{
    [Theory]
    [InlineData(true, true, false, false)]   // stock GNOME: TrayIcon created, nobody hosts it
    [InlineData(true, true, true, true)]     // KDE, GNOME + AppIndicator
    [InlineData(false, true, true, false)]   // tray failed to initialize
    [InlineData(true, false, false, true)]   // Windows / macOS: a created TrayIcon is shown
    [InlineData(false, false, false, false)]
    public void IsTrayUsable_OnLinux_NeedsAStatusNotifierHost(
        bool trayIconCreated, bool isLinux, bool hostPresent, bool expected)
    {
        Assert.Equal(expected, LinuxTrayHost.IsTrayUsable(trayIconCreated, isLinux, hostPresent));
    }

    [Fact]
    public void TryStart_OffLinux_ReturnsNull()
    {
        if (OperatingSystem.IsLinux()) return; // the Linux path needs a session bus; covered by hand
        Assert.Null(LinuxTrayHost.TryStart());
    }

    [AvaloniaFact]
    public void SettleStartMinimized_NoTray_GivesTheTaskbarButtonBack()
    {
        // As App leaves a login launch: minimized, off the taskbar.
        var window = new Window { WindowState = WindowState.Minimized, ShowInTaskbar = false };
        window.Show();
        try
        {
            MainWindow.SettleStartMinimized(window, trayUsable: false);

            Assert.True(window.IsVisible);
            Assert.True(window.ShowInTaskbar);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettleStartMinimized_WithTray_HidesIntoIt()
    {
        var window = new Window { WindowState = WindowState.Minimized, ShowInTaskbar = false };
        window.Show();
        try
        {
            MainWindow.SettleStartMinimized(window, trayUsable: true);

            Assert.False(window.IsVisible);
            Assert.False(window.ShowInTaskbar);
        }
        finally
        {
            window.Close();
        }
    }
}
