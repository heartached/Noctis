using System.Diagnostics;
using System.Runtime.InteropServices;

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
    public static bool TryMoveToTrash(string path) => TryMoveToTrash(path, out _);

    /// <inheritdoc cref="TryMoveToTrash(string)"/>
    /// <param name="path">The file to trash.</param>
    /// <param name="declined">True when Windows could only delete permanently (the
    /// drive's Recycle Bin is off, or the item is over its quota) and the user said No.
    /// Trying again would only raise the same prompt.</param>
    public static bool TryMoveToTrash(string path, out bool declined)
    {
        declined = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return TrashCore(path, out declined);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the directory at <paramref name="path"/> (and its contents) to the OS
    /// trash. Returns true only when the directory existed and was trashed. All three
    /// platform backends accept directories the same way they accept files.
    /// </summary>
    public static bool TryMoveDirectoryToTrash(string path) => TryMoveDirectoryToTrash(path, out _);

    /// <inheritdoc cref="TryMoveDirectoryToTrash(string)"/>
    /// <param name="path">The directory to trash.</param>
    /// <param name="declined">Same as <see cref="TryMoveToTrash(string, out bool)"/>.</param>
    public static bool TryMoveDirectoryToTrash(string path, out bool declined)
    {
        declined = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            return TrashCore(path, out declined);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrashCore(string path, out bool declined)
    {
        declined = false;
        if (OperatingSystem.IsWindows()) return WindowsRecycle(path, out declined);
        if (OperatingSystem.IsMacOS()) return MacTrash(path);
        if (OperatingSystem.IsLinux()) return LinuxTrash(path);
        return false;
    }

    // SHFileOperation with FOF_NOERRORUI/FOF_SILENT so a failure can never pop a
    // modal shell dialog from a background thread (Microsoft.VisualBasic's
    // DeleteFile with UIOption.OnlyErrorDialogs did exactly that); failures just
    // surface as `false` to the caller. The one prompt left is the shell's
    // "permanently delete?" nuke warning (see FOF_WANTNUKEWARNING below).
    private static bool WindowsRecycle(string path, out bool declined)
    {
        declined = false;
        var fullPath = Path.GetFullPath(path);
        // FOF_ALLOWUNDO only recycles "if possible": on a volume with no Recycle Bin
        // (UNC or mapped network share, USB stick) the shell deletes permanently, so
        // refuse up front and let the caller report "couldn't trash".
        if (!IsRecyclableVolume(fullPath, root => new DriveInfo(root).DriveType)) return false;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // The file list is double-null-terminated; marshaling adds one
            // terminator, the explicit "\0" supplies the second.
            pFrom = fullPath + "\0",
            // FOF_WANTNUKEWARNING: without it FOF_NOCONFIRMATION answers "Yes" to the
            // shell's permanent-delete prompt (item over the bin quota, bin turned off
            // for the drive); declining leaves the item and reports false.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref op);
        declined = WasDeclined(result, op.fAnyOperationsAborted);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>
    /// True when the operation was cancelled at a prompt instead of failing. Every other
    /// dialog is suppressed, so the only prompt left is the permanent-delete warning.
    /// Real failures return an error code with the aborted flag clear. Measured with
    /// these flags: a missing path returns 2, a locked file 32, a folder holding a
    /// locked file 124. So the handle-release retries are not mistaken for a No.
    /// </summary>
    internal static bool WasDeclined(int result, bool anyOperationsAborted)
        => anyOperationsAborted || result == ERROR_CANCELLED || result == DE_OPCANCELLED;

    private const int ERROR_CANCELLED = 1223;
    private const int DE_OPCANCELLED = 0x75;

    /// <summary>
    /// True when <paramref name="fullPath"/> is on a local fixed drive, the only kind
    /// of volume Windows keeps a Recycle Bin on. UNC paths and network, removable,
    /// RAM and optical drives have none, so "recycling" there is a permanent delete.
    /// </summary>
    internal static bool IsRecyclableVolume(string fullPath, Func<string, DriveType> driveTypeOf)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrEmpty(root) && driveTypeOf(root) == DriveType.Fixed;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Note: this unpacked layout is correct for x64/arm64 (the shipped Windows
    // targets); 32-bit x86 would need the Pack=1 variant of the struct.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // Ask Finder to move the file to the Trash. The path is passed as a script
    // argument (not interpolated into the AppleScript source) so paths containing
    // quotes or backslashes can't break the script.
    private static bool MacTrash(string path) => RunProcess(
        "osascript",
        "-e", "on run argv",
        "-e", "tell application \"Finder\" to delete (POSIX file (item 1 of argv) as alias)",
        "-e", "end run",
        path);

    // freedesktop trash spec; gio ships with glib2 on essentially every Linux desktop —
    // but not on minimal/headless installs or some non-GNOME setups, where the shell-out
    // just failed and the caller reported success anyway. Fall back to implementing the
    // spec directly so "Move to Trash" doesn't silently do nothing.
    private static bool LinuxTrash(string path)
        => RunProcess("gio", "trash", "--", path) || FreedesktopTrash(path);

    /// <summary>
    /// Minimal freedesktop.org Trash implementation for hosts without `gio`: move the
    /// file into ~/.local/share/Trash/files and write the matching .trashinfo record.
    /// </summary>
    private static bool FreedesktopTrash(string path)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return false;

            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataHome))
                dataHome = Path.Combine(home, ".local", "share");

            var trashRoot = Path.Combine(dataHome, "Trash");
            var filesDir = Path.Combine(trashRoot, "files");
            var infoDir = Path.Combine(trashRoot, "info");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);

            // Unique destination name (the spec requires the two stay in lockstep).
            var baseName = Path.GetFileName(path);
            var destName = baseName;
            var stem = Path.GetFileNameWithoutExtension(baseName);
            var ext = Path.GetExtension(baseName);
            for (var i = 1; File.Exists(Path.Combine(filesDir, destName))
                            || File.Exists(Path.Combine(infoDir, destName + ".trashinfo")); i++)
            {
                destName = $"{stem}.{i}{ext}";
                if (i > 10_000) return false;
            }

            var infoPath = Path.Combine(infoDir, destName + ".trashinfo");
            var info = "[Trash Info]\n" +
                       $"Path={Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/")}\n" +
                       $"DeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n";
            File.WriteAllText(infoPath, info);

            try
            {
                File.Move(path, Path.Combine(filesDir, destName));
            }
            catch
            {
                // Cross-device move (trash is on a different filesystem) — the spec wants
                // a per-volume .Trash-$uid there, which is more than this fallback covers.
                try { File.Delete(infoPath); } catch { }
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

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
            PlatformHelper.ScrubAppImageEnvironment(psi);

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
