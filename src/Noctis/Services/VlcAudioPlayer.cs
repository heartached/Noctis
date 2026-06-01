using System.Diagnostics;
using System.Runtime.InteropServices;
using Noctis.Models;
using LibVLCSharp.Shared;

namespace Noctis.Services;

/// <summary>
/// LibVLC-based implementation of IAudioPlayer.
/// Manages a single LibVLC instance and MediaPlayer for the application lifetime.
///
/// Threading rules:
///   - VLC fires EndReached/EncounteredError on its own internal thread.
///   - You MUST NOT call Play/Stop/Pause from inside those handlers (deadlock).
///   - All VLC state-changing calls go through ThreadPool to avoid blocking UI.
///   - A SemaphoreSlim serializes Play/Stop to prevent overlapping operations.
/// </summary>
public class VlcAudioPlayer : IAudioPlayer
{
    private const int SeekThrottleMs = 50;
    // Throttled volume write. Volume goes to player.Volume, which on
    // --aout=waveout calls waveOutSetVolume — a driver-level write that does
    // not fire WASAPI session events, so it is click-free even during
    // continuous slider drag. The 150ms throttle remains a useful coalescer
    // for trailing writes; CommitVolume (pointer-release) flushes the final
    // exact value immediately. _applyingVolume breaks the reentrance feedback
    // loop: setting _player.Volume can fire MediaPlayer.VolumeChanged, which
    // (if observed) can re-enter the public Volume setter and chain rapid
    // writes — Interlocked.CompareExchange guards every direct write.
    private const int VolumeThrottleMs = 150;
    private const int VolumeDeadband = 1;
    private const int EndReachedGraceMs = 1200;
    private const int FadeStepMs = 35;
    private const int StandbyWarmupTimeoutMs = 650;
    private const int StandbyWarmupPollMs = 25;
    private const int DeferredCleanupDelayMs = 1000;
    private const double DualFadeHeadroom = 0.88;

    private readonly LibVLC _libVlc;
    private MediaPlayer _player;
    private MediaPlayer _standbyPlayer;
    private Media? _currentMedia;
    private Media? _standbyMedia;
    private string? _standbyPath;
    private long _standbyStartPositionMs = -1;
    private long _standbyPreparedTicksUtc;
    private bool _standbyPrepared;
    private bool _disposed;

    // Serializes Play/Stop operations so rapid track switching
    // (e.g. spamming Next) doesn't overlap Stop+Play calls.
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    // Timer for polling position (~4Hz for smooth seek bar updates).
    // More reliable than VLC's PositionChanged event, which fires
    // inconsistently on some codecs (M4A/ALAC in particular).
    private readonly System.Timers.Timer _positionTimer;

    // Coalesce rapid seek requests so VLC isn't hammered by timeline scrubbing.
    private readonly object _seekGate = new();
    private long _latestSeekMs = -1;
    private int _seekWorkerActive;
    private long _lastAppliedSeekTicksUtc;

    // Volume write state.
    //   _applyingVolume:        0 = idle, 1 = a write is currently in progress.
    //                           Used by SetPlayerVolumeGuarded to skip reentrant writes
    //                           triggered by MediaPlayer.VolumeChanged → ViewModel → setter.
    //   _pendingVolumeTarget:   the most-recent target requested while throttled
    //                           (-1 = none pending).
    //   _lastVolumeWriteTicks:  Stopwatch ticks of the last successful write.
    //   _volumeTrailingCts:     cancellation for the scheduled trailing write.
    //   _lastWrittenVolume:     deadband baseline (last value handed to the player).
    private int _applyingVolume;
    private volatile int _pendingVolumeTarget = -1;
    private long _lastVolumeWriteTicks;
    private int _lastWrittenVolume = -1;
    private CancellationTokenSource? _volumeTrailingCts;
    private readonly object _volumeWriteLock = new();
    private long _lastDualFadeTickMs;
    private int _slowDualFadeTicks;

    // EndReached can fire before the final output buffer is fully audible.
    // Keep lyrics/UI alive briefly, then raise TrackEnded once the grace window passes.
    private long _endReachedDeadlineTicksUtc;
    private long _endReachedSessionId;

    // VLC's _player.Length can return 0 after EndReached (media considered "finished").
    // Store the last known good value so end-of-track position updates always reach
    // the true duration — otherwise lyrics/UI stop updating early.
    private long _lastKnownLengthMs;

    // Track paused state ourselves because VLC's MediaPlayer
    // does not expose a reliable IsPaused property.
    private volatile bool _isPaused;

    private long _playbackSessionId;
    private long _lastPlayStartTicksUtc;

    // Sound enhancer state
    private readonly object _equalizerLock = new();
    private Equalizer? _equalizer;
    private bool _soundEnhancerEnabled;
    private int _soundEnhancerLevel = 50;

    // Advanced equalizer state
    private bool _advancedEqEnabled;
    private int _advancedEqPresetIndex;
    private float[] _advancedEqBands = new float[10];
    private float _advancedEqPreamp;
    private int _appliedAdvancedEqPresetIndex = -2;
    private long _advancedEqRequestVersion;
    private long _advancedEqAppliedVersion;
    private int _advancedEqApplyQueued;

    // Normalization state
    private bool _normalizationEnabled;

    // Crossfade state
    private bool _crossfadeEnabled;
    private int _crossfadeDurationMs = 6000;
    private AutoMixFadeCurve _crossfadeFadeCurve = AutoMixFadeCurve.SmoothEase;

    // Pending seek — applied inside PlayInternal after _player.Play() to avoid race
    private long _pendingSeekMs = -1;

    // Skip cancellation — cancelled when a new Play() is requested so any
    // in-progress fade or parse aborts immediately for instant track switching.
    private CancellationTokenSource _skipCts = new();

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;

    public VlcAudioPlayer()
    {
        try
        {
            // On macOS the VideoLAN.LibVLC.Mac NuGet has shifting layouts between
            // versions; if VLC.app is installed (recommended path), point the
            // loader at its dylibs directly so playback works regardless of
            // which package version restore picked. libvlc also needs to find
            // its plugins folder, which it cannot locate on its own when loaded
            // from outside an .app bundle — set VLC_PLUGIN_PATH explicitly.
            var macLibPath = TryFindMacLibVlcPath();
            if (macLibPath != null)
            {
                var pluginsPath = Path.Combine(Path.GetDirectoryName(macLibPath) ?? "", "plugins");
                if (Directory.Exists(pluginsPath) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")))
                {
                    // libvlc reads VLC_PLUGIN_PATH via libc getenv(), and on Unix
                    // .NET's Environment.SetEnvironmentVariable does not always
                    // reach the C `environ` array that getenv() consults. Call
                    // setenv() directly so libvlc actually sees the path.
                    SetUnixEnv("VLC_PLUGIN_PATH", pluginsPath);
                }
                Core.Initialize(macLibPath);
            }
            else
            {
                Core.Initialize();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   || ex is System.IO.FileNotFoundException
                                   || ex is VLCException)
        {
            // libvlc native library missing. Re-throw with a platform-tailored
            // message so users see what to install.
            throw new InvalidOperationException(BuildLibVlcMissingMessage(), ex);
        }

        // Audio-optimized flags for high-quality music playback:
        //   --audio-resampler=speex : Use Speex resampler (high quality, universally available)
        //   --speex-resampler-quality=10 : Maximum quality resampling (0=fast, 10=best)
        //   --no-video/spu        : skip video & subtitle pipelines entirely
        //   --no-audio-time-stretch: disable time-stretching that degrades quality
        //   --demux=avformat      : Force FFmpeg avformat demuxer for all audio files.
        //                           VLC's native MP3 demuxer performs a linear scan for
        //                           VBR MP3 files without a Xing/LAME seek index, causing
        //                           audible seek stutter on those tracks. FFmpeg reads the
        //                           Xing header and builds an O(1) seek table on open,
        //                           fixing per-song variation in seek quality. Also needed
        //                           for AAC/M4A Lossless seek smoothness.
        //   --aout=mmdevice: WASAPI shared-mode output, VLC's modern Windows
        //   backend. Replaces the legacy --aout=directsound path, whose
        //   DirectSound emulation underran on high-latency endpoints
        //   (Bluetooth A2DP, some USB DACs) and produced the audible stutter
        //   reported in issues #1 and #3. mmdevice also auto-follows the
        //   Windows default-device change (e.g. plugging in headphones) and
        //   runs a smaller output buffer, so EQ slider moves take effect
        //   faster.
        //
        //   CAVEAT (verify by ear on real BT / USB-DAC hardware): mmdevice
        //   routes volume writes through ISimpleAudioVolume::SetMasterVolume,
        //   which historically fired a session-volume event producing an
        //   audible click/static on continuous slider drag — the reason an
        //   earlier build moved off WASAPI. If that artifact regresses,
        //   switch this back to --aout=directsound.
        //
        // NOTE on caching: leaving VLC's input-caching at its defaults
        // (300ms file / 1500ms live / 300ms disc). A previous build forced
        // these to 50ms for snappier EQ slider response, but on Bluetooth
        // / high-latency A2DP endpoints the 50ms buffer is too tight — any
        // disk-read jitter, GC pause, or track-change cost starves the
        // output and produces stutter (re-opening of issue #1; new reports
        // in issue #3). The ~250ms EQ-drag lag is a fair tradeoff vs.
        // audible stuttering. --clock-jitter stays at the 5000ms default
        // for output-side drift tolerance. The seek-stutter fix remains
        // --demux=avformat, which builds an O(1) seek index at open time
        // regardless of caching.
        var vlcArgs = new List<string>
        {
            "--no-video",
            "--no-osd",
            "--no-spu",
            "--input-repeat=0",
            "--demux=avformat",
            "--no-audio-time-stretch",
        };
        // The speex resampler module + its quality flag are not always present
        // in third-party VLC builds (notably the macOS VLC.app distribution).
        // mmdevice is Windows-only.
        if (OperatingSystem.IsWindows())
        {
            vlcArgs.Add("--audio-resampler=speex");
            vlcArgs.Add("--speex-resampler-quality=10");
            vlcArgs.Add("--aout=mmdevice");
        }

        _libVlc = new LibVLC(vlcArgs.ToArray());

        _player = new MediaPlayer(_libVlc);
        _standbyPlayer = new MediaPlayer(_libVlc);

        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnError;
        _standbyPlayer.EndReached += OnEndReached;
        _standbyPlayer.EncounteredError += OnError;

        _positionTimer = new System.Timers.Timer(100);
        _positionTimer.Elapsed += OnPositionTimerElapsed;
        _positionTimer.AutoReset = true;
    }

    // ── Properties ──────────────────────────────────────────────

    public PlaybackState State
    {
        get
        {
            if (_disposed) return PlaybackState.Stopped;
            if (_player.IsPlaying) return PlaybackState.Playing;
            if (_isPaused) return PlaybackState.Paused;
            return PlaybackState.Stopped;
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_disposed) return TimeSpan.Zero;
            var len = _player.Length;
            return len > 0 ? TimeSpan.FromMilliseconds(len) : TimeSpan.Zero;
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_disposed) return TimeSpan.Zero;
            var time = _player.Time;
            if (time <= 0) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(time);
        }
    }

    public long CurrentSessionId => Interlocked.Read(ref _playbackSessionId);

    // Store the user-facing volume (0–100) separately from VLC's internal volume,
    // because we apply a logarithmic curve to make low volumes audible.
    private int _userVolume = 75;
    private int _volumeAdjust;

    // ── ReplayGain ──
    // Linear multiplier applied on top of the curved VLC volume so RG-aware
    // playback can attenuate or boost without changing the user's slider.
    // 1.0 = bypass. Updated by ApplyReplayGain().
    private double _replayGainScalar = 1.0;
    private string? _currentMediaPath;
    private string _rgMode = "Off";
    private double _rgPreampDb = 0.0;

    public string? CurrentMediaPath => _currentMediaPath;

    public int Volume
    {
        get => _userVolume;
        set
        {
            if (_disposed) return;
            _userVolume = Math.Clamp(value, 0, 100);
            if (_crossfadeEnabled && _currentMedia != null)
                return;

            var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
            target = ApplyReplayGainScalar(target);
            ScheduleVolumeWrite(target);
        }
    }

    public int VolumeAdjust
    {
        get => _volumeAdjust;
        set
        {
            _volumeAdjust = Math.Clamp(value, -100, 100);
            if (_crossfadeEnabled && _currentMedia != null)
                return;

            // Re-apply volume with the new adjustment
            var effective = Math.Clamp(_userVolume + _volumeAdjust, 0, 100);
            var target = ApplyVolumeCurve(effective);
            target = ApplyReplayGainScalar(target);
            ScheduleVolumeWrite(target);
        }
    }

    public long PendingSeekMs
    {
        get => _pendingSeekMs;
        set => _pendingSeekMs = value;
    }

    /// <summary>
    /// True while a guarded write to _player.Volume is in progress. Exposed so the
    /// ViewModel can ignore its own MediaPlayer.VolumeChanged echo and avoid the
    /// reentrance feedback loop (VLC → ViewModel → setter → VLC → …).
    /// </summary>
    public bool IsApplyingVolume => Volatile.Read(ref _applyingVolume) == 1;

    /// <summary>
    /// Applies the final volume to VLC immediately, bypassing the throttle.
    /// Call on drag-end / pointer-release so the exact target is applied. Clears
    /// any pending trailing write and resets the throttle deadline so subsequent
    /// drag motion isn't held up by the just-applied write's cooldown.
    /// </summary>
    public void CommitVolume()
    {
        if (_disposed) return;
        if (_crossfadeEnabled && _currentMedia != null)
            return;

        var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
        target = ApplyReplayGainScalar(target);
        lock (_volumeWriteLock)
        {
            _volumeTrailingCts?.Cancel();
            _volumeTrailingCts = null;
            _pendingVolumeTarget = -1;
            SetPlayerVolumeGuarded(_player, target);
            _lastWrittenVolume = target;
            _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
        }
    }

    public bool IsMuted
    {
        get => !_disposed && _player.Mute;
        set
        {
            if (_disposed) return;
            _player.Mute = value;
            if (_standbyPrepared)
                _standbyPlayer.Mute = value;
        }
    }

    /// <summary>
    /// Applies a perceptual curve so that low volume levels remain audible
    /// and the full range feels smooth and consistent.
    /// Uses x^0.5 (square root) — gentler than the old x^0.4 curve,
    /// producing smaller VLC jumps per slider unit and fewer audible
    /// discontinuities during drag.
    /// </summary>
    private static int ApplyVolumeCurve(int userVolume)
    {
        if (userVolume <= 0) return 0;
        if (userVolume >= 100) return 100;

        double normalized = userVolume / 100.0;
        double curved = Math.Pow(normalized, 0.5);
        return (int)Math.Round(curved * 100);
    }

    /// <summary>
    /// Wraps every write to a MediaPlayer.Volume with a one-shot Interlocked guard so
    /// the ViewModel's MediaPlayer.VolumeChanged callback can't reenter the public
    /// Volume setter and chain rapid writes. Concurrent reentrant calls fall through
    /// silently — the outer call is the one that actually writes.
    /// </summary>
    private void SetPlayerVolumeGuarded(MediaPlayer player, int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (Interlocked.CompareExchange(ref _applyingVolume, 1, 0) != 0)
            return; // reentrant write from VolumeChanged echo — drop it
        try { player.Volume = clamped; }
        catch { /* player disposed / transitioning */ }
        finally { Interlocked.Exchange(ref _applyingVolume, 0); }
    }

    /// <summary>
    /// Debounced/throttled volume write. Inside the cooldown we cache the target
    /// and schedule a single trailing write for the remaining time; outside the
    /// cooldown we write immediately. Deadband skips writes where the mapped VLC
    /// value didn't change from the last successful write.
    /// </summary>
    private void ScheduleVolumeWrite(int target)
    {
        target = Math.Clamp(target, 0, 100);
        lock (_volumeWriteLock)
        {
            if (_disposed) return;

            // Deadband: skip if nothing changed from the last value handed to VLC.
            if (_lastWrittenVolume >= 0 && Math.Abs(target - _lastWrittenVolume) < VolumeDeadband)
            {
                _pendingVolumeTarget = target; // CommitVolume will still flush
                return;
            }

            var nowTicks = Stopwatch.GetTimestamp();
            var elapsedMs = _lastVolumeWriteTicks == 0
                ? long.MaxValue
                : (nowTicks - _lastVolumeWriteTicks) * 1000L / Stopwatch.Frequency;

            if (elapsedMs >= VolumeThrottleMs)
            {
                // Cooldown expired — apply immediately. Cancel any in-flight
                // trailing CTS so it can't double-write at the boundary.
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
                SetPlayerVolumeGuarded(_player, target);
                _lastWrittenVolume = target;
                _lastVolumeWriteTicks = nowTicks;
                return;
            }

            // Inside cooldown — schedule a single trailing write for the remainder.
            _pendingVolumeTarget = target;
            _volumeTrailingCts?.Cancel();
            var cts = new CancellationTokenSource();
            _volumeTrailingCts = cts;
            var delay = (int)(VolumeThrottleMs - elapsedMs);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { return; }

                lock (_volumeWriteLock)
                {
                    if (_disposed || cts.IsCancellationRequested) return;
                    var pending = _pendingVolumeTarget;
                    if (pending < 0) return;
                    _pendingVolumeTarget = -1;
                    SetPlayerVolumeGuarded(_player, pending);
                    _lastWrittenVolume = pending;
                    _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
                }
            });
        }
    }

    private void FadePlayerVolumeBlocking(int fromVolume, int toVolume, int durationMs, CancellationToken cancel = default)
    {
        if (_disposed) return;

        fromVolume = Math.Clamp(fromVolume, 0, 100);
        toVolume = Math.Clamp(toVolume, 0, 100);
        durationMs = Math.Max(0, durationMs);

        if (durationMs == 0 || fromVolume == toVolume)
        {
            SetPlayerVolumeGuarded(_player, toVolume);
            return;
        }

        var steps = Math.Max(1, durationMs / FadeStepMs);
        var sleepMs = Math.Max(1, durationMs / steps);

        for (var i = 1; i <= steps; i++)
        {
            if (_disposed || cancel.IsCancellationRequested)
            {
                SetPlayerVolumeGuarded(_player, toVolume);
                return;
            }
            var progress = (double)i / steps;
            var eased = AutoMixFadeMath.SmoothFadeProgress(progress);
            var next = (int)Math.Round(fromVolume + ((toVolume - fromVolume) * eased));
            SetPlayerVolumeGuarded(_player, next);

            if (i < steps)
                Thread.Sleep(sleepMs);
        }
    }

    public void SetSoundEnhancer(bool enabled, int level)
    {
        if (_disposed) return;

        _soundEnhancerEnabled = enabled;
        _soundEnhancerLevel = Math.Clamp(level, 0, 100);

        // Advanced EQ takes priority over sound enhancer
        if (_advancedEqEnabled) return;

        if (enabled)
        {
            // Create an equalizer with a subtle enhancement profile.
            // Boost lows and highs slightly to create a "wider" sound.
            lock (_equalizerLock)
            {
                _equalizer?.Dispose();
                _equalizer = new Equalizer();
                _equalizer.SetPreamp(0f);

                // Scale factor from level: 0 = no boost, 100 = full boost
                double factor = _soundEnhancerLevel / 100.0;

                // 10-band EQ: boost bass (bands 0-2) and treble (bands 7-9)
                // Band frequencies approximate: 60Hz, 170Hz, 310Hz, 600Hz, 1kHz, 3kHz, 6kHz, 12kHz, 14kHz, 16kHz
                float bassBoost = (float)(6.0 * factor);
                float trebleBoost = (float)(4.0 * factor);

                _equalizer.SetAmp(bassBoost, 0);    // 60Hz
                _equalizer.SetAmp((float)(bassBoost * 0.7), 1);  // 170Hz
                _equalizer.SetAmp((float)(bassBoost * 0.3), 2);  // 310Hz
                _equalizer.SetAmp(0f, 3);            // 600Hz (neutral)
                _equalizer.SetAmp(0f, 4);            // 1kHz (neutral)
                _equalizer.SetAmp(0f, 5);            // 3kHz (neutral)
                _equalizer.SetAmp((float)(trebleBoost * 0.3), 6);  // 6kHz
                _equalizer.SetAmp((float)(trebleBoost * 0.7), 7);  // 12kHz
                _equalizer.SetAmp(trebleBoost, 8);   // 14kHz
                _equalizer.SetAmp(trebleBoost, 9);   // 16kHz

                // Only apply to player if it's initialized
                if (_player != null)
                    _player.SetEqualizer(_equalizer);
                if (_standbyPrepared)
                    _standbyPlayer.SetEqualizer(_equalizer);
            }
        }
        else
        {
            // Disable equalizer (only if player is initialized)
            lock (_equalizerLock)
            {
                if (_player != null)
                    _player.SetEqualizer(null);
                if (_standbyPrepared)
                    _standbyPlayer.SetEqualizer(null);
                _equalizer?.Dispose();
                _equalizer = null;
            }
        }
    }

    public void SetAdvancedEqualizer(bool enabled, int presetIndex, float[] customBands)
    {
        if (_disposed) return;

        lock (_equalizerLock)
        {
            _advancedEqEnabled = enabled;
            _advancedEqPresetIndex = presetIndex;
            if (customBands is { Length: 10 })
            {
                for (var i = 0; i < 10; i++)
                    _advancedEqBands[i] = Math.Clamp(customBands[i], -12f, 12f);
            }
        }

        Interlocked.Increment(ref _advancedEqRequestVersion);
        QueueAdvancedEqualizerApply();
    }

    private void QueueAdvancedEqualizerApply()
    {
        if (Interlocked.Exchange(ref _advancedEqApplyQueued, 1) == 0)
            _ = Task.Run(ProcessAdvancedEqualizerQueue);
    }

    private void ProcessAdvancedEqualizerQueue()
    {
        try
        {
            while (!_disposed)
            {
                var version = Interlocked.Read(ref _advancedEqRequestVersion);
                ApplyAdvancedEqualizerSnapshot(version);
                Interlocked.Exchange(ref _advancedEqAppliedVersion, version);

                if (Interlocked.Read(ref _advancedEqRequestVersion) == version)
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _advancedEqApplyQueued, 0);
            if (!_disposed && Interlocked.Read(ref _advancedEqAppliedVersion) != Interlocked.Read(ref _advancedEqRequestVersion))
                QueueAdvancedEqualizerApply();
        }
    }

    private void ApplyAdvancedEqualizerSnapshot(long capturedVersion = long.MinValue)
    {
        bool enabled;
        int presetIndex;
        float[] bands;
        bool soundEnhancerEnabled;
        int soundEnhancerLevel;

        lock (_equalizerLock)
        {
            enabled = _advancedEqEnabled;
            presetIndex = _advancedEqPresetIndex;
            bands = (float[])_advancedEqBands.Clone();
            soundEnhancerEnabled = _soundEnhancerEnabled;
            soundEnhancerLevel = _soundEnhancerLevel;
        }

        if (enabled)
        {
            lock (_equalizerLock)
            {
                if (presetIndex >= 0)
                {
                    // Keep native preset behavior (including preset preamp).
                    _equalizer?.Dispose();
                    _equalizer = new Equalizer((uint)presetIndex);
                    _advancedEqPreamp = Math.Clamp(_equalizer.Preamp, -20f, 20f);
                    _appliedAdvancedEqPresetIndex = presetIndex;
                }
                else
                {
                    // Avoid rebuilding native EQ every slider tick in custom mode.
                    if (_equalizer == null || _appliedAdvancedEqPresetIndex >= 0)
                    {
                        _equalizer?.Dispose();
                        _equalizer = new Equalizer();
                    }

                    // Preserve preamp when switching from preset -> custom, which
                    // prevents sudden global volume drops while editing bands.
                    _equalizer.SetPreamp(Math.Clamp(_advancedEqPreamp, -20f, 20f));

                    for (uint i = 0; i < 10; i++)
                        _equalizer.SetAmp(bands[i], i);

                    _appliedAdvancedEqPresetIndex = presetIndex;
                }

                if (_player != null)
                    _player.SetEqualizer(_equalizer);

                // Skip the standby player update while newer EQ requests are still
                // pending (i.e. the user is actively dragging a slider). The
                // standby is silent until end-of-track crossfade, so it doesn't
                // need real-time updates — and skipping it halves the per-iteration
                // cost of the apply loop, which is the dominant source of slider
                // lag. The next loop iteration (or the final converged one) will
                // sync standby once the version stabilises.
                if (_standbyPrepared)
                {
                    var pendingNewer = capturedVersion != long.MinValue &&
                        Interlocked.Read(ref _advancedEqRequestVersion) != capturedVersion;
                    if (!pendingNewer)
                        _standbyPlayer.SetEqualizer(_equalizer);
                }
            }
        }
        else
        {
            // Disable advanced EQ. If sound enhancer is still on, re-apply it.
            if (soundEnhancerEnabled)
            {
                SetSoundEnhancer(true, soundEnhancerLevel);
            }
            else
            {
                lock (_equalizerLock)
                {
                    if (_player != null)
                        _player.SetEqualizer(null);
                    if (_standbyPrepared)
                        _standbyPlayer.SetEqualizer(null);
                    _equalizer?.Dispose();
                    _equalizer = null;
                    _appliedAdvancedEqPresetIndex = -2;
                }
            }
        }
    }

    public void SetNormalization(bool enabled)
    {
        if (_disposed) return;
        _normalizationEnabled = enabled;
        // Normalization is applied per-track via VLC audio filters.
        // The flag is stored here and applied in PlayInternal when creating new media.
    }

    public void ApplyReplayGain(string mode, double preampDb)
    {
        if (_disposed) return;
        _rgMode = string.IsNullOrWhiteSpace(mode) ? "Off" : mode;
        _rgPreampDb = preampDb;

        // Mode "Off" — bypass.
        if (string.Equals(_rgMode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(_replayGainScalar - 1.0) > 0.0001)
            {
                _replayGainScalar = 1.0;
                ReapplyVolume();
            }
            return;
        }

        // Need a loaded track to read RG tags from.
        if (string.IsNullOrEmpty(_currentMediaPath) || !File.Exists(_currentMediaPath))
        {
            _replayGainScalar = 1.0;
            ReapplyVolume();
            return;
        }

        var (track, album) = ReadReplayGainTags(_currentMediaPath);
        double? gain = _rgMode.ToLowerInvariant() switch
        {
            "track" => track,
            "album" => album ?? track,
            "auto" => album ?? track,
            _ => null,
        };

        if (gain == null)
        {
            // No tag present — bypass rather than guess.
            _replayGainScalar = 1.0;
        }
        else
        {
            // Clamp combined gain to a sane window so a corrupt tag can't blow speakers.
            var totalDb = Math.Clamp(gain.Value + preampDb, -30.0, 12.0);
            _replayGainScalar = Math.Pow(10.0, totalDb / 20.0);
        }
        ReapplyVolume();
    }

    /// <summary>Re-issue the current curved volume × RG scalar so the next
    /// audible sample reflects an updated <see cref="_replayGainScalar"/>.</summary>
    private void ReapplyVolume()
    {
        if (_crossfadeEnabled && _currentMedia != null) return;
        var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
        target = ApplyReplayGainScalar(target);
        ScheduleVolumeWrite(target);
    }

    private int ApplyReplayGainScalar(int curvedVolume)
    {
        if (Math.Abs(_replayGainScalar - 1.0) < 0.0001) return curvedVolume;
        var scaled = (int)Math.Round(curvedVolume * _replayGainScalar);
        return Math.Clamp(scaled, 0, 100);
    }

    /// <summary>Read REPLAYGAIN_TRACK_GAIN / REPLAYGAIN_ALBUM_GAIN from a file
    /// via TagLib. Returns the parsed dB value (negative for attenuation).</summary>
    private static (double? track, double? album) ReadReplayGainTags(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            double? track = null, album = null;

            if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            {
                track ??= ReadTxxx(id3, "REPLAYGAIN_TRACK_GAIN");
                album ??= ReadTxxx(id3, "REPLAYGAIN_ALBUM_GAIN");
            }
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            {
                track ??= ParseDb(xiph.GetField("REPLAYGAIN_TRACK_GAIN").FirstOrDefault());
                album ??= ParseDb(xiph.GetField("REPLAYGAIN_ALBUM_GAIN").FirstOrDefault());
            }
            // MP4 / M4A / ALAC / AAC: RG lives in iTunes freeform atoms.
            if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
            {
                track ??= ParseDb(apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_TRACK_GAIN"));
                album ??= ParseDb(apple.GetDashBox("com.apple.iTunes", "REPLAYGAIN_ALBUM_GAIN"));
            }
            return (track, album);
        }
        catch
        {
            return (null, null);
        }
    }

    private static double? ReadTxxx(TagLib.Id3v2.Tag id3, string desc)
    {
        var frame = id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
            .FirstOrDefault(f => string.Equals(f.Description, desc, StringComparison.OrdinalIgnoreCase));
        return ParseDb(frame?.Text.FirstOrDefault());
    }

    private static double? ParseDb(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var token = s.Trim();
        // RG values are stored like "-7.84 dB". Strip the unit.
        var spaceIdx = token.IndexOf(' ');
        if (spaceIdx > 0) token = token.Substring(0, spaceIdx);
        return double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    public void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase)
    {
        if (_disposed) return;
        _crossfadeEnabled = enabled;
        _crossfadeDurationMs = Math.Clamp(durationSeconds, 1, 12) * 1000;
        _crossfadeFadeCurve = fadeCurve;
    }

    public void PrepareNext(string filePath, long startPositionMs = -1)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var normalizedPath = Path.GetFullPath(filePath);
        if (_standbyPrepared &&
            string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
            _standbyStartPositionMs == startPositionMs)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                var prepareStart = Environment.TickCount64;
                DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.DualPrepareStart", $"path={Path.GetFileName(normalizedPath)}, startMs={startPositionMs}");

                if (_disposed || _currentMedia == null)
                    return;

                if (_standbyPrepared &&
                    string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    _standbyStartPositionMs == startPositionMs)
                    return;

                ReleasePreparedNext();

                var media = new Media(_libVlc, normalizedPath, FromType.FromPath);
                if (_normalizationEnabled)
                {
                    media.AddOption(":audio-replay-gain-mode=track");
                    media.AddOption(":audio-replay-gain-preamp=0.0");
                    media.AddOption(":audio-replay-gain-default=-7.0");
                }

                var parseTask = media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);
                if (!parseTask.Wait(8000) || parseTask.Result != MediaParsedStatus.Done)
                {
                    media.Dispose();
                    DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", $"path={Path.GetFileName(normalizedPath)}");
                    return;
                }

                _standbyMedia = media;
                _standbyPath = normalizedPath;
                _standbyStartPositionMs = startPositionMs;
                Interlocked.Exchange(ref _standbyPreparedTicksUtc, DateTime.UtcNow.Ticks);
                _standbyPrepared = true;
                SetPlayerVolumeGuarded(_standbyPlayer, 0);
                _standbyPlayer.Mute = _player.Mute;

                Equalizer? equalizerToApply = null;
                lock (_equalizerLock)
                {
                    if ((_soundEnhancerEnabled || _advancedEqEnabled) && _equalizer != null)
                        equalizerToApply = _equalizer;
                }

                if (equalizerToApply != null)
                    _standbyPlayer.SetEqualizer(equalizerToApply);

                DebugLogger.Info(
                    DebugLogger.Category.Playback,
                    "AutoMix.DualPrepared",
                    $"path={Path.GetFileName(normalizedPath)}, startMs={startPositionMs}, elapsedMs={Environment.TickCount64 - prepareStart}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", ex.Message);
                ReleasePreparedNext();
            }
            finally
            {
                _playbackLock.Release();
            }
        });
    }

    public void CancelPreparedNext()
    {
        if (_disposed) return;
        CancelSkipCts();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (_standbyPrepared)
                    DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.Cancelled", "inactive player stopped");
                ReleasePreparedNext();
            }
            finally
            {
                _playbackLock.Release();
            }
        });
    }

    // ── Playback control ────────────────────────────────────────

    public void Play(string filePath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath)) return;

        if (!File.Exists(filePath))
        {
            PlaybackError?.Invoke(this, $"File not found: {filePath}");
            return;
        }

        DebugLogger.Info(DebugLogger.Category.Playback, "VLC.Play", $"path={Path.GetFileName(filePath)}");
        _currentMediaPath = filePath;
        // Re-apply RG for the new track using the last known mode/preamp. If
        // the mode is "Off" this is a no-op and _replayGainScalar stays 1.0.
        if (!string.Equals(_rgMode, "Off", StringComparison.OrdinalIgnoreCase))
            ApplyReplayGain(_rgMode, _rgPreampDb);

        // All heavy work on ThreadPool, serialized by the lock.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _playbackLock.Wait();
            try
            {
                PlayInternal(filePath);
            }
            finally
            {
                _playbackLock.Release();
            }
        });
    }

    /// <summary>
    /// Core playback logic. Must be called under _playbackLock on a ThreadPool thread.
    ///
    /// Sequence:
    ///   1. Create new Media + parse header while current playback can continue
    ///   2. Stop current playback (synchronous, VLC releases all buffers)
    ///   3. Dispose old Media (safe now that VLC isn't reading it)
    ///   4. Start playback
    ///
    /// Parsing is critical for M4A/ALAC. Without it, VLC may not detect
    /// the AAC/ALAC codec inside the MP4 container, causing silent playback
    /// or immediate EndReached.
    /// </summary>
    private void PlayInternal(string filePath)
    {
        try
        {
            // Cancel any in-progress fade/parse from a previous PlayInternal call
            // so rapid Next/Previous skips respond instantly.
            var sessionId = Interlocked.Increment(ref _playbackSessionId);
            _positionTimer.Stop();
            var oldCts = _skipCts;
            _skipCts = new CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();
            var cancel = _skipCts.Token;

            ResetEndReachedPending();
            Interlocked.Exchange(ref _latestSeekMs, -1);
            Interlocked.Exchange(ref _lastKnownLengthMs, 0);

            var hadPreviousMedia = _currentMedia != null;
            var targetVolume = GetTargetVlcVolume();
            var canTransitionFade = _crossfadeEnabled && hadPreviousMedia && !_player.Mute;
            var fadeOutMs = canTransitionFade && _player.IsPlaying
                ? Math.Clamp(_crossfadeDurationMs / 2, 100, 6000)
                : 0;
            var fadeInMs = canTransitionFade
                ? Math.Clamp(_crossfadeDurationMs - fadeOutMs, 100, 12000)
                : 0;

            if (canTransitionFade &&
                TryStartPreparedAutoMix(filePath, targetVolume, sessionId, cancel))
            {
                Interlocked.Exchange(ref _pendingSeekMs, -1);
                return;
            }

            if (!canTransitionFade && _standbyPrepared)
                ReleasePreparedNext();

            // 1. Create and parse the new media before fading/stopping the old one.
            // This keeps AutoMix transitions clean: the next track is already picked
            // and decoder-ready before the audible handoff starts.
            var media = new Media(_libVlc, filePath, FromType.FromPath);

            // Parse the file header synchronously. This reads container
            // metadata (codec, sample rate, duration, channel layout).
            // Without this, M4A/ALAC/AAC can fail to decode.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(8000);
            var parseTask = media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);
            try
            {
                parseTask.Wait(cts.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Skipped by a new Play() call — abort cleanly
                media.Dispose();
                return;
            }

            var parseResult = parseTask.Result;
            if (parseResult != MediaParsedStatus.Done)
            {
                // Parsing failed or timed out — file may be corrupted
                media.Dispose();
                PlaybackError?.Invoke(this, $"Could not parse: {filePath}");
                return;
            }

            // 2. Stop current playback — synchronous when off VLC's event thread.
            // After EndReached, IsPlaying is already false, but VLC can still be
            // holding the ended media. Stop whenever media exists so sequential
            // queue playback starts the next item from a clean player state.
            if (_currentMedia != null || _player.IsPlaying || _isPaused)
            {
                if (fadeOutMs > 0)
                    FadePlayerVolumeBlocking(_player.Volume, 0, fadeOutMs, cancel);

                _player.Stop();
            }

            // 3. Dispose old media AFTER stop so VLC isn't reading from it
            var oldMedia = _currentMedia;
            _currentMedia = media;
            oldMedia?.Dispose();
            _isPaused = false;

            // 4. Apply loudness normalization via ReplayGain tags (static per-track).
            //    IMPORTANT: The previous implementation used VLC's "normvol" audio
            //    filter, which is a real-time AGC (automatic gain control). It analyzes
            //    a sliding window of audio buffers and adjusts gain dynamically — this
            //    causes audible "pumping" on music with high dynamic range (e.g. beat
            //    drops). ReplayGain reads pre-computed loudness metadata from the file
            //    and applies a fixed gain offset for the entire track — no real-time
            //    volume fluctuation.
            if (_normalizationEnabled)
            {
                _currentMedia.AddOption(":audio-replay-gain-mode=track");
                _currentMedia.AddOption(":audio-replay-gain-preamp=0.0");
                _currentMedia.AddOption(":audio-replay-gain-default=-7.0");
            }

            // 5. Start playback
            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            _player.Play(_currentMedia);

            // Re-apply volume curve and equalizer after starting new media
            if (fadeInMs > 0)
            {
                // Single-player approximation of crossfade: fade out old track, then fade in new one.
                SetPlayerVolumeGuarded(_player, 0);
                FadePlayerVolumeBlocking(0, targetVolume, fadeInMs, cancel);
            }
            else
            {
                SetPlayerVolumeGuarded(_player, targetVolume);
            }
            _crossfadeEnabled = false;

            Equalizer? equalizerToApply = null;
            lock (_equalizerLock)
            {
                if ((_soundEnhancerEnabled || _advancedEqEnabled) && _equalizer != null)
                    equalizerToApply = _equalizer;
            }

            if (equalizerToApply != null)
            {
                _player.SetEqualizer(equalizerToApply);
            }

            // 6. Apply pending seek (start time / saved position) now that media is playing
            var pendingMs = Interlocked.Exchange(ref _pendingSeekMs, -1);
            if (pendingMs > 0)
            {
                _player.Time = pendingMs;
            }

            // 7. Start position timer and fire initial duration update after brief delay
            _positionTimer.Start();

            // Poll for accurate duration shortly after playback starts
            // VLC may not report accurate duration until decoding begins
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(150);
                if (!_disposed && sessionId == CurrentSessionId && _player.IsPlaying)
                {
                    var len = _player.Length;
                    if (len > 0)
                    {
                        var dur = TimeSpan.FromMilliseconds(len);
                        DurationResolved?.Invoke(this, dur);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, $"Playback error: {ex.Message}");
        }
    }

    private bool TryStartPreparedAutoMix(string filePath, int targetVolume, long sessionId, CancellationToken cancel)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (!_standbyPrepared ||
            _standbyMedia == null ||
            string.IsNullOrWhiteSpace(_standbyPath) ||
            !string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.FallbackSinglePlayer", $"path={Path.GetFileName(filePath)}");
            return false;
        }

        try
        {
            ResetEndReachedPending();
            // Cancel any in-flight slider trailing write so it can't fire mid-crossfade
            // and fight the fade's direct _player.Volume writes.
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
            }

            SetPlayerVolumeGuarded(_standbyPlayer, 0);
            _standbyPlayer.Mute = _player.Mute;

            var preparedAgeMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _standbyPreparedTicksUtc)) / TimeSpan.TicksPerMillisecond;

            DebugLogger.Info(
                DebugLogger.Category.Playback,
                "AutoMix.DualStarted",
                $"path={Path.GetFileName(filePath)}, durationMs={_crossfadeDurationMs}, curve={_crossfadeFadeCurve}, preparedAgeMs={preparedAgeMs}");

            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            _standbyPlayer.Play(_standbyMedia);

            var startMs = Math.Max(_standbyStartPositionMs, Interlocked.Read(ref _pendingSeekMs));
            if (startMs > 0)
                _standbyPlayer.Time = startMs;

            if (!WaitForStandbyPlaybackReady(_standbyPlayer, sessionId, cancel, out var warmupElapsedMs))
            {
                DebugLogger.Warn(
                    DebugLogger.Category.Playback,
                    "AutoMix.FallbackSinglePlayer",
                    $"standby not ready; warmupElapsedMs={warmupElapsedMs}");
                ReleasePreparedNext();
                return false;
            }

            DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.DualWarmupReady", $"elapsedMs={warmupElapsedMs}, state={_standbyPlayer.State}");

            FadeDualPlayerVolumesBlocking(
                _player,
                _standbyPlayer,
                _player.Volume,
                targetVolume,
                _crossfadeDurationMs,
                _crossfadeFadeCurve,
                cancel);

            var finalVolume = GetTargetVlcVolume();
            if (cancel.IsCancellationRequested || _disposed)
            {
                SetPlayerVolumeGuarded(_player, finalVolume);
                ReleasePreparedNext();
                return true;
            }

            var outgoingPlayer = _player;
            var outgoingMedia = _currentMedia;
            _player = _standbyPlayer;
            SetPlayerVolumeGuarded(_player, finalVolume);
            _currentMedia = _standbyMedia;
            _standbyPlayer = outgoingPlayer;
            _standbyMedia = null;
            _standbyPath = null;
            _standbyStartPositionMs = -1;
            Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
            _standbyPrepared = false;

            // Crossfade just wrote finalVolume directly to _player.Volume — sync
            // the throttle deadband baseline so the next slider write isn't
            // erroneously suppressed (or accepted) by a stale _lastWrittenVolume.
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
                _lastWrittenVolume = finalVolume;
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            _crossfadeEnabled = false;
            _isPaused = false;
            _positionTimer.Start();

            DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.PlayerSwapCommitted", $"session={sessionId}");
            QueueInactivePlayerCleanup(_standbyPlayer, outgoingMedia, sessionId);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(150);
                if (!_disposed && sessionId == CurrentSessionId && _player.IsPlaying)
                {
                    var len = _player.Length;
                    if (len > 0)
                        DurationResolved?.Invoke(this, TimeSpan.FromMilliseconds(len));
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.FallbackSinglePlayer", ex.Message);
            ReleasePreparedNext();
            return false;
        }
    }

    private bool WaitForStandbyPlaybackReady(MediaPlayer standby, long sessionId, CancellationToken cancel, out long elapsedMs)
    {
        var start = Environment.TickCount64;
        var deadline = start + StandbyWarmupTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_disposed || cancel.IsCancellationRequested || sessionId != CurrentSessionId)
            {
                elapsedMs = Environment.TickCount64 - start;
                return false;
            }

            try
            {
                if (standby.IsPlaying)
                {
                    elapsedMs = Environment.TickCount64 - start;
                    return true;
                }
            }
            catch
            {
                elapsedMs = Environment.TickCount64 - start;
                return false;
            }

            Thread.Sleep(StandbyWarmupPollMs);
        }

        elapsedMs = Environment.TickCount64 - start;
        return false;
    }

    private void QueueInactivePlayerCleanup(MediaPlayer inactivePlayer, Media? inactiveMedia, long sessionId)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var cleanupStart = Environment.TickCount64;
            try
            {
                Thread.Sleep(DeferredCleanupDelayMs);
                DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.CleanupStart", $"session={sessionId}");
                SetPlayerVolumeGuarded(inactivePlayer, 0);
                inactivePlayer.Stop();
                inactivePlayer.SetEqualizer(null);
                inactiveMedia?.Dispose();
                DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.CleanupEnd", $"session={sessionId}, elapsedMs={Environment.TickCount64 - cleanupStart}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupFailed", ex.Message);
            }
        });
    }

    private void FadeDualPlayerVolumesBlocking(
        MediaPlayer outgoing,
        MediaPlayer incoming,
        int outgoingStartVolume,
        int incomingTargetVolume,
        int durationMs,
        AutoMixFadeCurve fadeCurve,
        CancellationToken cancel)
    {
        outgoingStartVolume = Math.Clamp(outgoingStartVolume, 0, 100);
        incomingTargetVolume = Math.Clamp(incomingTargetVolume, 0, 100);
        durationMs = Math.Max(0, durationMs);

        if (durationMs == 0)
        {
            SetPlayerVolumeGuarded(outgoing, 0);
            SetPlayerVolumeGuarded(incoming, incomingTargetVolume);
            return;
        }

        var steps = Math.Max(1, durationMs / FadeStepMs);
        var sleepMs = Math.Max(1, durationMs / steps);
        var fadeStart = Environment.TickCount64;
        Interlocked.Exchange(ref _lastDualFadeTickMs, fadeStart);
        Interlocked.Exchange(ref _slowDualFadeTicks, 0);
        DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.FadeStart", $"durationMs={durationMs}, steps={steps}, sleepMs={sleepMs}, curve={fadeCurve}");

        for (var i = 1; i <= steps; i++)
        {
            if (_disposed || cancel.IsCancellationRequested)
            {
                return;
            }

            var progress = (double)i / steps;
            var (outFactor, inFactor) = AutoMixFadeMath.GetFadeFactors(progress, fadeCurve);
            incomingTargetVolume = GetTargetVlcVolume();
            var headroom = 1.0 - ((1.0 - DualFadeHeadroom) * Math.Sin(Math.PI * progress));
            SetPlayerVolumeGuarded(outgoing, (int)Math.Round(outgoingStartVolume * outFactor * headroom));
            SetPlayerVolumeGuarded(incoming, (int)Math.Round(incomingTargetVolume * inFactor * headroom));

            var now = Environment.TickCount64;
            var lastTick = Interlocked.Exchange(ref _lastDualFadeTickMs, now);
            if (lastTick > 0 && now - lastTick > sleepMs + 25)
                Interlocked.Increment(ref _slowDualFadeTicks);

            if (i < steps)
                Thread.Sleep(sleepMs);
        }

        DebugLogger.Info(
            DebugLogger.Category.Playback,
            "AutoMix.FadeEnd",
            $"elapsedMs={Environment.TickCount64 - fadeStart}, slowTicks={Interlocked.CompareExchange(ref _slowDualFadeTicks, 0, 0)}");
    }

    private int GetTargetVlcVolume() =>
        ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));

    public void Pause()
    {
        if (_disposed) return;
        CancelSkipCts();
        CancelPreparedNext();

        if (_player.IsPlaying)
        {
            ResetEndReachedPending();
            _player.Pause();
            _isPaused = true;
            _positionTimer.Stop();
        }
    }

    public void Resume()
    {
        if (_disposed || _currentMedia == null) return;

        // Serialize with Play()/Stop() to prevent racing against a concurrent
        // Play() call on the ThreadPool (e.g., track ending + user unpausing).
        try
        {
            _playbackLock.Wait();
        }
        catch (ObjectDisposedException)
        {
            return; // Dispose() ran between the _disposed check and Wait()
        }

        try
        {
            if (_isPaused)
            {
                ResetEndReachedPending();
                // VLC's Pause() toggles between pause and play
                _player.Pause();
                _isPaused = false;
                _positionTimer.Start();
            }
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    public void Stop()
    {
        if (_disposed) return;

        CancelSkipCts();
        ResetEndReachedPending();
        Interlocked.Exchange(ref _latestSeekMs, -1);
        _positionTimer.Stop();
        _isPaused = false;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                ReleasePreparedNext();
                _player.Stop();

                // Detach and dispose media so VLC cannot replay it.
                var oldMedia = _currentMedia;
                _currentMedia = null;
                oldMedia?.Dispose();
            }
            finally
            {
                _playbackLock.Release();
            }
        });
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || _currentMedia == null) return;

        CancelSkipCts();
        CancelPreparedNext();
        ResetEndReachedPending();

        var len = _player.Length;
        if (len <= 0) return;

        var clampedMs = (long)Math.Clamp(position.TotalMilliseconds, 0, len);
        DebugLogger.Info(DebugLogger.Category.Playback, "Seek.Request", $"targetMs={clampedMs}, playerState={_player.State}");

        // Stop the position timer before enqueuing the seek so the timer thread
        // cannot read _player.Time concurrently while the seek worker writes it.
        // The worker restarts the timer after the seek is applied.
        _positionTimer.Stop();

        lock (_seekGate)
        {
            _latestSeekMs = clampedMs;
        }

        EnsureSeekWorker();
    }

    // ── VLC event handlers (fired on VLC's internal thread) ─────

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player))
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredInactive");
            return;
        }

        var sessionId = CurrentSessionId;
        var elapsedSinceStartMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPlayStartTicksUtc)) / TimeSpan.TicksPerMillisecond;
        if (elapsedSinceStartMs is >= 0 and < 500)
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredStale", $"session={sessionId}");
            return;
        }

        DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached", $"session={sessionId}");
        _isPaused = false;

        // Fire a final position update at the track's full duration so lyrics
        // and UI reflect the complete position before the track transitions.
        // VLC fires EndReached before the audio buffer fully drains, which
        // can cause lyrics/UI to cut early if TrackEnded is fired immediately.
        //
        // CRITICAL: _player.Length can return 0 after EndReached because VLC
        // considers the media finished. Fall back to the last known good length
        // captured during normal playback.
        try
        {
            var len = _player.Length;
            if (len <= 0)
                len = Interlocked.Read(ref _lastKnownLengthMs);
            if (len > 0)
                PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(len));
        }
        catch { /* Player may be in transitional state */ }

        var deadline = DateTime.UtcNow.AddMilliseconds(EndReachedGraceMs).Ticks;
        Interlocked.Exchange(ref _endReachedSessionId, sessionId);
        Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, deadline);
        _positionTimer.Start();
    }

    private void OnError(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player))
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "VLC.Error.IgnoredInactive");
            return;
        }

        DebugLogger.Error(DebugLogger.Category.Playback, "VLC.Error", "VLC encountered a playback error");
        ResetEndReachedPending();
        _positionTimer.Stop();
        _isPaused = false;
        PlaybackError?.Invoke(this, "VLC encountered a playback error.");
    }

    private void OnPositionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_disposed) return;

        try
        {
            var sessionId = CurrentSessionId;
            var endDeadlineTicks = Interlocked.Read(ref _endReachedDeadlineTicksUtc);
            if (endDeadlineTicks != 0)
            {
                var pendingEndSessionId = Interlocked.Read(ref _endReachedSessionId);
                if (pendingEndSessionId != sessionId)
                {
                    DebugLogger.Info(DebugLogger.Category.Playback, "VLC.Event.IgnoredStale", $"eventSession={pendingEndSessionId}, currentSession={sessionId}");
                    ResetEndReachedPending();
                    return;
                }

                // During grace period, report the full track duration so lyrics/UI
                // see the complete position. Fall back to last known good length
                // because _player.Length can return 0 after EndReached.
                var len = _player.Length;
                if (len <= 0)
                    len = Interlocked.Read(ref _lastKnownLengthMs);
                if (len > 0)
                    PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(len));

                if (DateTime.UtcNow.Ticks >= endDeadlineTicks &&
                    Interlocked.CompareExchange(ref _endReachedDeadlineTicksUtc, 0, endDeadlineTicks) == endDeadlineTicks)
                {
                    _positionTimer.Stop();
                    if (pendingEndSessionId == CurrentSessionId)
                        TrackEnded?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            // Don't gate on IsPlaying — VLC sets IsPlaying=false before the audio
            // output buffer fully drains, which kills position updates while music
            // is still audible. The timer lifecycle (Start on Play, Stop on
            // Pause/Stop) already controls when updates should fire.
            var time = _player.Time;
            if (time >= 0)
            {
                // Track the last known good length during normal playback.
                // _player.Length is reliable while playing but can return 0
                // after EndReached. Capturing it here ensures the grace
                // handler always has a valid fallback.
                var len = _player.Length;
                if (len > 0)
                    Interlocked.Exchange(ref _lastKnownLengthMs, len);

                var pos = TimeSpan.FromMilliseconds(time);
                if (sessionId == CurrentSessionId)
                    PositionChanged?.Invoke(this, pos);
            }
        }
        catch
        {
            // Player may have been disposed between check and read — safe to ignore
        }
    }

    private void EnsureSeekWorker()
    {
        if (Interlocked.CompareExchange(ref _seekWorkerActive, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (true)
                {
                    long targetMs;
                    lock (_seekGate)
                    {
                        targetMs = _latestSeekMs;
                        _latestSeekMs = -1;
                    }

                    if (targetMs < 0)
                        break;

                    var lastAppliedTicks = Interlocked.Read(ref _lastAppliedSeekTicksUtc);
                    if (lastAppliedTicks > 0)
                    {
                        var elapsedMs = (DateTime.UtcNow.Ticks - lastAppliedTicks) / TimeSpan.TicksPerMillisecond;
                        var waitMs = SeekThrottleMs - elapsedMs;
                        if (waitMs > 0)
                            Thread.Sleep((int)waitMs);
                    }

                    if (_disposed || _currentMedia == null)
                        break;

                    var len = _player.Length;
                    if (len <= 0)
                        continue;

                    targetMs = Math.Clamp(targetMs, 0, len);

                    var nowTicks = DateTime.UtcNow.Ticks;
                    DebugLogger.Info(DebugLogger.Category.Playback, "Seek.Apply", $"targetMs={targetMs}, state={_player.State}, isPlaying={_player.IsPlaying}");
                    _player.Time = targetMs;
                    Interlocked.Exchange(ref _lastAppliedSeekTicksUtc, nowTicks);

                    // Restart the position timer now that the seek is applied.
                    // It was stopped in Seek() to prevent concurrent _player.Time
                    // reads from the timer thread racing the write above.
                    if (!_isPaused)
                        _positionTimer.Start();
                    else
                        // When paused, VLC accepts seek but may not emit position updates.
                        PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(targetMs));
                }
            }
            catch
            {
                // If a seek fails due to transient VLC state, keep player alive.
            }
            finally
            {
                Interlocked.Exchange(ref _seekWorkerActive, 0);
                lock (_seekGate)
                {
                    if (_latestSeekMs >= 0 && !_disposed && _currentMedia != null)
                        EnsureSeekWorker();
                }
            }
        });
    }

    private void ResetEndReachedPending()
    {
        Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, 0);
        Interlocked.Exchange(ref _endReachedSessionId, 0);
    }

    private void CancelSkipCts()
    {
        try { _skipCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ReleasePreparedNext()
    {
        try { _standbyPlayer.Stop(); } catch { }
        SetPlayerVolumeGuarded(_standbyPlayer, 0);
        try { _standbyPlayer.SetEqualizer(null); } catch { }
        _standbyMedia?.Dispose();
        _standbyMedia = null;
        _standbyPath = null;
        _standbyStartPositionMs = -1;
        Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
        _standbyPrepared = false;
    }

    // ── Dispose ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ResetEndReachedPending();
        Interlocked.Exchange(ref _latestSeekMs, -1);
        _positionTimer.Stop();
        _positionTimer.Dispose();

        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnError;
        _standbyPlayer.EndReached -= OnEndReached;
        _standbyPlayer.EncounteredError -= OnError;

        CancelSkipCts();
        _skipCts.Dispose();
        lock (_volumeWriteLock)
        {
            try { _volumeTrailingCts?.Cancel(); } catch { }
            _volumeTrailingCts?.Dispose();
            _volumeTrailingCts = null;
        }

        try { _player.Stop(); } catch { }
        try { _standbyPlayer.Stop(); } catch { }

        lock (_equalizerLock)
        {
            _equalizer?.Dispose();
            _equalizer = null;
        }
        _currentMedia?.Dispose();
        _standbyMedia?.Dispose();
        _player.Dispose();
        _standbyPlayer.Dispose();
        _libVlc.Dispose();
        _playbackLock.Dispose();
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int LibcSetenv(string name, string value, int overwrite);

    private static void SetUnixEnv(string name, string value)
    {
        try
        {
            // overwrite=1 so we replace any stale value on the C side too.
            LibcSetenv(name, value, 1);
            // Also set via .NET so any managed code reading via Environment sees it.
            Environment.SetEnvironmentVariable(name, value);
        }
        catch
        {
            // Best effort; fall back to managed-only set.
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string? TryFindMacLibVlcPath()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        // Standard VLC.app install (covers `brew install --cask vlc` and manual installs).
        string[] candidates =
        {
            "/Applications/VLC.app/Contents/MacOS/lib",
            "/opt/homebrew/lib",
            "/usr/local/lib",
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "libvlc.dylib")))
                return dir;
        }
        return null;
    }

    private static string BuildLibVlcMissingMessage()
    {
        if (OperatingSystem.IsLinux())
        {
            return "libvlc is required but was not found. Install it with your package manager:\n" +
                   "  Debian/Ubuntu:  sudo apt install vlc\n" +
                   "  Fedora:         sudo dnf install vlc\n" +
                   "  Arch:           sudo pacman -S vlc";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "libvlc is required but was not found. Install VLC from https://www.videolan.org/vlc/ " +
                   "or via Homebrew: brew install --cask vlc";
        }
        return "libvlc native libraries were not found in the application directory. " +
               "Reinstall Noctis or check that the libvlc/ folder ships alongside the executable.";
    }
}
