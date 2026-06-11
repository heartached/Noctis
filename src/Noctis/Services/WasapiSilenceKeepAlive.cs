using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Noctis.Services;

/// <summary>
/// Windows-only fix for the first-play-after-launch stutter (Discord report,
/// follow-up to issue #3).
///
/// Failure mode: when no app holds an audio stream, Windows lets the audio
/// engine / endpoint go idle (USB interfaces and Bluetooth links especially).
/// The very first WASAPI stream LibVLC's mmdevice output opens then pays the
/// device spin-up cost, which desyncs mmdevice's output clock at stream start —
/// the same permanent "playback too late → flushing buffers" spiral documented
/// for the restart-seek case in VlcAudioPlayer. The second Play() rebuilds the
/// output against a now-warm engine, so it's always clean. Confirmed by the
/// reporter's observation that keeping ANOTHER audio app open (Qobuz) fully
/// suppresses the stutter: that app's open stream keeps the engine warm.
///
/// Fix: hold our own silent shared-mode render stream (buffers submitted with
/// AUDCLNT_BUFFERFLAGS_SILENT — no PCM is ever written) so the engine and the
/// endpoint are already running before LibVLC opens its stream. Same technique
/// as foobar2000's "keep device open" / SoundKeeper.
///
/// Behavior:
///   - Starts at player construction (app launch counts as activity, covering
///     the reported first-play case) and parks after 10 min without playback
///     activity. A running stream holds the OS "audio stream is in use" power
///     request, which would otherwise block laptop auto-sleep while Noctis sits
///     idle — parking releases it. Any playback activity resumes the stream.
///   - Follows default-device changes (polled; also recreated when the device
///     is invalidated, e.g. unplugged).
///   - Uses its own audio-session GUID, distinct from LibVLC's, and publishes
///     its session instance id so WindowsSessionVolume can exclude it — the
///     user's volume must never land on the silent stream.
///
/// Env gates (A/B testing on real hardware, matching NOCTIS_CACHING et al.):
///   NOCTIS_KEEPALIVE=0        — disable entirely
///   NOCTIS_KEEPALIVE_IDLE_MS  — idle park timeout in ms (0 = never park)
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WasapiSilenceKeepAlive : IDisposable
{
    private const int BufferMs = 200;
    private const int FillIntervalMs = 50;
    private const int DeviceCheckIntervalMs = 5000;
    private const int ErrorRetryDelayMs = 3000;
    private const int DefaultIdleStopMs = 10 * 60 * 1000;

    private const uint CLSCTX_ALL = 23;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    // Distinct session GUID so this stream never joins LibVLC's audio session;
    // paired with the instance-id exclusion in WindowsSessionVolume.
    private static readonly Guid KeepAliveSessionGuid = new("b7d2a9c4-6e8f-4a31-9d5c-2f0e7b4c8a19");

    private static string? _sessionInstanceId;
    /// <summary>Session instance id of the live keep-alive stream so
    /// WindowsSessionVolume can exclude it. Null while no stream exists.</summary>
    internal static string? SessionInstanceId => Volatile.Read(ref _sessionInstanceId);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly int _idleStopMs;
    private long _lastActivityTicks;
    private volatile bool _disposed;
    private volatile bool _suspended;

    public static WasapiSilenceKeepAlive? TryStart()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE") == "0") return null;
        try { return new WasapiSilenceKeepAlive(); }
        catch { return null; }
    }

    private WasapiSilenceKeepAlive()
    {
        _idleStopMs = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE_IDLE_MS"), out var ms) && ms >= 0
            ? ms : DefaultIdleStopMs;
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NoctisAudioKeepAlive",
            Priority = ThreadPriority.BelowNormal, // renders only silence; never timing-critical
        };
        _thread.Start();
    }

    /// <summary>Mark playback activity: refreshes the idle deadline and wakes the
    /// stream if it was parked. Called from Play/Pause/Resume/Seek and the
    /// position timer — must stay allocation-free and cheap.</summary>
    public void NotifyActivity()
    {
        if (_disposed) return;
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        if (!_wake.IsSet) _wake.Set();
    }

    /// <summary>
    /// Force-park the silent stream (used while WASAPI exclusive output holds the
    /// endpoint — a concurrent shared stream is pointless there and some drivers
    /// dislike it). Resuming follows the normal activity rules.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (_disposed) return;
        _suspended = suspended;
        if (!suspended) NotifyActivity();
    }

    private void Run()
    {
        while (!_disposed)
        {
            var hadError = false;
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioRenderClient? render = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
                var deviceId = GetDeviceId(device);

                var clientIid = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientIid, CLSCTX_ALL, IntPtr.Zero, out var clientObj));
                client = (IAudioClient)clientObj;

                Check(client.GetMixFormat(out var mixFormat));
                try
                {
                    var sessionGuid = KeepAliveSessionGuid;
                    Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, 0,
                        BufferMs * 10_000L, 0, mixFormat, ref sessionGuid));
                }
                finally { Marshal.FreeCoTaskMem(mixFormat); }

                Check(client.GetBufferSize(out var bufferFrames));
                var renderIid = typeof(IAudioRenderClient).GUID;
                Check(client.GetService(ref renderIid, out var renderObj));
                render = (IAudioRenderClient)renderObj;

                // Publish our session instance id BEFORE the stream starts so the
                // volume resolver can never observe an unexcluded active session.
                Volatile.Write(ref _sessionInstanceId, TryGetSessionInstanceId(client));

                Check(client.Start());
                var running = true;
                var lastDeviceCheck = Environment.TickCount64;
                DebugLogger.Info(DebugLogger.Category.Playback, "KeepAlive.Started",
                    $"bufferFrames={bufferFrames}, idleStopMs={_idleStopMs}");

                while (!_disposed)
                {
                    if (running)
                    {
                        if (_suspended ||
                            (_idleStopMs > 0 &&
                             Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks) > _idleStopMs))
                        {
                            // Park: stop the stream so the OS audio power request is
                            // released (allows system auto-sleep) and the endpoint may
                            // suspend. Resumed on the next playback activity.
                            Check(client.Stop());
                            running = false;
                            _wake.Reset();
                            DebugLogger.Info(DebugLogger.Category.Playback, "KeepAlive.Parked");
                            continue;
                        }

                        Check(client.GetCurrentPadding(out var padding));
                        var frames = bufferFrames - padding;
                        if (frames > 0)
                        {
                            Check(render.GetBuffer(frames, out _));
                            Check(render.ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT));
                        }

                        Thread.Sleep(FillIntervalMs);

                        var now = Environment.TickCount64;
                        if (now - lastDeviceCheck >= DeviceCheckIntervalMs)
                        {
                            lastDeviceCheck = now;
                            if (DefaultDeviceChanged(enumerator, deviceId))
                            {
                                DebugLogger.Info(DebugLogger.Category.Playback, "KeepAlive.DeviceChanged");
                                break; // tear down and recreate on the new default device
                            }
                        }
                    }
                    else
                    {
                        // Parked: wait for activity. The recency check (not just the
                        // event) decides resumption, so a Reset can never eat a wake.
                        if (_wake.Wait(1000))
                            _wake.Reset();
                        if (_disposed) break;
                        if (_suspended)
                            continue;
                        if (_idleStopMs > 0 &&
                            Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks) > _idleStopMs)
                            continue;

                        if (DefaultDeviceChanged(enumerator, deviceId))
                            break; // default moved while parked — recreate there

                        Check(client.Start());
                        running = true;
                        lastDeviceCheck = Environment.TickCount64;
                        DebugLogger.Info(DebugLogger.Category.Playback, "KeepAlive.Resumed");
                    }
                }
            }
            catch (Exception ex)
            {
                // Device invalidated/unplugged, no endpoint present, or transient COM
                // failure — recreate after a delay. Must never affect playback.
                hadError = true;
                DebugLogger.Warn(DebugLogger.Category.Playback, "KeepAlive.Error",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _sessionInstanceId, null);
                TryRelease(render);
                TryRelease(client);
                TryRelease(device);
                TryRelease(enumerator);
            }

            if (_disposed) break;
            if (hadError)
                Thread.Sleep(ErrorRetryDelayMs);
        }

        _wake.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _wake.Set(); } catch { } // unblock a parked worker so it can exit
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static string? GetDeviceId(IMMDevice device)
    {
        if (device.GetId(out var ptr) < 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    private static bool DefaultDeviceChanged(IMMDeviceEnumerator enumerator, string? currentId)
    {
        if (currentId == null) return false;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) < 0 || device == null)
                return false;
            try { return !string.Equals(GetDeviceId(device), currentId, StringComparison.Ordinal); }
            finally { TryRelease(device); }
        }
        catch
        {
            return false; // enumeration hiccup — don't churn the stream over it
        }
    }

    // GetService is documented for IID_IAudioSessionControl; the cast below QIs
    // the returned object up to IAudioSessionControl2.
    private static readonly Guid IidAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");

    private static string? TryGetSessionInstanceId(IAudioClient client)
    {
        try
        {
            var iid = IidAudioSessionControl;
            if (client.GetService(ref iid, out var obj) < 0 || obj is not IAudioSessionControl2 ctl)
                return null;
            try
            {
                if (ctl.GetSessionInstanceIdentifier(out var ptr) < 0 || ptr == IntPtr.Zero)
                    return null;
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            finally { TryRelease(ctl); }
        }
        catch
        {
            return null; // exclusion becomes inactive; resolver still prefers VLC's session
        }
    }

    private static void TryRelease(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    // ── Minimal Core Audio COM interop (same pattern as WindowsSessionVolume:
    // full vtable in declaration order, unused slots stubbed, PreserveSig) ──

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

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
        [PreserveSig] int GetId(out IntPtr ppstrId);
        [PreserveSig] int GetState();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags,
            long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency();
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported();
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle();
        [PreserveSig] int GetService(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState();
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
}
