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
    public static bool TryMoveToTrash(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            return TrashCore(path);
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
    public static bool TryMoveDirectoryToTrash(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            return TrashCore(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrashCore(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsRecycle(path);
        if (OperatingSystem.IsMacOS()) return MacTrash(path);
        if (OperatingSystem.IsLinux()) return LinuxTrash(path);
        return false;
    }

    // SHFileOperation with FOF_NOERRORUI/FOF_SILENT so a failure can never pop a
    // modal shell dialog from a background thread (Microsoft.VisualBasic's
    // DeleteFile with UIOption.OnlyErrorDialogs did exactly that); failures just
    // surface as `false` to the caller.
    private static bool WindowsRecycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // The file list is double-null-terminated; marshaling adds one
            // terminator, the explicit "\0" supplies the second.
            pFrom = Path.GetFullPath(path) + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

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
