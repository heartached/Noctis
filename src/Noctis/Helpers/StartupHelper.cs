using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Noctis.Helpers;

/// <summary>
/// Cross-platform "launch Noctis when the user logs in" toggle. Every entry is
/// per-user and needs no elevation:
///   • Windows — a value under HKCU\…\CurrentVersion\Run.
///   • macOS   — a LaunchAgent plist in ~/Library/LaunchAgents.
///   • Linux   — a .desktop file in ~/.config/autostart.
/// The OS entry itself is the source of truth, so a change made outside the app
/// (Task Manager's Startup tab, macOS Login Items, deleting the file) stays in
/// sync with the toggle. Nothing here throws, so a locked-down environment can't
/// crash the Settings page — but <see cref="SetEnabled"/> reports failure so the
/// caller can re-read the real state instead of showing a toggle that lies.
/// </summary>
public static class StartupHelper
{
    private const string AppName = "Noctis";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Passed in every autostart command so a login launch is distinguishable
    /// from a manual one.</summary>
    private const string StartupArg = "--startup";

    /// <summary>Added to the autostart command when the user wants the login launch to
    /// start hidden in the tray. The app reads this from its args at startup.</summary>
    private const string MinimizedArg = "--minimized";

    /// <summary>Whether Noctis is currently registered to launch at login.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsIsEnabled();
            if (OperatingSystem.IsMacOS()) return File.Exists(MacAgentPath);
            if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupHelper] IsEnabled failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>Registers (or removes) the launch-at-login entry for this copy.
    /// <paramref name="startMinimized"/> adds a "--minimized" arg so the app can start
    /// hidden in the tray on login — encoded in the command (not read from async-loaded
    /// settings) so the decision is available the instant the process starts.
    /// <para>Returns false when the entry could not be written or removed. Callers must
    /// not assume success: a locked-down HKCU, a read-only ~/.config/autostart or a
    /// macOS build running outside a .app bundle all fail here, and the toggle used to
    /// stay visually ON while nothing had been registered — the user found out at the
    /// next login.</para></summary>
    public static bool SetEnabled(bool enabled, bool startMinimized = false)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsSet(enabled, startMinimized);
            if (OperatingSystem.IsMacOS()) return MacSet(enabled, startMinimized);
            if (OperatingSystem.IsLinux()) return LinuxSet(enabled, startMinimized);
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupHelper] SetEnabled({enabled},{startMinimized}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The argument string appended to the launch command.</summary>
    private static string LaunchArgs(bool startMinimized) =>
        startMinimized ? $"{StartupArg} {MinimizedArg}" : StartupArg;

    /// <summary>The path the OS should run at login — the AppImage when packaged as
    /// one (its path survives self-update via in-place swap), otherwise this exe.</summary>
    private static string? LaunchTargetPath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage)
            ? appImage
            : Environment.ProcessPath;

    // ── Windows: HKCU Run key ──
    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
        return key?.GetValue(AppName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsSet(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(WindowsRunKey);
        if (key is null) return false;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            key.SetValue(AppName, $"\"{exe}\" {LaunchArgs(startMinimized)}");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
        return true;
    }

    // ── macOS: LaunchAgent plist ──
    private static string MacAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.heartached.noctis.plist");

    private static bool MacSet(bool enabled, bool startMinimized)
    {
        if (!enabled)
        {
            if (File.Exists(MacAgentPath)) File.Delete(MacAgentPath);
            return true;
        }

        // `open <bundle>` is the only launch form a LaunchAgent can use here, so an
        // unbundled run (dotnet run, a bare publish folder) genuinely cannot register.
        var appPath = MacAppBundlePath();
        if (string.IsNullOrEmpty(appPath)) return false;

        var minimizedArg = startMinimized ? $"    <string>{MinimizedArg}</string>\n" : string.Empty;

        Directory.CreateDirectory(Path.GetDirectoryName(MacAgentPath)!);
        var plist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "  <key>Label</key>\n" +
            "  <string>com.heartached.noctis</string>\n" +
            "  <key>ProgramArguments</key>\n" +
            "  <array>\n" +
            "    <string>/usr/bin/open</string>\n" +
            $"    <string>{System.Security.SecurityElement.Escape(appPath)}</string>\n" +
            "    <string>--args</string>\n" +
            $"    <string>{StartupArg}</string>\n" +
            minimizedArg +
            "  </array>\n" +
            "  <key>RunAtLoad</key>\n" +
            "  <true/>\n" +
            "</dict>\n" +
            "</plist>\n";
        File.WriteAllText(MacAgentPath, plist);
        return true;
    }

    /// <summary>Resolves the enclosing .app bundle from the running executable
    /// (…/Noctis.app/Contents/MacOS/Noctis → …/Noctis.app); null when not bundled.</summary>
    private static string? MacAppBundlePath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ── Linux: ~/.config/autostart/.desktop ──
    private static string LinuxDesktopPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // ~/.config
        "autostart", "noctis.desktop");

    private static bool LinuxSet(bool enabled, bool startMinimized)
    {
        if (!enabled)
        {
            if (File.Exists(LinuxDesktopPath)) File.Delete(LinuxDesktopPath);
            return true;
        }

        var exec = LaunchTargetPath;
        if (string.IsNullOrEmpty(exec)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
        var desktop =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Noctis\n" +
            $"Exec=\"{exec}\" {LaunchArgs(startMinimized)}\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n";
        File.WriteAllText(LinuxDesktopPath, desktop);
        return true;
    }
}
