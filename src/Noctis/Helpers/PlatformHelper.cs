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
                    {
                        // ArgumentList — the string overload splits on spaces.
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "xdg-open",
                            ArgumentList = { parent },
                            UseShellExecute = false
                        });
                    }
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
            // Percent-encoded file URI: dbus-send's array:string: syntax splits
            // elements on commas, and Uri leaves ',' unescaped (legal in a URI
            // path), so it must be encoded on top of Uri's own escaping.
            var fileUri = new Uri(filePath).AbsoluteUri.Replace(",", "%2C");
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
                    $"array:string:{fileUri}",
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
                // ArgumentList — the string overload splits on spaces.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { folderPath },
                    UseShellExecute = false
                });
            }
        }
        catch
        {
            // Non-critical
        }
    }

    /// <summary>
    /// Detects whether the system is using dark mode.
    /// Windows: reads registry, macOS: reads AppleInterfaceStyle default,
    /// Linux: GNOME/GTK gsettings, then the freedesktop settings portal, then KDE's kdeglobals.
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

                // Non-GNOME desktops (KDE, XFCE, …) don't answer the gsettings
                // probes. The freedesktop settings portal covers them uniformly:
                // 0 = no preference, 1 = prefer dark, 2 = prefer light.
                var portal = ReadPortalColorScheme();
                if (portal == 1) return true;
                if (portal == 2) return false;

                // KDE without a running portal: kdeglobals records the active
                // color scheme (e.g. BreezeDark / BreezeLight).
                var kdeScheme = ReadKdeColorScheme();
                if (!string.IsNullOrEmpty(kdeScheme))
                    return kdeScheme.Contains("dark", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Default to dark
        }

        return true;
    }

    /// <summary>
    /// Reads org.freedesktop.appearance color-scheme from the xdg-desktop-portal
    /// (works on KDE, GNOME and most other desktops). Returns 0/1/2 per the
    /// portal spec, or null when the portal/gdbus is unavailable.
    /// </summary>
    private static int? ReadPortalColorScheme()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gdbus",
                Arguments = "call --session --dest org.freedesktop.portal.Desktop " +
                            "--object-path /org/freedesktop/portal/desktop " +
                            "--method org.freedesktop.portal.Settings.Read " +
                            "org.freedesktop.appearance color-scheme",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            if (proc.ExitCode != 0) return null;
            // Output shape: (<<uint32 1>>,)
            var idx = output.IndexOf("uint32 ", StringComparison.Ordinal);
            if (idx < 0 || idx + 7 >= output.Length) return null;
            return output[idx + 7] switch { '0' => 0, '1' => 1, '2' => 2, _ => (int?)null };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the active KDE color scheme name from kdeglobals, or null when the
    /// file or key is absent.
    /// </summary>
    private static string? ReadKdeColorScheme()
    {
        try
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var kdeGlobals = Path.Combine(configHome, "kdeglobals");
            if (!File.Exists(kdeGlobals)) return null;
            foreach (var line in File.ReadLines(kdeGlobals))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ColorScheme=", StringComparison.Ordinal))
                    return trimmed.Substring("ColorScheme=".Length).Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// True when the Linux session has a running compositor, i.e. when a window may ask
    /// for per-pixel transparency and expect it to render. False everywhere else,
    /// including on Windows and macOS — callers there have no reason to ask.
    /// </summary>
    /// <remarks>
    /// Answered once per call site at window-creation time; Avalonia's X11 backend does
    /// not track a compositor that starts or stops later (AvaloniaUI/Avalonia#3300), so
    /// a window opened without one keeps its opaque fallback for its lifetime.
    /// </remarks>
    public static bool IsLinuxCompositorRunning()
    {
        if (!IsLinux) return false;

        try
        {
            // Wayland has no un-composited mode — the compositor IS the display server.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                return true;

            // X11: a compositing manager owns the _NET_WM_CM_S<screen> selection
            // (EWMH). KWin, Mutter, picom and Xfwm's compositor all claim it; a bare
            // WM leaves it unowned, which is precisely the case where an ARGB visual
            // renders as garbage.
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return false;

            try
            {
                var atom = XInternAtom(display, $"_NET_WM_CM_S{XDefaultScreen(display)}", true);
                if (atom == IntPtr.Zero) return false;
                return XGetSelectionOwner(display, atom) != IntPtr.Zero;
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // No libX11, no display, headless CI — assume no compositor and let the
            // caller take its opaque path.
            return false;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr atom);
}
