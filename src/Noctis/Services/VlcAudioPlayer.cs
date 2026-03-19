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
    private const int EndReachedGraceMs = 1200;
    private const int FadeStepMs = 35;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _currentMedia;
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

    // EndReached can fire before the final output buffer is fully audible.
    // Keep lyrics/UI alive briefly, then raise TrackEnded once the grace window passes.
    private long _endReachedDeadlineTicksUtc;

    // VLC's _player.Length can return 0 after EndReached (media considered "finished").
    // Store the last known good value so end-of-track position updates always reach
    // the true duration — otherwise lyrics/UI stop updating early.
    private long _lastKnownLengthMs;

    // Track paused state ourselves because VLC's MediaPlayer
    // does not expose a reliable IsPaused property.
    private volatile bool _isPaused;

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

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;

    public VlcAudioPlayer()
    {
        Core.Initialize();

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
        //                           fixing per-song variation in seek quality. Applies
        //                           globally — M4A, FLAC, CBR MP3 are unaffected.
        //   NOTE: --gain=0 was removed — it multiplies audio by 0 (silence!)
        //   NOTE: --aout removed — let VLC auto-detect best output module
        _libVlc = new LibVLC(
            "--no-video",
            "--no-osd",
            "--no-spu",
            "--input-repeat=0",
            "--demux=avformat",
            "--file-caching=0",
            "--live-caching=0",
            "--disc-caching=0",
            "--no-audio-time-stretch",
            "--audio-resampler=speex",
            "--speex-resampler-quality=10",
            "--clock-jitter=0"
        );

        _player = new MediaPlayer(_libVlc);

        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnError;

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

    // Store the user-facing volume (0–100) separately from VLC's internal volume,
    // because we apply a logarithmic curve to make low volumes audible.
    private int _userVolume = 75;

    public int Volume
    {
        get => _userVolume;
        set
        {
            if (_disposed) return;
            _userVolume = Math.Clamp(value, 0, 100);
            _player.Volume = ApplyVolumeCurve(_userVolume);
        }
    }

    public bool IsMuted
    {
        get => !_disposed && _player.Mute;
        set { if (!_disposed) _player.Mute = value; }
    }

    /// <summary>
    /// Applies a logarithmic curve so that low volume levels remain audible
    /// and the full range feels smooth and consistent.
    /// Linear volume maps poorly to human hearing — at 10% linear,
    /// perceived loudness is nearly silent. This curve fixes that.
    /// </summary>
    private static int ApplyVolumeCurve(int userVolume)
    {
        if (userVolume <= 0) return 0;
        if (userVolume >= 100) return 100;

        // Use a power curve (x^0.4) for even better low-end audibility.
        // At 1%  user → ~16% VLC (barely audible but present)
        // At 5%  user → ~28% VLC (clearly audible)
        // At 10% user → ~40% VLC (comfortable low volume)
        // At 25% user → ~55% VLC (moderate)
        // At 50% user → ~76% VLC (loud)
        // At 100% user → 100% VLC (max)
        double normalized = userVolume / 100.0;
        double curved = Math.Pow(normalized, 0.4);
        return (int)Math.Round(curved * 100);
    }

    private void FadePlayerVolumeBlocking(int fromVolume, int toVolume, int durationMs)
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
            if (_disposed) return;
            var progress = (double)i / steps;
            var next = (int)Math.Round(fromVolume + ((toVolume - fromVolume) * progress));
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
            }
        }
        else
        {
            // Disable equalizer (only if player is initialized)
            lock (_equalizerLock)
            {
                if (_player != null)
                    _player.SetEqualizer(null);
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

    public void SetCrossfade(bool enabled, int durationSeconds)
    {
        if (_disposed) return;
        _crossfadeEnabled = enabled;
        _crossfadeDurationMs = Math.Clamp(durationSeconds, 1, 12) * 1000;
    }

    // ── Playback control ────────────────────────────────────────

    public void Play(string filePath)
    {
        if (_disposed) return;
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
    ///   1. Stop current playback (synchronous, VLC releases all buffers)
    ///   2. Dispose old Media (safe now that VLC isn't reading it)
    ///   3. Create new Media + parse header (gets codec/duration/sample rate)
    ///   4. Start playback
    ///
    /// Step 3 is critical for M4A/ALAC. Without parsing, VLC may not detect
    /// the AAC/ALAC codec inside the MP4 container, causing silent playback
    /// or immediate EndReached.
    /// </summary>
    private void PlayInternal(string filePath)
    {
        try
        {
            ResetEndReachedPending();
            Interlocked.Exchange(ref _latestSeekMs, -1);
            Interlocked.Exchange(ref _lastKnownLengthMs, 0);

            var hadPreviousMedia = _currentMedia != null;
            var targetVolume = ApplyVolumeCurve(_userVolume);
            var canTransitionFade = _crossfadeEnabled && hadPreviousMedia && !_player.Mute;
            var fadeOutMs = canTransitionFade && _player.IsPlaying
                ? Math.Clamp(_crossfadeDurationMs / 2, 100, 6000)
                : 0;
            var fadeInMs = canTransitionFade
                ? Math.Clamp(_crossfadeDurationMs - fadeOutMs, 100, 12000)
                : 0;

            // 1. Stop current playback — synchronous when off VLC's event thread
            if (_player.IsPlaying || _isPaused)
            {
                if (fadeOutMs > 0)
                    FadePlayerVolumeBlocking(_player.Volume, 0, fadeOutMs);

                _player.Stop();
            }

            // 2. Dispose old media AFTER stop so VLC isn't reading from it
            var oldMedia = _currentMedia;
            _currentMedia = null;
            oldMedia?.Dispose();

            // 3. Create and parse the new media
            var media = new Media(_libVlc, filePath, FromType.FromPath);

            // Parse the file header synchronously. This reads container
            // metadata (codec, sample rate, duration, channel layout).
            // Without this, M4A/ALAC/AAC can fail to decode.
            using var cts = new CancellationTokenSource(8000);
            var parseTask = media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);
            parseTask.Wait(cts.Token);

            var parseResult = parseTask.Result;
            if (parseResult != MediaParsedStatus.Done)
            {
                // Parsing failed or timed out — file may be corrupted
                media.Dispose();
                PlaybackError?.Invoke(this, $"Could not parse: {filePath}");
                return;
            }

            _currentMedia = media;
            _isPaused = false;

            // 4. Apply audio filters
            if (_normalizationEnabled)
            {
                _currentMedia.AddOption(":audio-filter=normvol");
                // Limit normalization strength to prevent muting quiet tracks
                _currentMedia.AddOption(":norm-buff-size=20");
                _currentMedia.AddOption(":norm-max-level=2.0");
            }

            // 5. Start playback
            _player.Play(_currentMedia);

            // Re-apply volume curve and equalizer after starting new media
            if (fadeInMs > 0)
            {
                // Single-player approximation of crossfade: fade out old track, then fade in new one.
                _player.Volume = 0;
                FadePlayerVolumeBlocking(0, targetVolume, fadeInMs);
            }
            else
            {
                _player.Volume = targetVolume;
            }

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

            // 6. Start position timer and fire initial duration update after brief delay
            _positionTimer.Start();

            // Poll for accurate duration shortly after playback starts
            // VLC may not report accurate duration until decoding begins
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(150);
                if (!_disposed && _player.IsPlaying)
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

    public void Pause()
    {
        if (_disposed) return;

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
        DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached");
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
        Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, deadline);
        _positionTimer.Start();
    }

    private void OnError(object? sender, EventArgs e)
    {
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
            var endDeadlineTicks = Interlocked.Read(ref _endReachedDeadlineTicksUtc);
            if (endDeadlineTicks != 0)
            {
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

        try { _player.Stop(); } catch { }

        lock (_equalizerLock)
        {
            _equalizer?.Dispose();
            _equalizer = null;
        }
        _currentMedia?.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
        _playbackLock.Dispose();
    }
}
