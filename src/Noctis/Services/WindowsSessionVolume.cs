using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Noctis.Services;

/// <summary>
/// Controls THIS process's Windows audio-session volume via ISimpleAudioVolume.
///
/// Why this exists: LibVLC applies its volume as a block gain in its float_mixer
/// — every change is an abrupt step at a buffer boundary, audible as a crackle
/// during a continuous slider drag (confirmed via the VLC diag log; even tiny
/// 16ms steps still click). Windows' own session volume, by contrast, is ramped
/// sample-accurately by the OS — click-free, exactly like Spotify/Apple Music.
///
/// So the player pins LibVLC's volume at 100% (float_mixer never steps) and
/// routes the user's volume through here instead. Verified safe on this stack:
/// with LibVLC pinned at 100, dragging the session slider is smooth — LibVLC
/// does not re-apply external session changes through float_mixer.
///
/// We ENUMERATE the device's sessions, match our process id, and hold the SINGLE
/// ACTIVE (currently-rendering) session — writing only that one. Sessions from
/// previous track-opens otherwise accumulate, and writing them all on every ramp
/// tick produced ~10 clustered amplitude steps per tick (audible zipper on bright
/// tracks). The session only exists once audio is flowing, so resolution is lazy,
/// re-run on a short TTL and on <see cref="Invalidate"/> at track change — which
/// also follows default-device changes (wired ⇄ Bluetooth) for free.
///
/// Windows-only. <see cref="TryCreate"/> returns null on other platforms.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSessionVolume
{
    private const long RefreshTtlMs = 750;
    private const uint CLSCTX_ALL = 23;

    private readonly object _gate = new();
    private readonly uint _pid = (uint)Environment.ProcessId;
    private bool _disposed;
    private IMMDeviceEnumerator? _enumerator;
    // The single audio session we currently drive (the active render session for
    // this process). Null until audio is flowing / between resolves.
    private ISimpleAudioVolume? _activeVolume;
    private long _resolvedTicks;

    public static WindowsSessionVolume? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var inst = new WindowsSessionVolume();
            inst._enumerator = (IMMDeviceEnumerator)CoreAudioComInterop.CreateMMDeviceEnumerator();
            return inst; // sessions are resolved lazily once audio plays
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Set the volume of this process's active render session, level 0.0–1.0.
    /// Returns true if the session was set (i.e. audio is flowing and we control
    /// it); false otherwise so the caller can fall back. Writes exactly one
    /// session — never the stale ones from previous track-opens.
    /// </summary>
    public bool SetLevel(double level)
    {
        level = Math.Clamp(level, 0.0, 1.0);
        lock (_gate)
        {
            // A ramp-worker write can land just after Dispose(); without this
            // guard EnsureFresh would re-create the COM enumerator (leak).
            if (_disposed) return false;
            EnsureFresh();
            if (_activeVolume == null) return false;

            var ctx = Guid.Empty;
            try
            {
                return _activeVolume.SetMasterVolume((float)level, ref ctx) >= 0;
            }
            catch
            {
                // Session expired (e.g. device/track changed) — drop and re-resolve next call.
                ReleaseActive();
                _resolvedTicks = 0;
                return false;
            }
        }
    }

    /// <summary>
    /// Drop the cached session so the next <see cref="SetLevel"/> re-resolves.
    /// Call on track change / new media open: VLC opens a fresh session and the
    /// previous one goes inactive, so we must re-pick the active one promptly
    /// instead of waiting out the TTL.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            ReleaseActive();
            _resolvedTicks = 0;
        }
    }

    private void EnsureFresh()
    {
        if (_activeVolume != null && Environment.TickCount64 - _resolvedTicks < RefreshTtlMs)
            return;
        Resolve();
    }

    private void Resolve()
    {
        ReleaseActive();

        var matched = 0;
        var active = 0;
        var excluded = 0;
        var heldActive = false;
        // The silent keep-alive stream (WasapiSilenceKeepAlive) is a session of
        // THIS process and is Active whenever it runs — without exclusion the
        // active-preference below could hold it instead of VLC's session, and
        // the user's volume would drive silence while the music stays at 100%.
        var keepAliveId = WasapiSilenceKeepAlive.SessionInstanceId;
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)CoreAudioComInterop.CreateMMDeviceEnumerator();
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device) < 0 || device == null)
                return;

            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var mgrObj) < 0 || mgrObj is not IAudioSessionManager2 mgr)
                    return;

                try
                {
                    if (mgr.GetSessionEnumerator(out var sessions) < 0 || sessions == null)
                        return;

                    try
                    {
                        if (sessions.GetCount(out var count) < 0) return;

                        ISimpleAudioVolume? chosen = null;
                        var chosenActive = false;

                        for (var i = 0; i < count; i++)
                        {
                            if (sessions.GetSession(i, out var ctlObj) < 0 || ctlObj == null)
                                continue;

                            var keep = false;
                            try
                            {
                                if (ctlObj is IAudioSessionControl2 ctl2 &&
                                    ctl2.GetProcessId(out var spid) >= 0 && spid == _pid &&
                                    ctlObj is ISimpleAudioVolume vol &&
                                    !IsKeepAliveSession(ctl2, keepAliveId, ref excluded))
                                {
                                    matched++;
                                    var isActive = ctl2.GetState(out var state) >= 0 &&
                                                   state == AudioSessionState.Active;
                                    if (isActive) active++;

                                    // Hold exactly one session, preferring the active
                                    // (currently-rendering) one. Upgrading from a matched-
                                    // but-inactive pick to the active one drops the stale
                                    // accumulation that caused the multi-write zipper.
                                    if (chosen == null || (isActive && !chosenActive))
                                    {
                                        if (chosen != null) TryRelease(chosen);
                                        chosen = vol;
                                        chosenActive = isActive;
                                        keep = true;
                                    }
                                }
                            }
                            finally
                            {
                                if (!keep) TryRelease(ctlObj);
                            }
                        }

                        _activeVolume = chosen;
                        heldActive = chosenActive;
                        _resolvedTicks = Environment.TickCount64;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessions);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(mgr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            // Leave _activeVolume as-is (possibly null) — caller falls back.
        }

        // Verification signal in noctis_vlc_diag.log: with the fix, held should be
        // 1 (one write per ramp tick) even when many stale sessions are matched,
        // and excluded should be 1 while the silent keep-alive stream exists.
        DebugLogger.Info(DebugLogger.Category.Playback, "SessionVolume.Resolve",
            $"matched={matched}, active={active}, excluded={excluded}, held={(_activeVolume != null ? 1 : 0)}, heldActive={heldActive}");
    }

    /// <summary>True if this session is the silent keep-alive stream's session
    /// (compared by session instance identifier). Failure to read the identifier
    /// counts as "not the keep-alive" so volume control keeps working.</summary>
    private static bool IsKeepAliveSession(IAudioSessionControl2 ctl, string? keepAliveId, ref int excluded)
    {
        if (keepAliveId == null) return false;
        try
        {
            if (ctl.GetSessionInstanceIdentifier(out var ptr) < 0 || ptr == IntPtr.Zero)
                return false;
            try
            {
                if (!string.Equals(Marshal.PtrToStringUni(ptr), keepAliveId, StringComparison.Ordinal))
                    return false;
                excluded++;
                return true;
            }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseActive()
    {
        if (_activeVolume == null) return;
        TryRelease(_activeVolume);
        _activeVolume = null;
    }

    private static void TryRelease(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseActive();
            TryRelease(_enumerator);
            _enumerator = null;
        }
    }

    // ── Minimal Core Audio COM interop ──────────────────────────────
    // Each interface declares its full vtable in order; unused methods are
    // stubbed parameterless (slot position is by declaration order, and they're
    // never invoked). All use PreserveSig and return the HRESULT.

    // The MMDeviceEnumerator coclass is activated via CoreAudioComInterop
    // (Type.GetTypeFromCLSID) rather than a private [ComImport] coclass, so it
    // can't collide with the other Core Audio consumers' coclasses for the same
    // CLSID. See CoreAudioComInterop for the full rationale.

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }
    private enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        [PreserveSig] int GetDevice();
        [PreserveSig] int RegisterEndpointNotificationCallback();
        [PreserveSig] int UnregisterEndpointNotificationCallback();
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore();
        [PreserveSig] int GetId();
        [PreserveSig] int GetState();
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl();
        [PreserveSig] int GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        [PreserveSig] int RegisterSessionNotification();
        [PreserveSig] int UnregisterSessionNotification();
        [PreserveSig] int RegisterDuckNotification();
        [PreserveSig] int UnregisterDuckNotification();
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount,
            [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out AudioSessionState pRetVal);
        [PreserveSig] int GetDisplayName();
        [PreserveSig] int SetDisplayName();
        [PreserveSig] int GetIconPath();
        [PreserveSig] int SetIconPath();
        [PreserveSig] int GetGroupingParam();
        [PreserveSig] int SetGroupingParam();
        [PreserveSig] int RegisterAudioSessionNotification();
        [PreserveSig] int UnregisterAudioSessionNotification();
        [PreserveSig] int GetSessionIdentifier();
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute(int bMute, ref Guid EventContext);
        [PreserveSig] int GetMute(out int pbMute);
    }
}
