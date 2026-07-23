using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Noctis.Helpers;

/// <summary>
/// Cross-platform utility for OS-specific operations (file manager, URL opening, theme detection).
/// </summary>
public static class PlatformHelper
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Opens the system file manager and selects the specified file.
    /// Windows: Explorer /select, macOS: open -R, Linux: best-effort via dbus/nautilus, falls back to opening parent dir.
    /// </summary>
    public static void ShowInFileManager(string filePath)
    {
        try
        {
            if (IsWindows)
            {
                // Explorer's /select syntax needs the quoted single-string form;
                // Windows filenames can't contain quotes, so this can't split.
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else if (IsMacOS)
            {
                // ArgumentList, not a hand-quoted string: a filename containing
                // a quote would otherwise split into extra arguments.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-R", filePath },
                    UseShellExecute = false
                });
            }
            else if (IsLinux)
            {
                if (!TryShowInLinuxFileManager(filePath))
                {
                    var parent = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(parent))
                        Process.Start("xdg-open", parent);
                }
            }
        }
        catch
        {
            // Non-critical — file manager integration is best-effort
        }
    }

    private static bool TryShowInLinuxFileManager(string filePath)
    {
        // Try the FileManager1 D-Bus interface first (works for nautilus, nemo, dolphin, thunar).
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dbus-send",
                ArgumentList =
                {
                    "--session",
                    "--dest=org.freedesktop.FileManager1",
                    "--type=method_call",
                    "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1.ShowItems",
                    $"array:string:file://{filePath}",
                    "string:"
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(1500);
                if (proc.ExitCode == 0) return true;
            }
        }
        catch { /* fall through */ }

        return false;
    }

    /// <summary>
    /// Opens a URL in the default browser.
    /// </summary>
    public static void OpenUrl(string url)
    {
        // ShellExecute launches whatever it is handed (file:, UNC, custom
        // protocols, .exe paths) — restrict to real web URLs so a future caller
        // can't be steered into launching something else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback for platforms where UseShellExecute doesn't work
            try
            {
                if (IsMacOS)
                    Process.Start("open", url);
                else if (IsLinux)
                    Process.Start("xdg-open", url);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Opens a folder in the system file manager.
    /// </summary>
    public static void OpenFolder(string folderPath)
    {
        try
        {
            if (IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            else if (IsMacOS)
            {
                // ArgumentList — see ShowInFileManager.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { folderPath },
                    UseShellExecute = false
                });
            }
            else if (IsLinux)
            {
                Process.Start("xdg-open", folderPath);
            }
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Detects whether the system is using dark mode.
    /// Windows: reads registry, macOS: reads AppleInterfaceStyle default, Linux: GNOME/GTK gsettings.
    /// </summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            if (IsWindows)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                return value is int i && i == 0;
            }

            if (IsMacOS)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "defaults",
                    Arguments = "read -g AppleInterfaceStyle",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(1000);
                    return string.Equals(output, "Dark", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (IsLinux)
            {
                // GNOME 42+ exposes color-scheme; older GNOME exposes gtk-theme.
                var colorScheme = ReadGSettings("org.gnome.desktop.interface", "color-scheme");
                if (!string.IsNullOrEmpty(colorScheme))
                    return colorScheme.Contains("dark", StringComparison.OrdinalIgnoreCase);

                var gtkTheme = ReadGSettings("org.gnome.desktop.interface", "gtk-theme");
                if (!string.IsNullOrEmpty(gtkTheme))
                    return gtkTheme.Contains("dark", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Default to dark
        }

        return true;
    }

    private static string? ReadGSettings(string schema, string key)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gsettings",
                Arguments = $"get {schema} {key}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim().Trim('\'');
            proc.WaitForExit(1000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
