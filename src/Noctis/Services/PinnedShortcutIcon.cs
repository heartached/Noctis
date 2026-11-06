using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>
/// Windows: a PINNED taskbar button draws the icon of its pinned shortcut, not the window
/// icon, so the App Icon choice never showed there (verified 11-03: unpinned dev build
/// followed the window icon live, the pinned button stayed classic). This points the
/// pinned shortcut of THIS exe at the chosen .ico; Explorer repaints the button live
/// after SHCNE_UPDATEITEM. Classic resets it to the exe's own icon.
/// </summary>
public static class PinnedShortcutIcon
{
    private static readonly object s_gate = new();

    internal static string PinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>Where the chosen icon is copied so the shortcut can point at a real file.</summary>
    internal static string IconFile(string id) => Path.Combine(AppPaths.DataRoot, "app-icons", id + ".ico");

    /// <summary>True when a shortcut target is this exe (case-insensitive, normalised paths).</summary>
    internal static bool IsSameExe(string? target, string? exe)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(exe)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Icon location the shortcut should carry: empty = the exe's own (classic) icon.</summary>
    internal static string DesiredIconPath(string id) =>
        id == AppIconService.DefaultId ? "" : IconFile(id);

    /// <summary>
    /// Fire and forget, off the UI thread. <paramref name="icoBytes"/> is the chosen icon
    /// (read from avares on the UI side); null for classic.
    /// </summary>
    public static void Sync(string id, byte[]? icoBytes)
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Environment.ProcessPath;
        _ = Task.Run(() =>
        {
            lock (s_gate)
            {
                try { SyncCore(id, icoBytes, exe); }
                catch (Exception ex)
                {
                    DebugLogger.Error(DebugLogger.Category.UI, "AppIcon.Pinned", ex.Message);
                }
            }
        });
    }

    /// <summary>
    /// Settings › Reset Everything: deletes the icon copies once the classic icon is back (the
    /// reset's AppIcon change queued that <see cref="Sync"/> first; the same gate keeps them in
    /// order). A pinned shortcut of another Noctis on this data folder that still points here
    /// gets its copy rewritten by that app's next start (it syncs at launch).
    /// </summary>
    public static Task ClearIconCopiesAsync(string dataRoot) => Task.Run(() =>
    {
        lock (s_gate)
        {
            var dir = Path.Combine(dataRoot, "app-icons");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    });

    private static void SyncCore(string id, byte[]? icoBytes, string? exe)
    {
        var folder = PinnedFolder;
        if (exe == null || !Directory.Exists(folder)) return;
        foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                var file = (IPersistFile)link;
                file.Load(lnk, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (!IsSameExe(target.ToString(), exe)) continue;

                var desired = DesiredIconPath(id);
                if (desired.Length > 0)
                {
                    if (icoBytes == null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(desired)!);
                    if (!File.Exists(desired) || new FileInfo(desired).Length != icoBytes.Length)
                        File.WriteAllBytes(desired, icoBytes);
                }

                var current = new StringBuilder(1024);
                link.GetIconLocation(current, current.Capacity, out _);
                if (string.Equals(current.ToString(), desired, StringComparison.OrdinalIgnoreCase)) continue;

                link.SetIconLocation(desired, 0);
                file.Save(lnk, true);
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lnk, IntPtr.Zero);
                // UPDATEITEM alone often leaves the pinned button on the old icon (seen 11-04:
                // shortcut on midnight.ico, button still Dark). ASSOCCHANGED makes Explorer
                // re-read its icons; verified it repaints the button at once. Only runs on an
                // App Icon change, so its cost doesn't matter.
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                DebugLogger.Info(DebugLogger.Category.UI, "AppIcon.Pinned", $"{Path.GetFileName(lnk)} -> {id}");
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATHW = 0x0005;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
