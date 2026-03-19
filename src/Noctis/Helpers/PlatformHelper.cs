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

    /// <summary>
    /// Opens the system file manager and selects the specified file.
    /// Windows: Explorer /select, macOS: open -R
    /// </summary>
    public static void ShowInFileManager(string filePath)
    {
        try
        {
            if (IsWindows)
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else if (IsMacOS)
            {
                Process.Start("open", $"-R \"{filePath}\"");
            }
        }
        catch
        {
            // Non-critical — file manager integration is best-effort
        }
    }

    /// <summary>
    /// Opens a URL in the default browser.
    /// </summary>
    public static void OpenUrl(string url)
    {
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
            if (IsMacOS)
            {
                Process.Start("open", url);
            }
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
                Process.Start("open", $"\"{folderPath}\"");
            }
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Detects whether the system is using dark mode.
    /// Windows: reads registry, macOS: reads AppleInterfaceStyle default.
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
        }
        catch
        {
            // Default to dark
        }

        return true;
    }
}
