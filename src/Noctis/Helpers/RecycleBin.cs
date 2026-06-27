using System.Diagnostics;

namespace Noctis.Helpers;

/// <summary>
/// Moves files to the operating system's trash / recycle bin.
/// Windows uses the Recycle Bin; macOS asks Finder; Linux uses the freedesktop
/// trash via <c>gio trash</c>. On any failure (tool missing, error, or
/// unsupported platform) the file is left untouched — this never permanently
/// deletes as a fallback, so callers can surface a "couldn't trash" result
/// instead of silently destroying data.
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// Moves <paramref name="path"/> to the OS trash. Returns true only when the
    /// file existed and was successfully trashed.
    /// </summary>
    public static bool TryMoveToTrash(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            if (OperatingSystem.IsWindows()) return WindowsRecycle(path);
            if (OperatingSystem.IsMacOS()) return MacTrash(path);
            if (OperatingSystem.IsLinux()) return LinuxTrash(path);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool WindowsRecycle(string path)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        return true;
    }

    // Ask Finder to move the file to the Trash. The path is passed as a script
    // argument (not interpolated into the AppleScript source) so paths containing
    // quotes or backslashes can't break the script.
    private static bool MacTrash(string path) => RunProcess(
        "osascript",
        "-e", "on run argv",
        "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv) as alias)",
        "-e", "end run",
        path);

    // freedesktop trash spec; gio ships with glib2 on essentially every Linux desktop.
    private static bool LinuxTrash(string path) => RunProcess("gio", "trash", "--", path);

    private static bool RunProcess(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
