using LibVLCSharp.Shared;
using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>
/// macOS/Linux counterpart to <see cref="WasapiSilenceKeepAlive"/>. Holds a
/// private <see cref="MediaPlayer"/> looping a generated silent WAV so the
/// native audio device endpoint stays open. The player is deliberately NOT
/// muted or volume-zeroed: the source is silent anyway, and on PulseAudio /
/// PipeWire those writes poison the app-wide stream-restore entry that real
/// playback streams inherit (see StartSilence). The main player's first
/// Play() and every Stop()->Play() transition then open against a running device
/// instead of cold-dropping the first buffers (the track-start clip). Windows
/// keeps WasapiSilenceKeepAlive (a real silent WASAPI stream); this path relies
/// on the OS mixing one extra shared stream to keep the endpoint live — true on
/// CoreAudio / PulseAudio / PipeWire / ALSA-dmix (verified by ear, not by tests).
///
/// Mirrors the Windows behavior: streams from construction (covers first play),
/// idle-parks via Stop() after NOCTIS_KEEPALIVE_IDLE_MS (default 10 min) so the
/// OS audio power request is released, and resumes on NotifyActivity(). All
/// play/stop happens on the worker thread, never inside a VLC event handler.
/// </summary>
internal sealed class VlcSilenceKeepAlive : IAudioKeepAlive
{
    private const int DefaultIdleStopMs = 10 * 60 * 1000;
    private const int WatchdogIntervalMs = 1000;

    private readonly MediaPlayer _player;
    private readonly Media _silence;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly int _idleStopMs;
    private long _lastActivityTicks;
    private volatile bool _disposed;
    private volatile bool _suspended;
    private volatile bool _running;

    public static VlcSilenceKeepAlive? TryStart(LibVLC libVlc)
    {
        var env = Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE");
        if (env == "0") return null;
        if (OperatingSystem.IsWindows()) return null; // Windows uses WasapiSilenceKeepAlive
        // macOS: opt-in only (NOCTIS_KEEPALIVE=1). Running this second looping
        // aout stream alongside real playback corrupts audible output on
        // CoreAudio — repeating channel-alternating distortion + dropouts
        // (Apple Silicon, VLC.app libvlc, first real-hardware report 2026-07-16).
        // CoreAudio doesn't cold-drop first buffers the way WASAPI does, so the
        // keep-alive's benefit there is unproven while the cost is unlistenable
        // playback; Linux keeps it (verified working over PulseAudio/PipeWire).
        if (OperatingSystem.IsMacOS() && env != "1") return null;
        try { return new VlcSilenceKeepAlive(libVlc); }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "VlcKeepAlive.StartFailed",
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private VlcSilenceKeepAlive(LibVLC libVlc)
    {
        _idleStopMs = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE_IDLE_MS"), out var ms) && ms >= 0
            ? ms : DefaultIdleStopMs;

        var path = SilentWavFile.EnsureCached(AppPaths.DataRoot);
        _silence = new Media(libVlc, path, FromType.FromPath);
        // Loop the clip in-process so the device never closes between repeats; the
        // worker's watchdog restarts it if VLC ever ends/stops it anyway.
        _silence.AddOption(":input-repeat=65535");

        _player = new MediaPlayer(libVlc);
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        StartSilence();

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NoctisVlcKeepAlive",
            Priority = ThreadPriority.BelowNormal, // renders only silence; never timing-critical
        };
        _thread.Start();
        DebugLogger.Info(DebugLogger.Category.Playback, "VlcKeepAlive.Started", $"idleStopMs={_idleStopMs}");
    }

    public void NotifyActivity()
    {
        if (_disposed) return;
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        if (!_wake.IsSet) _wake.Set();
    }

    public void SetSuspended(bool suspended)
    {
        if (_disposed) return;
        _suspended = suspended;
        if (!suspended) NotifyActivity();
    }

    private void StartSilence()
    {
        // Never set Mute/Volume on this player. The WAV is already silent, and on
        // PulseAudio/PipeWire the stream shares the app's stream-restore entry with
        // real playback (same application identity) — a mute/volume-0 write here is
        // saved by the OS and re-applied to the NEXT playback stream, which is how
        // Linux ended up starting muted / intermittently silent.
        _player.Play(_silence);
        _running = true;
    }

    private void Run()
    {
        while (!_disposed)
        {
            try
            {
                _wake.Wait(WatchdogIntervalMs);
                _wake.Reset();
                if (_disposed) break;

                var idleExceeded = _idleStopMs > 0 &&
                    Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks) > _idleStopMs;
                var shouldRun = !_suspended && !idleExceeded;

                if (shouldRun)
                {
                    if (!_running || !_player.IsPlaying)
                    {
                        StartSilence();
                        DebugLogger.Info(DebugLogger.Category.Playback, "VlcKeepAlive.Resumed");
                    }
                }
                else if (_running)
                {
                    _player.Stop();
                    _running = false;
                    DebugLogger.Info(DebugLogger.Category.Playback, "VlcKeepAlive.Parked");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "VlcKeepAlive.Error",
                    $"{ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(WatchdogIntervalMs); // never spin on a persistent fault
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _wake.Set(); } catch { }
        try { _thread.Join(2000); } catch { }
        try { _player.Stop(); } catch { }
        try { _player.Dispose(); } catch { }
        try { _silence.Dispose(); } catch { }
        try { _wake.Dispose(); } catch { }
    }
}
