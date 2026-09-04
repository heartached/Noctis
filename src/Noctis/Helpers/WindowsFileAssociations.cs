using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Noctis.Helpers;

/// <summary>
/// Registers Noctis as an "Open with" / Default Apps candidate for the audio formats it
/// plays (Discord ask, TomSalvador 2026-08-31). Windows 10/11 no longer let an app take
/// a default for itself: the app can only advertise a ProgID plus a Capabilities block,
/// and the user picks it in Settings → Apps → Default apps (or the Open-with dialog).
/// Everything is written under HKCU, so no elevation and no effect on other accounts —
/// the same scope the installer's per-user mode uses. The Setup.exe path registers the
/// same keys via [Registry]; this exists for portable, winget and self-updated installs.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsFileAssociations
{
    public const string ProgId = "Noctis.AudioFile";
    private const string CapabilitiesPath = @"Software\Noctis\Capabilities";
    private const string RegisteredAppsPath = @"Software\RegisteredApplications";

    /// <summary>Extensions offered to Windows. Mirrors MetadataService.SupportedExtensions
    /// minus the video containers (.mp4/.asf), which a music player must not claim.</summary>
    public static readonly string[] Extensions =
    {
        ".mp3", ".flac", ".ogg", ".oga", ".m4a", ".wav", ".wma", ".aac",
        ".opus", ".aiff", ".aif", ".aifc", ".ape", ".wv", ".alac", ".dsf", ".dff",
    };

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    /// <summary>True when the ProgID exists and points at this executable.</summary>
    public static bool IsRegistered(string exePath)
        => FileAssociationCommand.PointsAt(ReadRecordedCommand(), exePath);

    /// <summary>The <c>shell\open\command</c> value this user's registration recorded,
    /// or null when Noctis was never registered (or the key is unreadable).</summary>
    public static string? ReadRecordedCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Silent self-heal for a registration that went stale: the user registered once,
    /// then moved/renamed/updated the app, so Windows' Open-with entry points at an exe
    /// that no longer exists and every double-click fails. Re-writes the same keys for
    /// the running copy — HKCU only, no prompt, no Default-apps page. Does nothing when
    /// Noctis was never registered or the recorded exe still exists (see
    /// <see cref="FileAssociationCommand.ShouldRepoint"/>). Returns true when it re-registered.
    /// </summary>
    public static bool TryRepointToCurrentExe(string exePath)
    {
        try
        {
            if (!FileAssociationCommand.ShouldRepoint(ReadRecordedCommand(), exePath, System.IO.File.Exists))
                return false;
            Register(exePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes the ProgID, per-extension OpenWithProgids entries and the
    /// Default-Apps capabilities block, then tells the shell associations changed.</summary>
    public static void Register(string exePath)
    {
        var quotedExe = $"\"{exePath}\"";

        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, "Audio File");
            prog.SetValue("FriendlyTypeName", "Audio File");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"{quotedExe},0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue(null, FileAssociationCommand.Format(exePath));
            using var open = prog.CreateSubKey(@"shell\open");
            open.SetValue(null, "Play with Noctis");
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            caps.SetValue("ApplicationName", "Noctis");
            caps.SetValue("ApplicationDescription", "Music player");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions)
                assoc.SetValue(ext, ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredAppsPath))
            registered.SetValue("Noctis", CapabilitiesPath);

        foreach (var ext in Extensions)
        {
            using var withProgids = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            withProgids.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Removes everything <see cref="Register"/> wrote. Leaves the user's other
    /// per-extension choices untouched.</summary>
    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Noctis\Capabilities", throwOnMissingSubKey: false); } catch { }
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(RegisteredAppsPath, writable: true);
            registered?.DeleteValue("Noctis", throwOnMissingValue: false);
        }
        catch { }
        foreach (var ext in Extensions)
        {
            try
            {
                using var withProgids = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                withProgids?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            catch { }
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
