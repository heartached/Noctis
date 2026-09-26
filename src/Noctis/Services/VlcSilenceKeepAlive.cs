using LibVLCSharp.Shared;
using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>
/// macOS/Linux counterpart to <see cref="WasapiSilenceKeepAlive"/>, ON by
/// default only in the Linux AppImage; elsewhere opt-in via NOCTIS_KEEPALIVE=1
/// (see <see cref="ShouldStartKeepAlive"/> for why). Holds a
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
/// OS audio power request is released, and resumes on NotifyActivity(). On Linux
/// a paused track holds it instead of letting it park (see <see cref="ShouldRun"/>).
/// All play/stop happens on the worker thread, never inside a VLC event handler.
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
    private readonly Func<bool>? _holdWhilePaused;
    private long _lastActivityTicks;
    private volatile bool _disposed;
    private volatile bool _suspended;
    private volatile bool _running;

    /// <param name="isPaused">True while a track sits paused and can be resumed; on Linux
    /// the loop holds through the pause instead of idle-parking (see <see cref="ShouldRun"/>).</param>
    public static VlcSilenceKeepAlive? TryStart(LibVLC libVlc, Func<bool>? isPaused = null)
    {
        if (OperatingSystem.IsWindows()) return null; // Windows uses WasapiSilenceKeepAlive
        if (!ShouldStartKeepAlive(
                OperatingSystem.IsLinux(),
                Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE"),
                Environment.GetEnvironmentVariable("NOCTIS_BUNDLED_VLC")))
            return null;
        try { return new VlcSilenceKeepAlive(libVlc, isPaused); }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "VlcKeepAlive.StartFailed",
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether the silent loop runs (non-Windows). NOCTIS_KEEPALIVE=1 forces it on,
    /// =0 forces it off; otherwise it runs only in the Linux AppImage
    /// (NOCTIS_BUNDLED_VLC=1, exported by its AppRun).
    /// Linux AppImage: on PipeWire the audio cut out twice (~500 ms) on the first
    /// play after startup and again after idle, and NOCTIS_KEEPALIVE=1 fixed both
    /// (GitHub #70). The two old Linux blockers don't apply there: the
    /// stream-restore poisoning (playback started muted) was fixed by never writing
    /// Mute/Volume on this player (see StartSilence), and the bundle ships the full
    /// plugin set, so silence.wav always opens.
    /// Linux system libvlc: stays opt-in — with an incomplete plugin set the looped
    /// silence.wav can't even be opened, spamming "VLC is unable to open the MRL
    /// '...silence.wav'" at every launch (issue #26, Arch's split VLC packaging).
    /// macOS: stays opt-in — running this second looping aout stream alongside real
    /// playback corrupts audible output on CoreAudio — repeating channel-alternating
    /// distortion + dropouts (Apple Silicon, VLC.app libvlc, first real-hardware
    /// report 2026-07-16).
    /// Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static bool ShouldStartKeepAlive(bool isLinux, string? keepAliveEnv, string? bundledVlcEnv)
        => keepAliveEnv == "1" || (keepAliveEnv != "0" && isLinux && bundledVlcEnv == "1");

    /// <summary>
    /// Whether the silent loop should be streaming. Exclusive output
    /// (<see cref="SetSuspended"/>) always parks it; otherwise it parks once
    /// <paramref name="idleForMs"/> passes <paramref name="idleStopMs"/> (0 = never),
    /// unless a paused track holds it (<paramref name="heldByPause"/>, Linux only).
    /// GitHub #70: Pause() corks VLC's PulseAudio stream and PipeWire treats a corked
    /// stream as inactive, so during a pause this loop is all that keeps the sink
    /// running. The pause also stops the position-timer heartbeat, so 10 min into the
    /// pause the loop parked, WirePlumber suspended the idle sink 5 s later, and Resume()
    /// uncorked against a suspended sink: the same two ~500 ms dropouts as a cold
    /// first play. Resume's NotifyActivity() can't prevent that — the loop restarts
    /// on this worker and LibVLC's Play() is asynchronous, while the resume worker
    /// uncorks straight away. A paused track can resume at any moment, so a pause
    /// doesn't count as idle. Pure; internal for tests.
    /// </summary>
    internal static bool ShouldRun(bool suspended, bool heldByPause, int idleStopMs, long idleForMs)
        => !suspended && (heldByPause || idleStopMs <= 0 || idleForMs <= idleStopMs);

    private VlcSilenceKeepAlive(LibVLC libVlc, Func<bool>? isPaused)
    {
        _idleStopMs = int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_KEEPALIVE_IDLE_MS"), out var ms) && ms >= 0
            ? ms : DefaultIdleStopMs;
        // Linux only (GitHub #70, see ShouldRun): macOS (opt-in) keeps the plain idle
        // park, so a long pause there still releases the output after 10 min.
        _holdWhilePaused = OperatingSystem.IsLinux() ? isPaused : null;

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

                var shouldRun = ShouldRun(
                    _suspended,
                    _holdWhilePaused?.Invoke() == true,
                    _idleStopMs,
                    Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));

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
