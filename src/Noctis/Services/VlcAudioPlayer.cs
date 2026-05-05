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
    private const int VolumeThrottleMs = 80;
    private const int VolumeDeadband = 2;
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

    // Rate-limited volume path — applies immediately then enforces a cooldown.
    private int _pendingVolumeTarget = -1;
    private int _lastAppliedVolume = -1;
    private long _lastVolumeWriteTick;
    private CancellationTokenSource? _volumeTrailingCts;
    private readonly object _volumeGate = new();
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
                    Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginsPath);
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
        //   --file-caching=0      : Disable file read-ahead buffer. This is the
        //                           documented fix for VLC's 13-year seek stutter bug —
        //                           the caching delay is the root cause of audio
        //                           glitches heard after seeking to a new position.
        //   --no-video/spu        : skip video & subtitle pipelines entirely
        //   --no-audio-time-stretch: disable time-stretching that degrades quality
        //   --demux=avformat      : Force FFmpeg avformat demuxer for all audio files.
        //                           VLC's native MP3 demuxer performs a linear scan for
        //                           VBR MP3 files without a Xing/LAME seek index, causing
        //                           audible seek stutter on those tracks. FFmpeg reads the
        //                           Xing header and builds an O(1) seek table on open,
        //                           fixing per-song variation in seek quality. Also needed
        //                           for AAC/M4A Lossless seek smoothness.
        //   NOTE: --gain=0 was removed — it multiplies audio by 0 (silence!)
        //   --aout=mmdevice: WASAPI output. Required so VLC follows the
        //   Windows default-device change at runtime — WaveOut binds to the
        //   endpoint that was default at stream-open time and does not
        //   re-route when the user switches output device, which caused
        //   audible glitching and eventual silence until app restart.
        //   The vlc_AudioSessionEvents_OnSimpleVolumeChanged static during
        //   slider drag (the original reason WaveOut was selected) is
        //   mitigated by the throttling + deadband in ScheduleVolumeWrite.
        var vlcArgs = new List<string>
        {
            "--no-video",
            "--no-osd",
            "--no-spu",
            "--input-repeat=0",
            "--demux=avformat",
            "--file-caching=0",
            "--live-caching=0",
            "--disc-caching=0",
            "--no-audio-time-stretch",
            "--clock-jitter=0",
        };
        // The speex resampler module + its quality flag are not always present
        // in third-party VLC builds (notably the macOS VLC.app distribution).
        // mmdevice (WASAPI) is Windows-only.
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
            ScheduleVolumeWrite(target);
        }
    }

    public long PendingSeekMs
    {
        get => _pendingSeekMs;
        set => _pendingSeekMs = value;
    }

    /// <summary>
    /// Applies the final volume to VLC immediately, bypassing the throttle.
    /// Call on drag-end / pointer-release to ensure the exact target is applied.
    /// </summary>
    public void CommitVolume()
    {
        if (_disposed) return;
        if (_crossfadeEnabled && _currentMedia != null)
            return;

        var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
        lock (_volumeGate)
        {
            _volumeTrailingCts?.Cancel();
            _pendingVolumeTarget = -1;
            _lastAppliedVolume = target;
            _player.Volume = target;
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
    /// Schedules a single delayed volume write. Every call resets the timer;
    /// only the trailing write actually touches _player.Volume, eliminating
    /// the race between immediate and delayed paths that caused static.
    /// A deadband skips writes where the mapped VLC value hasn't moved
    /// enough to be audible.
    /// </summary>
    private void ScheduleVolumeWrite(int target)
    {
        lock (_volumeGate)
        {
            // Skip if the mapped VLC value hasn't changed enough to matter.
            if (_lastAppliedVolume >= 0 && Math.Abs(target - _lastAppliedVolume) < VolumeDeadband)
            {
                _pendingVolumeTarget = target; // remember it for CommitVolume
                return;
            }

            var now = Environment.TickCount64;
            var elapsed = now - _lastVolumeWriteTick;

            if (elapsed >= VolumeThrottleMs)
            {
                // Cooldown expired — apply immediately.
                _pendingVolumeTarget = -1;
                _lastAppliedVolume = target;
                _lastVolumeWriteTick = now;
                _player.Volume = target;
            }
            else
            {
                // Inside cooldown — schedule a trailing write for the remaining time.
                _pendingVolumeTarget = target;
                _volumeTrailingCts?.Cancel();
                var cts = new CancellationTokenSource();
                _volumeTrailingCts = cts;
                var delay = (int)(VolumeThrottleMs - elapsed);

                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    try
                    {
                        await Task.Delay(delay, cts.Token);
                        lock (_volumeGate)
                        {
                            var pending = _pendingVolumeTarget;
                            if (!_disposed && pending >= 0)
                            {
                                _pendingVolumeTarget = -1;
                                _lastAppliedVolume = pending;
                                _lastVolumeWriteTick = Environment.TickCount64;
                                _player.Volume = pending;
                            }
                        }
                    }
                    catch (TaskCanceledException) { }
                });
            }
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
            _player.Volume = toVolume;
            return;
        }

        var steps = Math.Max(1, durationMs / FadeStepMs);
        var sleepMs = Math.Max(1, durationMs / steps);

        for (var i = 1; i <= steps; i++)
        {
            if (_disposed || cancel.IsCancellationRequested)
            {
                _player.Volume = toVolume;
                return;
            }
            var progress = (double)i / steps;
            var eased = AutoMixFadeMath.SmoothFadeProgress(progress);
            var next = (int)Math.Round(fromVolume + ((toVolume - fromVolume) * eased));
            _player.Volume = Math.Clamp(next, 0, 100);

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

        var previousPresetIndex = _advancedEqPresetIndex;
        _advancedEqEnabled = enabled;
        _advancedEqPresetIndex = presetIndex;
        if (customBands is { Length: 10 })
        {
            for (var i = 0; i < 10; i++)
                _advancedEqBands[i] = Math.Clamp(customBands[i], -12f, 12f);
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
                }
                else
                {
                    // Avoid rebuilding native EQ every slider tick in custom mode.
                    if (_equalizer == null || previousPresetIndex >= 0)
                    {
                        _equalizer?.Dispose();
                        _equalizer = new Equalizer();
                    }

                    // Preserve preamp when switching from preset -> custom, which
                    // prevents sudden global volume drops while editing bands.
                    _equalizer.SetPreamp(Math.Clamp(_advancedEqPreamp, -20f, 20f));

                    for (uint i = 0; i < 10; i++)
                        _equalizer.SetAmp(_advancedEqBands[i], i);
                }

                if (_player != null)
                    _player.SetEqualizer(_equalizer);
                if (_standbyPrepared)
                    _standbyPlayer.SetEqualizer(_equalizer);
            }
        }
        else
        {
            // Disable advanced EQ. If sound enhancer is still on, re-apply it.
            if (_soundEnhancerEnabled)
            {
                SetSoundEnhancer(true, _soundEnhancerLevel);
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
                _standbyPlayer.Volume = 0;
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
                _player.Volume = 0;
                FadePlayerVolumeBlocking(0, targetVolume, fadeInMs, cancel);
            }
            else
            {
                _player.Volume = targetVolume;
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
            lock (_volumeGate)
            {
                _volumeTrailingCts?.Cancel();
                _pendingVolumeTarget = -1;
            }

            _standbyPlayer.Volume = 0;
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
                _player.Volume = finalVolume;
                ReleasePreparedNext();
                return true;
            }

            var outgoingPlayer = _player;
            var outgoingMedia = _currentMedia;
            _player = _standbyPlayer;
            _player.Volume = finalVolume;
            _currentMedia = _standbyMedia;
            _standbyPlayer = outgoingPlayer;
            _standbyMedia = null;
            _standbyPath = null;
            _standbyStartPositionMs = -1;
            Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
            _standbyPrepared = false;

            lock (_volumeGate)
            {
                _volumeTrailingCts?.Cancel();
                _pendingVolumeTarget = -1;
                _lastAppliedVolume = finalVolume;
                _lastVolumeWriteTick = Environment.TickCount64;
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
                inactivePlayer.Volume = 0;
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
            outgoing.Volume = 0;
            incoming.Volume = incomingTargetVolume;
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
            outgoing.Volume = Math.Clamp((int)Math.Round(outgoingStartVolume * outFactor * headroom), 0, 100);
            incoming.Volume = Math.Clamp((int)Math.Round(incomingTargetVolume * inFactor * headroom), 0, 100);

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
        ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));

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
        _standbyPlayer.Volume = 0;
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
        _volumeTrailingCts?.Cancel();
        _volumeTrailingCts?.Dispose();

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
