using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Noctis.Helpers;

/// <summary>
/// The mini player's pin (Discord request: keep it on screen through Show desktop, Minimize
/// all and games). Windows only. Measured on Windows 11 (26200) with probe windows styled
/// like the mini player:
/// <list type="bullet">
/// <item>Show desktop (Win+D, the taskbar corner) and Minimize all (Win+M) MINIMIZE every
/// taskbar window that has a minimize box — Topmost alone does not exempt it. The shell does
/// this with a direct state change, not WM_SYSCOMMAND, so swallowing SC_MINIMIZE does nothing.
/// A window without WS_MINIMIZEBOX is skipped, keeps its taskbar button and never moves.
/// Un-minimizing after the fact works too, but it flickers and, worse, the undo of Show desktop
/// then left another app's window stuck always-on-top.</item>
/// <item>A borderless fullscreen game that re-asserts HWND_TOPMOST in its own focus handler
/// (GLFW does) lands above a plain Topmost window; re-asserting once on the foreground change
/// loses that race, re-asserting again a little later wins it.</item>
/// </list>
/// So pinned = no minimize box (<see cref="CanMinimize"/>) + <see cref="TopmostKeeper"/>.
/// </summary>
public static class MiniPlayerPin
{
    /// <summary>Only Windows has the behaviour this fixes and the means to fix it; elsewhere
    /// the mini player is already always-on-top and the button is hidden.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The window's minimize box: dropped while pinned, which is exactly what makes
    /// the shell's Show desktop / Minimize all pass it over. Unpinned keeps today's default.</summary>
    public static bool CanMinimize(bool pinned) => !pinned;

    /// <summary>When the mini player puts itself back on top after a foreground change: at
    /// once, then again after the new foreground window's own focus handling has had time to
    /// raise it (a fullscreen game re-asserting HWND_TOPMOST).</summary>
    public static readonly TimeSpan[] ReassertDelays =
    {
        TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1000),
    };

    /// <summary>A foreground change is worth answering only while pinned and shown, and only
    /// when some OTHER window took the foreground.</summary>
    public static bool ShouldReassert(IntPtr foreground, IntPtr self, bool pinned, bool shown) =>
        pinned && shown && self != IntPtr.Zero && foreground != IntPtr.Zero && foreground != self;
}

/// <summary>
/// Puts a window back at the top of the topmost band whenever another window takes the
/// foreground (EVENT_SYSTEM_FOREGROUND, out of context, so the callback arrives on the UI
/// thread's message loop). Never activates or shows the window.
/// </summary>
public sealed class TopmostKeeper : IDisposable
{
    private readonly Func<IntPtr> _hwnd;
    private readonly Func<bool> _shown;
    private readonly WinEventProc _proc;
    private IntPtr _hook;
    private int _generation;

    public TopmostKeeper(Func<IntPtr> hwnd, Func<bool> shown)
    {
        _hwnd = hwnd;
        _shown = shown;
        _proc = OnWinEvent; // kept alive for as long as the hook is installed
        if (MiniPlayerPin.IsSupported)
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WinEventOutOfContext);
        Reassert();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!MiniPlayerPin.ShouldReassert(hwnd, _hwnd(), pinned: _hook != IntPtr.Zero, shown: _shown()))
            return;
        // A newer foreground change supersedes the late passes of an older one.
        var generation = ++_generation;
        foreach (var delay in MiniPlayerPin.ReassertDelays)
        {
            if (delay == TimeSpan.Zero) { Reassert(); continue; }
            DispatcherTimer.RunOnce(() =>
            {
                if (generation == _generation && _hook != IntPtr.Zero && _shown()) Reassert();
            }, delay);
        }
    }

    private void Reassert()
    {
        var hwnd = _hwnd();
        if (hwnd == IntPtr.Zero || !MiniPlayerPin.IsSupported) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _generation++;
    }

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
