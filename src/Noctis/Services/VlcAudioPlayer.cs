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
    // A backward seek to the very start desyncs LibVLC's mmdevice/WASAPI output
    // clock on files with encoder-delay priming (start_time != 0), producing a
    // permanent "playback too late → flushing buffers" stutter that never
    // recovers. Seeks landing at/under this threshold are served by a clean
    // track restart instead of an in-place seek. See Seek() for details.
    private const long StartSeekRestartThresholdMs = 1000;
    // ── Volume application (default, non-OS-session path) ──
    // Raw per-pixel slider writes go to player.Volume, which --aout=mmdevice
    // applies via the Windows audio session (ISimpleAudioVolume). Each session
    // change is ramped by the OS over ~10-20ms; hammering it every few ms during
    // a drag interrupts that ramp repeatedly → audible static (confirmed in the
    // diag log: a continuous stream of "mmdevice: simple volume changed" during
    // the drag). The fix: never write raw per-pixel. A ramp worker instead slews
    // the applied volume toward the latest slider value in small, evenly-paced
    // steps — a smooth continuous gain that follows the drag in real time, the
    // same way the crossfade already applies stepped volume cleanly.
    //   NOCTIS_VOL_RAMP_TICK — ms between steps (default 4; the worker raises the
    //                          Windows timer resolution so this is honored)
    //   NOCTIS_VOL_RAMP_STEP — max step per tick, in 0.1% amplitude units (default 5)
    // The level is applied to the Windows session as a FLOAT (sub-percent
    // resolution, click-free) so high-volume steps stop being coarse; non-Windows
    // / COM failure falls back to LibVLC's integer 0–100 player volume.
    // _applyingVolume breaks a reentrance loop: setting _player.Volume can fire
    // MediaPlayer.VolumeChanged, which (if observed) re-enters the public Volume
    // setter — Interlocked.CompareExchange guards every direct write.
    //
    // Legacy A/B: NOCTIS_VOL_SETTLE>0 restores the old settle-debounce (applies
    // on release, not real-time) for comparison; default 0 uses the ramp.
    private readonly int _volumeSettleMs =
        int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_VOL_SETTLE"), out var vs) && vs >= 0
            ? vs : 0;
    // Tick interval and max step are tuned together to keep the amplitude slew
    // (step per tick) under the Windows session-volume crackle threshold. Driving
    // the OS session faster than ~600–900 per-mille/sec produces audible
    // static/zipper; 10 per-mille every 16ms (~625/sec) is the fastest rate that
    // stays click-free while still tracking the slider in real time. Verified by
    // ear: STEP 10 clean, 15 faint crackle, 20 heavy static. Don't lower the tick
    // or raise the step without re-testing — it reintroduces the crackle.
    private readonly int _volumeRampTickMs =
        int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_VOL_RAMP_TICK"), out var rt) && rt >= 1
            ? rt : 16;
    // Max step per tick in per-mille (0.1%) of the 0–1000 amplitude level scale.
    private readonly int _volumeRampMaxStep =
        int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_VOL_RAMP_STEP"), out var rs) && rs >= 1
            ? rs : 10;
    private const int VolumeDeadband = 1;
    private const int EndReachedGraceMs = 1200;
    private const int FadeStepMs = 35;
    private const int StandbyWarmupTimeoutMs = 650;
    private const int StandbyWarmupPollMs = 25;
    private const int DeferredCleanupDelayMs = 1000;
    private const double DualFadeHeadroom = 0.88;
    // AutoMix no-silence handoff: the incoming track fades in from this fraction of the
    // user level (not silence), so there's no audible gap the moment the outgoing stops.
    private const double NoSilenceFadeInFloor = 0.35;
    // AutoMix overlap blend: while both tracks play, the shared session sits at this
    // fraction of the user level so their summed loudness (~+3 dB for two sources) stays
    // near the user's level instead of jumping. The incoming then rises back to full once
    // the outgoing stops. One shared control moves (no per-stream fade) → no thrash/stutter.
    private const double OverlapBlendLevel = 0.7;

    private readonly LibVLC _libVlc;
    private MediaPlayer _player;
    private MediaPlayer _standbyPlayer;

    // Windows: drives the user's volume through the OS audio session (ramped
    // smoothly, click-free) instead of LibVLC's abrupt float_mixer gain. When
    // non-null, LibVLC's own volume is pinned at 100 and only used as the
    // transient fade layer for crossfades. Null on non-Windows / COM failure →
    // the code falls back to the LibVLC (debounced) volume path. See
    // WindowsSessionVolume for why.
    private readonly WindowsSessionVolume? _sessionVolume;

    // Experimental Windows-only per-sample-gain output (NOCTIS_WASAPI=1, off by
    // default). When non-null it OWNS volume — applied click-free at any drag
    // speed — LibVLC's audio is routed here via SetAudioCallbacks, LibVLC's own
    // volume is pinned at unity, and _sessionVolume is forced null. See
    // WasapiGainOutput for why the stepped gain paths can't be both instant and
    // silent. The callback delegates must be held for the player's lifetime or
    // the GC collects them and LibVLC calls into freed memory.
    private readonly WasapiGainOutput? _wasapiOut;

    // Windows: silent WASAPI render stream that keeps the audio engine and the
    // endpoint warm, so LibVLC's mmdevice output never opens its stream against
    // a cold device — the cold open desyncs the output clock into the permanent
    // "playback too late → flushing buffers" stutter on the FIRST play after
    // launch (confirmed by reporter: keeping any other audio app open fully
    // suppresses it). Null on non-Windows / NOCTIS_KEEPALIVE=0 / init failure.
    // See WasapiSilenceKeepAlive for the idle-park and session-exclusion design.
    private readonly WasapiSilenceKeepAlive? _keepAlive;
    private MediaPlayer.LibVLCAudioPlayCb? _audioPlayCb;
    private MediaPlayer.LibVLCAudioPauseCb? _audioPauseCb;
    private MediaPlayer.LibVLCAudioResumeCb? _audioResumeCb;
    private MediaPlayer.LibVLCAudioFlushCb? _audioFlushCb;
    private MediaPlayer.LibVLCAudioDrainCb? _audioDrainCb;
    private MediaPlayer.LibVLCAudioSetupCb? _audioSetupCb;
    private MediaPlayer.LibVLCAudioCleanupCb? _audioCleanupCb;

    // Settings-driven WASAPI exclusive output (Windows). When enabled, LibVLC's
    // decoded PCM is routed via the audio callbacks to an exclusive-mode sink
    // opened at the SOURCE sample rate (see AudioSetup). Like the experimental
    // _wasapiOut path this is single-stream: crossfade and standby-prepare are
    // gated off while enabled. The sink is created lazily per format by the
    // audio-setup callback and reused across tracks with the same rate; an
    // exclusive open failure falls back to a shared-mode sink + OutputModeChanged.
    private volatile bool _exclusiveModeEnabled;
    private volatile WasapiGainOutput? _exclusiveOut;
    private readonly object _exclusiveSinkLock = new();

    private Media? _currentMedia;
    private Media? _standbyMedia;
    private string? _standbyPath;
    private long _standbyStartPositionMs = -1;
    private long _standbyPreparedTicksUtc;
    private bool _standbyPrepared;
    private bool _disposed;

    // ── VLC internal-log diagnostics (gated by env NOCTIS_VLC_LOG=1) ──
    // Off by default and zero-cost. When enabled, LibVLC's OWN log (decoder,
    // audio-output / mmdevice underrun warnings, seek/flush messages) is
    // written to a file alongside our Playback markers, so the exact failure
    // at a stutter moment can be read directly instead of guessed at. Pure
    // instrumentation — it does not alter the playback path.
    private StreamWriter? _vlcDiagWriter;
    private readonly object _vlcDiagLock = new();
    private long _vlcDiagStartTicks;

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

    // Volume ramp engine (see NOCTIS_VOL_RAMP_*). Works in per-mille amplitude
    // units (0–1000 = level 0.0–1.0). _rampTargetMilli is the latest slider value;
    // a single worker eases _rampCurrentMilli toward it, applying each step as a
    // float session level (or integer player volume on the fallback path). -1 =
    // uninitialized: the first value snaps (no startup glide). Accessed only via
    // Volatile.Read/Write (not the `volatile` keyword, which warns on ref pass).
    private int _rampTargetMilli = -1;
    private int _rampCurrentMilli = -1;
    private int _rampWorkerActive;

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

    // Equalizer state. The UI-facing parametric EQ is mapped onto LibVLC's
    // 10-band graphic equalizer upstream (see ParametricEqMath); this class
    // only ever receives the resolved 10 amp values + preamp.
    private readonly object _equalizerLock = new();
    private Equalizer? _equalizer;
    private bool _advancedEqEnabled;
    private float[] _advancedEqBands = new float[10];
    private float _advancedEqPreamp;
    private long _advancedEqRequestVersion;
    private long _advancedEqAppliedVersion;
    private int _advancedEqApplyQueued;

    // Normalization state
    private bool _normalizationEnabled;

    // Crossfade state
    private bool _crossfadeEnabled;
    private int _crossfadeDurationMs = 6000;
    private AutoMixFadeCurve _crossfadeFadeCurve = AutoMixFadeCurve.SmoothEase;
    // When false (AutoMix's no-silence handoff), the outgoing track is NOT faded out
    // early — it plays until the caller triggers the handoff near its end, then a short
    // click-safe dip hands straight to the incoming, which fades in. Eliminates the
    // mid-transition dead air of the fade-out → fade-in sequence.
    private volatile bool _crossfadeFadeOut = true;
    // When true (AutoMix), both tracks play simultaneously through the crossover (overlap
    // blend) instead of one-at-a-time. The shared session level dips through the blend and
    // rises on the incoming. Session path only.
    private volatile bool _crossfadeOverlap;

    // Gapless state: with a prepared standby and no crossfade, track changes
    // hand off to the standby player instantly at full volume.
    private volatile bool _gaplessEnabled = true;

    // Pending seek — applied inside PlayInternal after _player.Play() to avoid race
    private long _pendingSeekMs = -1;

    // Skip cancellation — cancelled when a new Play() is requested so any
    // in-progress fade or parse aborts immediately for instant track switching.
    private CancellationTokenSource _skipCts = new();

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;
    public event EventHandler<string>? OutputModeChanged;

    public VlcAudioPlayer()
    {
        // Bind the Core Audio MMDeviceEnumerator CLSID to NAudio's coclass FIRST,
        // before the session-volume / keep-alive / WASAPI paths activate it. NAudio
        // (WasapiGainOutput, exclusive + per-sample output) requires its own coclass
        // cast to succeed; if anything else binds the CLSID first, NAudio's sinks
        // throw and LibVLC ends up with no audio output at all. See CoreAudioComInterop.
        if (OperatingSystem.IsWindows())
            CoreAudioComInterop.EnsureInitialized();

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
        // NOTE on caching: VLC's 300ms default file-caching is fine for wired
        // output (near-zero endpoint latency) but too shallow for Bluetooth
        // A2DP. AirPods et al. add ~150-300ms of their own pipeline latency, so
        // a 300ms decode buffer runs dry between refills the moment any disk-read
        // jitter, GC pause, or track-change cost lands — mmdevice's WASAPI clock
        // then spirals into the permanent "playback too late -> flushing buffers"
        // stutter, dropping the first seconds of the track and stuttering
        // throughout. Reported as Bluetooth-only (wired is unaffected), which is
        // the signature of output-side starvation. The fix is to keep the
        // decoder further ahead so the output can't starve: deepen the input
        // caching to a Bluetooth-safe depth (VideoLAN's own BT recommendation).
        // This is purely a read-ahead margin — for local files VLC starts output
        // once primed rather than waiting out the whole window, so wired
        // track-start/seek and every other path are unchanged. --clock-jitter
        // stays at the 5000ms default; the seek-stutter fix remains
        // --demux=avformat (O(1) seek index, independent of caching).
        //   NOCTIS_CACHING overrides the depth in ms for A/B testing on real
        //   hardware (e.g. NOCTIS_CACHING=300 restores the old default).
        var cachingMs =
            int.TryParse(Environment.GetEnvironmentVariable("NOCTIS_CACHING"), out var cm) && cm >= 0
                ? cm : 1000;

        var vlcDiag = string.Equals(
            Environment.GetEnvironmentVariable("NOCTIS_VLC_LOG"), "1", StringComparison.Ordinal);

        var vlcArgs = new List<string>
        {
            "--no-video",
            "--no-osd",
            "--no-spu",
            "--input-repeat=0",
            "--demux=avformat",
            "--no-audio-time-stretch",
            $"--file-caching={cachingMs}",
            $"--disc-caching={cachingMs}",
            $"--live-caching={cachingMs}",
            $"--network-caching={cachingMs}",
        };
        // The speex resampler module + its quality flag are not always present
        // in third-party VLC builds (notably the macOS VLC.app distribution).
        // mmdevice is Windows-only.
        if (OperatingSystem.IsWindows())
        {
            vlcArgs.Add("--audio-resampler=speex");
            vlcArgs.Add("--speex-resampler-quality=10");
            // Diagnostic override: NOCTIS_AOUT lets us A/B the Windows audio
            // output module on real hardware without recompiling. On Bluetooth
            // (AirPods etc.) mmdevice's WASAPI clock can spiral into a permanent
            // "playback too late → flushing buffers" stutter; directsound /
            // waveout use different timing models. Defaults to mmdevice.
            var aoutOverride = Environment.GetEnvironmentVariable("NOCTIS_AOUT");
            var aout = string.IsNullOrWhiteSpace(aoutOverride) ? "mmdevice" : aoutOverride.Trim();
            vlcArgs.Add($"--aout={aout}");
        }

        // Verbose generation so LibVLC actually emits debug-level audio-output
        // (underrun / "playback too late" / flush) lines for the diag capture.
        if (vlcDiag)
            vlcArgs.Add("--verbose=2");

        // Diagnostic: append arbitrary space-separated LibVLC args so output
        // modules / clock / time-stretch settings can be A/B-tested on real
        // hardware without recompiling (e.g. "--audio-time-stretch" to test
        // whether tempo-stretch rides out Bluetooth clock drift instead of
        // dropping buffers). Appended last, so these override defaults above.
        var extraArgs = Environment.GetEnvironmentVariable("NOCTIS_VLC_EXTRA");
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            foreach (var tok in extraArgs.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                vlcArgs.Add(tok);
        }

        _libVlc = new LibVLC(vlcArgs.ToArray());

        // Identify our audio session as "Noctis" in the Windows Volume Mixer
        // (and as the network user agent) instead of LibVLC's default
        // "VLC media player (LibVLC x.y.z)".
        try
        {
            _libVlc.SetUserAgent("Noctis", "Noctis");
            _libVlc.SetAppId("com.heartached.noctis", "1.0", "noctis");
        }
        catch { /* cosmetic only — never block playback on naming */ }

        if (vlcDiag)
        {
            TryEnableVlcDiagnostics();
            // Self-document the effective audio config so the diag log proves
            // which build/settings produced the captured stutter (e.g. confirms
            // the deeper caching is actually live). Mirrored into the diag via
            // OnDebugEntryForDiag, which is now subscribed.
            DebugLogger.Info(DebugLogger.Category.Playback, "VLC.Config",
                $"args={string.Join(' ', vlcArgs)}");
        }

        _player = new MediaPlayer(_libVlc);
        _standbyPlayer = new MediaPlayer(_libVlc);

        // Experimental: route audio through a custom WASAPI sink that applies
        // volume as a per-sample interpolated gain (click-free at any drag speed).
        // SetAudioCallbacks disables LibVLC's own output, so EQ/ReplayGain — both
        // applied upstream of the callback — stay baked into the PCM we receive,
        // and LibVLC's volume is pinned at unity. Single stream only: crossfade
        // and standby-prepare are gated off on this path (see PlayInternal /
        // PrepareNext). On any failure we fall through to the OS-session path.
        WasapiGainOutput? wasapi = null;
        if (OperatingSystem.IsWindows() &&
            Environment.GetEnvironmentVariable("NOCTIS_WASAPI") == "1")
        {
            wasapi = WasapiGainOutput.TryCreate();
            if (wasapi != null)
            {
                _audioPlayCb = AudioPlay;
                _audioPauseCb = AudioPause;
                _audioResumeCb = AudioResume;
                _audioFlushCb = AudioFlush;
                _audioDrainCb = AudioDrain;
                try
                {
                    // Order matters: register the amem callbacks first, then pin the
                    // output format to what our sink renders (FL32 at the device rate).
                    _player.SetAudioCallbacks(_audioPlayCb, _audioPauseCb, _audioResumeCb, _audioFlushCb, _audioDrainCb);
                    _player.SetAudioFormat("FL32", (uint)wasapi.SampleRate, (uint)wasapi.Channels);
                    _player.Volume = 100;
                    WasapiGainOutput.Diag($"VlcAudioPlayer wired callbacks: FL32 {wasapi.SampleRate}Hz {wasapi.Channels}ch");
                }
                catch (Exception ex)
                {
                    WasapiGainOutput.Diag($"VlcAudioPlayer wiring FAILED: {ex.GetType().Name}: {ex.Message}");
                    try { wasapi.Dispose(); } catch { }
                    wasapi = null;
                    _audioPlayCb = null; _audioPauseCb = null; _audioResumeCb = null;
                    _audioFlushCb = null; _audioDrainCb = null;
                }
            }
        }
        _wasapiOut = wasapi;

        // Volume is driven through the Windows audio session as a FLOAT level via
        // a fine click-free ramp (see the NOCTIS_VOL_RAMP_* notes above). When
        // active, LibVLC's own integer volume is pinned at 100 and used only as
        // the transient fade layer for crossfades. Null on non-Windows / COM
        // failure → fall back to the integer player-volume ramp.
        // NOCTIS_OSVOL=0 forces the integer fallback for A/B testing. Skipped
        // entirely when the WASAPI sink owns volume.
        _sessionVolume = _wasapiOut != null || Environment.GetEnvironmentVariable("NOCTIS_OSVOL") == "0"
            ? null
            : WindowsSessionVolume.TryCreate();
        if (_sessionVolume != null)
        {
            try { _player.Volume = 100; } catch { }
            try { _standbyPlayer.Volume = 100; } catch { }
        }

        // Start the keep-alive immediately: construction happens at app launch,
        // which is exactly the window before the reported first-play stutter.
        _keepAlive = WasapiSilenceKeepAlive.TryStart();

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

        // WASAPI sink path: the sink already tracks the live target per-sample, so
        // a drag-release commit is just a final target set (no throttle to flush).
        if (ActiveCallbackSink is { } commitSink)
        {
            commitSink.SetGainTarget(WasapiGainLevel());
            return;
        }

        var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
        target = ApplyReplayGainScalar(target);

        // Legacy A/B path (integer player volume only): flush exact final now.
        if (_sessionVolume == null && _volumeSettleMs > 0)
        {
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
                SetPlayerVolumeGuarded(_player, target);
                _lastWrittenVolume = target;
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }
            return;
        }

        // Drag released — set the exact final target; the ramp converges to it.
        Volatile.Write(ref _rampTargetMilli, CurvedVolumeToLevelMilli(target));
        EnsureVolumeRampWorker();
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

    private void ScheduleVolumeWrite(int target)
    {
        target = Math.Clamp(target, 0, 100);
        if (_disposed) return;

        // WASAPI sink path: hand the target straight to the sink, which interpolates
        // the gain per-sample (click-free at any speed). No ramp worker needed.
        if (ActiveCallbackSink is { } sink)
        {
            sink.SetGainTarget(WasapiGainLevel());
            return;
        }

        // Legacy A/B path (integer player volume only): settle-debounce.
        if (_sessionVolume == null && _volumeSettleMs > 0)
        {
            ScheduleSettleDebounce(target);
            return;
        }

        // Default: fine click-free ramp toward the latest target.
        Volatile.Write(ref _rampTargetMilli, CurvedVolumeToLevelMilli(target));
        EnsureVolumeRampWorker();
    }

    // Legacy settle-debounce: never writes mid-drag; the value lands once the
    // slider holds still for _volumeSettleMs (or on release via CommitVolume).
    // Integer player-volume path only — kept for NOCTIS_VOL_SETTLE>0 A/B testing.
    private void ScheduleSettleDebounce(int target)
    {
        lock (_volumeWriteLock)
        {
            if (_disposed) return;

            // Deadband: skip if nothing changed from the last value handed to VLC.
            if (_lastWrittenVolume >= 0 && Math.Abs(target - _lastWrittenVolume) < VolumeDeadband)
            {
                _pendingVolumeTarget = target; // CommitVolume will still flush
                return;
            }

            _pendingVolumeTarget = target;
            _volumeTrailingCts?.Cancel();
            var cts = new CancellationTokenSource();
            _volumeTrailingCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_volumeSettleMs, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { return; }

                lock (_volumeWriteLock)
                {
                    if (_disposed || cts.IsCancellationRequested) return;
                    var pending = _pendingVolumeTarget;
                    if (pending < 0) return;
                    if (_lastWrittenVolume >= 0 && Math.Abs(pending - _lastWrittenVolume) < VolumeDeadband)
                        return;
                    _pendingVolumeTarget = -1;
                    SetPlayerVolumeGuarded(_player, pending);
                    _lastWrittenVolume = pending;
                    _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
                }
            });
        }
    }

    /// <summary>
    /// Map a curved VLC volume (0–100) to the session amplitude level in per-mille
    /// (0–1000). mmdevice applies VLC's volume to the session cubically
    /// (amplitude = (vol/100)³), so reproducing that taper makes driving the
    /// session directly sound identical to the old player-volume path — just at
    /// float resolution. Stepping the ramp in this amplitude domain also gives
    /// uniform, click-free increments across the whole range.
    /// </summary>
    private static int CurvedVolumeToLevelMilli(int curvedVolume)
    {
        var amp = Math.Pow(Math.Clamp(curvedVolume, 0, 100) / 100.0, 3.0);
        return Math.Clamp((int)Math.Round(amp * 1000.0), 0, 1000);
    }

    // Inverse of the mmdevice cubic taper: the LibVLC player volume (0–100) whose open
    // sets the shared session to the given amplitude-milli (0–1000). Used to start the
    // overlap's incoming player matched to the current session so its open doesn't blip.
    private static int MilliToPlayerVolume(int milli) =>
        (int)Math.Round(Math.Cbrt(Math.Clamp(milli, 0, 1000) / 1000.0) * 100.0);

    /// <summary>
    /// Apply one ramp level (0–1000 per-mille amplitude). On Windows this is a
    /// float write to the OS audio session (sub-percent resolution, click-free);
    /// otherwise it falls back to LibVLC's integer 0–100 player volume (which
    /// mmdevice re-cubes), recovered via the inverse cube root.
    /// </summary>
    private void ApplyRampLevel(int milli)
    {
        milli = Math.Clamp(milli, 0, 1000);
        if (_sessionVolume != null)
        {
            _sessionVolume.SetLevel(milli / 1000.0);
        }
        else
        {
            var vol = (int)Math.Round(Math.Cbrt(milli / 1000.0) * 100.0);
            SetPlayerVolumeGuarded(_player, vol);
        }
    }

    /// <summary>
    /// Drives the click-free real-time volume ramp. A single worker eases
    /// _rampCurrentMilli toward the latest slider value (_rampTargetMilli),
    /// applying each step via <see cref="ApplyRampLevel"/>. Steps are proportional
    /// (fast on big jumps) but capped at _volumeRampMaxStep and floored at a fine
    /// minimum, so motion stays responsive while the final approach is smooth.
    /// Exits once converged; the next slider move re-arms it.
    /// </summary>
    private void EnsureVolumeRampWorker()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _rampWorkerActive, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            // Raise the Windows timer resolution for the duration of the ramp so
            // the short ramp tick is honored (default scheduler granularity is
            // ~15.6ms, which would otherwise jitter the steps). Released when we
            // converge.
            var raisedTimer = TryBeginHighResTimer();
            try
            {
                while (!_disposed)
                {
                    var target = Volatile.Read(ref _rampTargetMilli);
                    if (target < 0) break;

                    var current = Volatile.Read(ref _rampCurrentMilli);
                    if (current < 0)
                    {
                        // First value ever: snap (no glide up from 0 at startup).
                        ApplyRampLevel(target);
                        Volatile.Write(ref _rampCurrentMilli, target);
                        continue;
                    }

                    if (current == target) break; // converged

                    var delta = target - current;
                    var dist = Math.Abs(delta);
                    var step = Math.Max(2, (int)Math.Round(dist * 0.35));
                    step = Math.Min(step, _volumeRampMaxStep);
                    step = Math.Min(step, dist);
                    var next = current + (delta > 0 ? step : -step);

                    ApplyRampLevel(next);
                    Volatile.Write(ref _rampCurrentMilli, next);

                    if (next == Volatile.Read(ref _rampTargetMilli)) break;

                    try { await Task.Delay(_volumeRampTickMs).ConfigureAwait(false); }
                    catch { break; }
                }
            }
            finally
            {
                if (raisedTimer) TryEndHighResTimer();
                Interlocked.Exchange(ref _rampWorkerActive, 0);
                // A target set after our last read (or between the converge check
                // and clearing the flag) must still be served.
                if (!_disposed)
                {
                    var t = Volatile.Read(ref _rampTargetMilli);
                    if (t >= 0 && t != Volatile.Read(ref _rampCurrentMilli))
                        EnsureVolumeRampWorker();
                }
            }
        });
    }

    // Windows multimedia timer resolution. timeBeginPeriod(1) drops the system
    // timer granularity from ~15.6ms to ~1ms so the ramp's short Task.Delay ticks
    // are honored (finer steps → click-free). Paired with timeEndPeriod while the
    // ramp is active. No-op / safe on non-Windows.
    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint NativeTimeBeginPeriod(uint uMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint NativeTimeEndPeriod(uint uMilliseconds);

    private static bool TryBeginHighResTimer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return NativeTimeBeginPeriod(1) == 0; }
        catch { return false; }
    }

    private static void TryEndHighResTimer()
    {
        try { NativeTimeEndPeriod(1); }
        catch { /* nothing to release */ }
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

    /// <summary>
    /// Click-free volume fade rode on the OS audio session (ISimpleAudioVolume),
    /// stepped in the amplitude-milli domain (0–1000). The OS ramps each step
    /// sample-accurately, so this never produces the float_mixer "block gain"
    /// crackle that stepping MediaPlayer.Volume causes. Session path only — the
    /// caller guarantees _sessionVolume != null and that exactly ONE stream is
    /// audible (so there's no shared-session collision). Lands on toMilli even if
    /// cancelled, and syncs the slider ramp baseline so a later drag glides true.
    /// </summary>
    private void FadeSessionLevelBlocking(int fromMilli, int toMilli, int durationMs, CancellationToken cancel)
    {
        var sv = _sessionVolume;
        if (sv == null) return;
        fromMilli = Math.Clamp(fromMilli, 0, 1000);
        toMilli = Math.Clamp(toMilli, 0, 1000);
        durationMs = Math.Max(0, durationMs);

        if (durationMs == 0 || fromMilli == toMilli)
        {
            sv.SetLevel(toMilli / 1000.0);
            Volatile.Write(ref _rampCurrentMilli, toMilli);
            return;
        }

        var steps = Math.Max(1, durationMs / FadeStepMs);
        var sleepMs = Math.Max(1, durationMs / steps);
        for (var i = 1; i <= steps; i++)
        {
            if (_disposed || cancel.IsCancellationRequested)
            {
                sv.SetLevel(toMilli / 1000.0);
                Volatile.Write(ref _rampCurrentMilli, toMilli);
                return;
            }
            var eased = AutoMixFadeMath.SmoothFadeProgress((double)i / steps);
            var milli = (int)Math.Round(fromMilli + ((toMilli - fromMilli) * eased));
            sv.SetLevel(milli / 1000.0);
            Volatile.Write(ref _rampCurrentMilli, milli);
            if (i < steps)
                Thread.Sleep(sleepMs);
        }
    }

    public void SetAdvancedEqualizer(bool enabled, float[] bands, float preampDb)
    {
        if (_disposed) return;

        lock (_equalizerLock)
        {
            _advancedEqEnabled = enabled;
            _advancedEqPreamp = Math.Clamp(preampDb, -20f, 20f);
            if (bands is { Length: 10 })
            {
                for (var i = 0; i < 10; i++)
                    _advancedEqBands[i] = Math.Clamp(bands[i], -12f, 12f);
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
        float[] bands;
        float preamp;

        lock (_equalizerLock)
        {
            enabled = _advancedEqEnabled;
            bands = (float[])_advancedEqBands.Clone();
            preamp = _advancedEqPreamp;
        }

        if (enabled)
        {
            lock (_equalizerLock)
            {
                // Avoid rebuilding the native EQ every slider tick.
                _equalizer ??= new Equalizer();
                _equalizer.SetPreamp(Math.Clamp(preamp, -20f, 20f));
                for (uint i = 0; i < 10; i++)
                    _equalizer.SetAmp(bands[i], i);

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

    public void SetNormalization(bool enabled)
    {
        if (_disposed) return;
        _normalizationEnabled = enabled;
        // Normalization is applied per-track via VLC audio filters.
        // The flag is stored here and applied in PlayInternal when creating new media.
    }

    public bool ExclusiveModeActive => _exclusiveOut is { IsExclusive: true };

    public string OutputDescription
    {
        get
        {
            if (_exclusiveModeEnabled && _exclusiveOut is { } sink)
            {
                return sink.IsExclusive
                    ? $"WASAPI Exclusive — {sink.SampleRate / 1000.0:0.#} kHz / {(sink.BitsPerSample == 32 ? "32-bit float" : $"{sink.BitsPerSample}-bit")}"
                    : $"WASAPI Shared — {sink.SampleRate / 1000.0:0.#} kHz (exclusive unavailable)";
            }
            if (_exclusiveModeEnabled)
                return "WASAPI Exclusive (engages on play)";
            if (_wasapiOut != null)
                return $"WASAPI Shared — {_wasapiOut.SampleRate / 1000.0:0.#} kHz";
            if (OperatingSystem.IsWindows())
                return "WASAPI Shared (system mixer)";
            if (OperatingSystem.IsMacOS())
                return "CoreAudio (shared)";
            return "System output (shared)";
        }
    }

    public double ReplayGainAppliedDb =>
        Math.Abs(_replayGainScalar - 1.0) < 0.0001 ? 0.0 : 20.0 * Math.Log10(_replayGainScalar);

    public void SetExclusiveMode(bool enabled)
    {
        if (_disposed) return;
        if (!OperatingSystem.IsWindows()) enabled = false;
        // The experimental NOCTIS_WASAPI sink already owns the audio callbacks.
        if (_wasapiOut != null) return;
        if (_exclusiveModeEnabled == enabled) return;
        _exclusiveModeEnabled = enabled;

        // Switching output mechanisms requires fresh MediaPlayer instances:
        // libvlc's audio callbacks cannot be unregistered once set, so the old
        // players are torn down and rebuilt under the playback lock. The current
        // track resumes at its position afterwards.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (!_disposed)
                    RebuildOutputModeLocked(enabled);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.SwitchFailed", ex.Message);
            }
            finally
            {
                _playbackLock.Release();
            }
        });
    }

    /// <summary>
    /// Tear down both MediaPlayers and recreate them wired for the requested
    /// output mode. Must be called under _playbackLock on a worker thread.
    /// </summary>
    private void RebuildOutputModeLocked(bool exclusive)
    {
        var wasActive = _currentMedia != null && (_player.IsPlaying || _isPaused);
        var resumePath = _currentMediaPath;
        long resumeMs = 0;
        if (wasActive)
        {
            try { resumeMs = Math.Max(0, _player.Time); } catch { }
        }

        ResetEndReachedPending();
        Interlocked.Exchange(ref _latestSeekMs, -1);
        _positionTimer.Stop();
        ReleasePreparedNext();
        try { _player.Stop(); } catch { }
        var oldMedia = _currentMedia;
        _currentMedia = null;
        oldMedia?.Dispose();
        _isPaused = false;

        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnError;
        _standbyPlayer.EndReached -= OnEndReached;
        _standbyPlayer.EncounteredError -= OnError;
        try { _player.Dispose(); } catch { }
        try { _standbyPlayer.Dispose(); } catch { }

        _player = new MediaPlayer(_libVlc);
        _standbyPlayer = new MediaPlayer(_libVlc);
        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnError;
        _standbyPlayer.EndReached += OnEndReached;
        _standbyPlayer.EncounteredError += OnError;

        if (exclusive)
        {
            _audioSetupCb ??= AudioSetup;
            _audioCleanupCb ??= AudioCleanup;
            _audioPlayCb ??= AudioPlay;
            _audioPauseCb ??= AudioPause;
            _audioResumeCb ??= AudioResume;
            _audioFlushCb ??= AudioFlush;
            _audioDrainCb ??= AudioDrain;
            // Format callback first so each track negotiates its own source rate.
            _player.SetAudioFormatCallback(_audioSetupCb, _audioCleanupCb);
            _player.SetAudioCallbacks(_audioPlayCb, _audioPauseCb, _audioResumeCb, _audioFlushCb, _audioDrainCb);
            try { _player.Volume = 100; } catch { }
            // The silent keep-warm stream is pointless while we hold the endpoint
            // exclusively, and some drivers dislike the concurrent shared stream.
            _keepAlive?.SetSuspended(true);
            // The setup callback reports the negotiated format once audio flows.
            OutputModeChanged?.Invoke(this, "Exclusive mode enabled");
        }
        else
        {
            lock (_exclusiveSinkLock)
            {
                _exclusiveOut?.Dispose();
                _exclusiveOut = null;
            }
            _keepAlive?.SetSuspended(false);
            if (_sessionVolume != null)
            {
                try { _player.Volume = 100; } catch { }
                try { _standbyPlayer.Volume = 100; } catch { }
            }
            OutputModeChanged?.Invoke(this, "Shared output (system mixer)");
        }

        DebugLogger.Info(DebugLogger.Category.Playback, "Exclusive.ModeSwitched",
            $"exclusive={exclusive}, resuming={wasActive}");

        if (wasActive && !string.IsNullOrEmpty(resumePath) && File.Exists(resumePath))
        {
            Interlocked.Exchange(ref _pendingSeekMs, resumeMs > 1000 ? resumeMs : -1);
            PlayInternal(resumePath);
        }
    }

    /// <summary>
    /// LibVLC audio-setup callback (decoder thread): negotiate the output format
    /// for the new track. Opens (or reuses) the exclusive sink at the source
    /// sample rate; on failure falls back to a shared-mode sink so playback
    /// continues, raising OutputModeChanged either way.
    /// </summary>
    private int AudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        try
        {
            var requestedRate = (int)Math.Clamp(rate, 8000, 384000);
            var requestedChannels = (int)Math.Clamp(channels, 1, 2);
            string? notice = null;

            lock (_exclusiveSinkLock)
            {
                // Reuse the open device stream when the format matches; otherwise
                // close it (rate change, or a shared fallback that can retry
                // exclusive now that the device may be free).
                if (_exclusiveOut != null &&
                    (!_exclusiveOut.IsExclusive ||
                     _exclusiveOut.SampleRate != requestedRate ||
                     _exclusiveOut.Channels != requestedChannels))
                {
                    _exclusiveOut.Dispose();
                    _exclusiveOut = null;
                }

                if (_exclusiveOut == null)
                {
                    var sink = WasapiGainOutput.TryCreateExclusive(requestedRate, requestedChannels, out var reason);
                    if (sink != null)
                    {
                        notice = $"Exclusive output active — {sink.SampleRate / 1000.0:0.#} kHz / {(sink.BitsPerSample == 32 ? "32-bit float" : $"{sink.BitsPerSample}-bit")}";
                    }
                    else
                    {
                        sink = WasapiGainOutput.TryCreate();
                        notice = $"Exclusive mode unavailable ({reason}) — using shared output";
                    }

                    if (sink == null)
                        return -1; // no usable output at all; VLC skips audio

                    _exclusiveOut = sink;
                    sink.SetGainTarget(WasapiGainLevel());
                }

                rate = (uint)_exclusiveOut.SampleRate;
                channels = (uint)_exclusiveOut.Channels;
            }

            // "FL32" — LibVLC hands us float PCM; the sink converts to the
            // negotiated device format at the output.
            Marshal.Copy(new[] { (byte)'F', (byte)'L', (byte)'3', (byte)'2' }, 0, format, 4);

            if (notice != null)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "Exclusive.Setup", notice);
                OutputModeChanged?.Invoke(this, notice);
            }
            return 0;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.SetupFailed", ex.Message);
            return -1;
        }
    }

    /// <summary>LibVLC audio-cleanup callback: the track's audio output is going
    /// away. Keep the device stream open for the next track (same-rate tracks
    /// reuse it gaplessly); just drop any queued PCM.</summary>
    private void AudioCleanup(IntPtr opaque) => _exclusiveOut?.Flush();

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

    public void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false)
    {
        if (_disposed) return;
        _crossfadeEnabled = enabled;
        _crossfadeDurationMs = Math.Clamp(durationSeconds, 1, 12) * 1000;
        _crossfadeFadeCurve = fadeCurve;
        _crossfadeFadeOut = fadeOut;
        _crossfadeOverlap = overlap;
    }

    public void SetGapless(bool enabled)
    {
        if (_disposed) return;
        _gaplessEnabled = enabled;
    }

    public void PrepareNext(string filePath, long startPositionMs = -1)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        // The WASAPI callback sinks are single-stream: standby warmup would play
        // the second player through LibVLC's own output, bypassing the sink. Skip.
        if (_wasapiOut != null || _exclusiveModeEnabled)
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
                    if (_advancedEqEnabled && _equalizer != null)
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
        _keepAlive?.NotifyActivity();
        _currentMediaPath = filePath;

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

            // Re-apply ReplayGain for the new track (tag read does file IO, so it
            // runs here on the worker, before the target volume is computed). If
            // the mode is "Off" this is a no-op and _replayGainScalar stays 1.0.
            _currentMediaPath = filePath;
            if (!string.Equals(_rgMode, "Off", StringComparison.OrdinalIgnoreCase))
                ApplyReplayGain(_rgMode, _rgPreampDb);

            var hadPreviousMedia = _currentMedia != null;
            var targetVolume = GetTargetVlcVolume();
            // Crossfade needs two simultaneous streams; the WASAPI callback sinks
            // are single-stream, so disable the transition fade on those paths.
            var canTransitionFade = _crossfadeEnabled && hadPreviousMedia && !_player.Mute &&
                                    _wasapiOut == null && !_exclusiveModeEnabled;
            var fadeOutMs = canTransitionFade && _player.IsPlaying
                ? Math.Clamp(_crossfadeDurationMs / 2, 100, 6000)
                : 0;
            var fadeInMs = canTransitionFade
                ? Math.Clamp(_crossfadeDurationMs - fadeOutMs, 100, 12000)
                : 0;

            if (canTransitionFade)
            {
                // Windows OS-session path: both players share ONE volume control, so a
                // true overlap collides on it (the transition stutter). Use the click-free
                // sequential fade instead. On the per-player path (non-Windows / OSVOL off)
                // the two volumes are independent, so the dual-stream overlap works.
                var crossfadeStarted = _sessionVolume != null
                    ? (_crossfadeOverlap
                        ? TryStartOverlapFade(filePath, sessionId, cancel)
                        : TryStartSequentialFade(filePath, sessionId, cancel))
                    : TryStartPreparedAutoMix(filePath, targetVolume, sessionId, cancel, instantHandoff: false);
                if (crossfadeStarted)
                {
                    Interlocked.Exchange(ref _pendingSeekMs, -1);
                    return;
                }
            }

            // Gapless: no crossfade, but the next track was prepared on the
            // standby player — hand off to it instantly at full volume instead
            // of the audible stop/parse/start path.
            if (!canTransitionFade && _gaplessEnabled && _standbyPrepared && hadPreviousMedia &&
                TryStartPreparedAutoMix(filePath, targetVolume, sessionId, cancel, instantHandoff: true))
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
                if (_advancedEqEnabled && _equalizer != null)
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

            // The new output session opens at 100% — push the user level onto it
            // as soon as it appears so there's no full-volume blip on track start.
            ScheduleSessionVolumeReassert(sessionId);

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

    /// <summary>
    /// Start the prepared standby player and swap it in. With
    /// <paramref name="instantHandoff"/> false this is the AutoMix crossfade
    /// (standby fades in while the outgoing player fades out); with true it is
    /// the gapless handoff — the standby starts at full volume immediately and
    /// the (ended) outgoing player is silenced, no fade.
    /// </summary>
    private bool TryStartPreparedAutoMix(string filePath, int targetVolume, long sessionId, CancellationToken cancel, bool instantHandoff)
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
            // Park the volume ramp so it can't fight the crossfade's direct fades.
            Volatile.Write(ref _rampTargetMilli, Volatile.Read(ref _rampCurrentMilli));

            // Gapless handoff starts the incoming track at full level right away;
            // the crossfade starts it silent and fades it in.
            SetPlayerVolumeGuarded(_standbyPlayer, instantHandoff ? targetVolume : 0);
            _standbyPlayer.Mute = _player.Mute;

            var preparedAgeMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _standbyPreparedTicksUtc)) / TimeSpan.TicksPerMillisecond;

            DebugLogger.Info(
                DebugLogger.Category.Playback,
                instantHandoff ? "Gapless.DualStarted" : "AutoMix.DualStarted",
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

            if (instantHandoff)
            {
                // The outgoing track is at (or within a beat of) its end —
                // silence it so the incoming audio is the only thing audible.
                SetPlayerVolumeGuarded(_player, 0);
            }
            else
            {
                FadeDualPlayerVolumesBlocking(
                    _player,
                    _standbyPlayer,
                    _player.Volume,
                    targetVolume,
                    _crossfadeDurationMs,
                    _crossfadeFadeCurve,
                    cancel);
            }

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

            // Restore the user level on the session after the crossfade (the fade
            // drove the session volume up to full as the incoming track came in).
            ScheduleSessionVolumeReassert(sessionId);

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

    /// <summary>
    /// Windows OS-session crossfade — a click-free SEQUENTIAL fade. On mmdevice both
    /// MediaPlayers share ONE volume control (the process audio session), so a true
    /// overlap is impossible: driving both at once collides on that single control,
    /// which is the source of the transition stutter. Instead this fades the outgoing
    /// out, hands off to the pre-decoded standby (opened at volume 0 so its session
    /// starts silent — no blip), then fades the incoming in. Only one stream is ever
    /// audible, and both fades ride the OS session level (sample-accurate, click-free).
    /// Caller guarantees a prepared standby for filePath and _sessionVolume != null;
    /// returns false (uncommitted) when the standby isn't usable so the caller can fall
    /// back to the single-player path.
    /// </summary>
    private bool TryStartSequentialFade(string filePath, long sessionId, CancellationToken cancel)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_sessionVolume is not { } sessionVolume ||
            !_standbyPrepared || _standbyMedia == null ||
            string.IsNullOrWhiteSpace(_standbyPath) ||
            !string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", $"path={Path.GetFileName(filePath)}");
            return false;
        }

        try
        {
            ResetEndReachedPending();
            // Park the slider ramp + any trailing write so they can't fight the fade.
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
            }
            Volatile.Write(ref _rampTargetMilli, Volatile.Read(ref _rampCurrentMilli));

            var userMilli = CurvedVolumeToLevelMilli(
                ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
            var startMilli = Math.Clamp(Volatile.Read(ref _rampCurrentMilli), 0, 1000);
            if (startMilli <= 0) startMilli = userMilli;
            int fadeOutMs, fadeInMs;
            if (_crossfadeFadeOut)
            {
                // Crossfade: split the duration into a fade-out then a fade-in (brief dip).
                fadeOutMs = Math.Clamp(_crossfadeDurationMs / 2, 100, 6000);
                fadeInMs = Math.Clamp(_crossfadeDurationMs - fadeOutMs, 100, 12000);
            }
            else
            {
                // AutoMix no-silence handoff: the caller already held the outgoing track
                // until it was nearly over, so only a short click-safe dip is needed before
                // the swap; the incoming then fades in over the full duration. No dead air.
                fadeOutMs = 150;
                fadeInMs = Math.Clamp(_crossfadeDurationMs, 100, 12000);
            }

            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.SeqStart",
                $"path={Path.GetFileName(filePath)}, durationMs={_crossfadeDurationMs}, fadeOut={_crossfadeFadeOut}, fadeOutMs={fadeOutMs}, fadeInMs={fadeInMs}");

            // 1. Fade the outgoing out via the OS session (only it is audible → no collision).
            FadeSessionLevelBlocking(startMilli, 0, fadeOutMs, cancel);
            if (_disposed || cancel.IsCancellationRequested)
            {
                sessionVolume.SetLevel(userMilli / 1000.0); // a new Play() cancelled us; restore + let it take over
                ReleasePreparedNext();
                return true;
            }

            // 2. Stop the outgoing, start the pre-decoded standby. Force its volume to 0
            //    first so LibVLC opens the new session silent (no full-volume blip).
            var outgoingPlayer = _player;
            var outgoingMedia = _currentMedia;

            try { _standbyPlayer.Volume = 0; } catch { }
            _standbyPlayer.Mute = _player.Mute;
            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            _standbyPlayer.Play(_standbyMedia);
            var seekMs = Math.Max(_standbyStartPositionMs, Interlocked.Read(ref _pendingSeekMs));
            if (seekMs > 0) _standbyPlayer.Time = seekMs;

            if (!WaitForStandbyPlaybackReady(_standbyPlayer, sessionId, cancel, out var warmupMs))
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", $"standby not ready; warmupMs={warmupMs}");
                try { _standbyPlayer.Stop(); } catch { }
                sessionVolume.SetLevel(userMilli / 1000.0);
                ReleasePreparedNext();
                return false;
            }

            // 3. Swap → the standby becomes the active player.
            _player = _standbyPlayer;
            _currentMedia = _standbyMedia;
            _standbyPlayer = outgoingPlayer;
            _standbyMedia = null;
            _standbyPath = null;
            _standbyStartPositionMs = -1;
            Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
            _standbyPrepared = false;
            _isPaused = false;
            ResetEndReachedPending();

            // AutoMix's no-silence handoff fades the incoming in from an audible floor
            // (not silence) so there's no dead-air gap when the outgoing stops; plain
            // Crossfade keeps its intentional from-zero dip.
            var fadeInFromMilli = _crossfadeFadeOut ? 0 : (int)Math.Round(userMilli * NoSilenceFadeInFloor);

            // The incoming opened its own OS session — drop the outgoing's dead one and
            // wait (briefly) until the new session is controllable so the fade-in applies.
            // It plays silent (player volume 0) during this poll, so there's nothing to hear yet.
            sessionVolume.Invalidate();
            for (var waited = 0; waited < 400 && !sessionVolume.SetLevel(fadeInFromMilli / 1000.0); waited += 10)
            {
                if (_disposed || cancel.IsCancellationRequested) break;
                Thread.Sleep(10);
            }

            // Carry the EQ onto the now-active player.
            Equalizer? eqToApply = null;
            lock (_equalizerLock)
            {
                if (_advancedEqEnabled && _equalizer != null)
                    eqToApply = _equalizer;
            }
            if (eqToApply != null)
                _player.SetEqualizer(eqToApply);

            _positionTimer.Start();
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.SeqSwap", $"session={sessionId}, warmupMs={warmupMs}");

            // 4. Fade the incoming in via the OS session (from the audible floor for the
            //    no-silence handoff, from zero for plain Crossfade).
            FadeSessionLevelBlocking(fadeInFromMilli, userMilli, fadeInMs, cancel);

            _crossfadeEnabled = false;
            lock (_volumeWriteLock)
            {
                _lastWrittenVolume = ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            // Re-assert in case the session resolved late, then tear down the outgoing.
            ScheduleSessionVolumeReassert(sessionId);
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
            DebugLogger.Warn(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", ex.Message);
            ReleasePreparedNext();
            return false;
        }
    }

    /// <summary>
    /// AutoMix overlap blend (Windows OS-session path). Both tracks play simultaneously
    /// through the crossover: the incoming starts ALONGSIDE the still-playing outgoing and
    /// both sit at <see cref="OverlapBlendLevel"/> of the user level (so their summed
    /// loudness stays steady); after the blend window the outgoing is stopped and the
    /// incoming rises back to full. Only the single shared session level moves — a handful
    /// of click-free OS-ramp writes, NOT the per-stream volume storm that collides and
    /// stutters. Caller guarantees a prepared standby for filePath and _sessionVolume != null;
    /// returns false (uncommitted) when the standby isn't usable so the caller can fall back.
    /// </summary>
    private bool TryStartOverlapFade(string filePath, long sessionId, CancellationToken cancel)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_sessionVolume is not { } sessionVolume ||
            !_standbyPrepared || _standbyMedia == null ||
            string.IsNullOrWhiteSpace(_standbyPath) ||
            !string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", $"path={Path.GetFileName(filePath)}");
            return false;
        }

        try
        {
            ResetEndReachedPending();
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
            }
            Volatile.Write(ref _rampTargetMilli, Volatile.Read(ref _rampCurrentMilli));

            var userMilli = CurvedVolumeToLevelMilli(
                ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
            var blendMilli = Math.Clamp((int)Math.Round(userMilli * OverlapBlendLevel), 1, 1000);
            var holdMs = Math.Clamp(_crossfadeDurationMs, 800, 6000);
            var riseMs = Math.Clamp(_crossfadeDurationMs / 2, 600, 3000);

            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.OverlapStart",
                $"path={Path.GetFileName(filePath)}, holdMs={holdMs}, riseMs={riseMs}, blendMilli={blendMilli}");

            var outgoingPlayer = _player;
            var outgoingMedia = _currentMedia;

            // 1. Start the incoming ALONGSIDE the outgoing, opened at the blend level so its
            //    session open ducks the shared volume to the blend (no +3 dB summed jump).
            //    Both are now audible together — the overlap.
            try { _standbyPlayer.Volume = MilliToPlayerVolume(blendMilli); } catch { }
            _standbyPlayer.Mute = _player.Mute;
            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            _standbyPlayer.Play(_standbyMedia);
            var seekMs = Math.Max(_standbyStartPositionMs, Interlocked.Read(ref _pendingSeekMs));
            if (seekMs > 0) _standbyPlayer.Time = seekMs;

            if (!WaitForStandbyPlaybackReady(_standbyPlayer, sessionId, cancel, out var warmupMs))
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", $"standby not ready; warmupMs={warmupMs}");
                try { _standbyPlayer.Stop(); } catch { }
                sessionVolume.SetLevel(userMilli / 1000.0);
                ReleasePreparedNext();
                return false;
            }
            // Pin the shared session exactly at the blend level (corrects the open's cubic).
            sessionVolume.SetLevel(blendMilli / 1000.0);
            Volatile.Write(ref _rampCurrentMilli, blendMilli);
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.OverlapBoth", $"warmupMs={warmupMs}");

            // 2. Hold the blend while BOTH play (the audible overlap). Cancellable.
            for (var waited = 0; waited < holdMs; waited += 50)
            {
                if (_disposed || cancel.IsCancellationRequested)
                {
                    sessionVolume.SetLevel(userMilli / 1000.0);
                    ReleasePreparedNext();
                    return true;
                }
                Thread.Sleep(50);
            }

            // 3. Stop the outgoing (it's been heard through its ending) and swap → the
            //    incoming is now the active player, still at the blend level.
            try { outgoingPlayer.Stop(); } catch { }
            _player = _standbyPlayer;
            _currentMedia = _standbyMedia;
            _standbyPlayer = outgoingPlayer;
            _standbyMedia = null;
            _standbyPath = null;
            _standbyStartPositionMs = -1;
            Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
            _standbyPrepared = false;
            _isPaused = false;
            ResetEndReachedPending();

            Equalizer? eqToApply = null;
            lock (_equalizerLock)
            {
                if (_advancedEqEnabled && _equalizer != null)
                    eqToApply = _equalizer;
            }
            if (eqToApply != null)
                _player.SetEqualizer(eqToApply);

            _positionTimer.Start();
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.OverlapSwap", $"session={sessionId}");

            // 4. Rise the incoming back to the full user level.
            FadeSessionLevelBlocking(blendMilli, userMilli, riseMs, cancel);

            _crossfadeEnabled = false;
            lock (_volumeWriteLock)
            {
                _lastWrittenVolume = ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            ScheduleSessionVolumeReassert(sessionId);
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
            DebugLogger.Warn(DebugLogger.Category.Playback, "Crossfade.FallbackSinglePlayer", ex.Message);
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
                if (_disposed) return;
                DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.CleanupStart", $"session={sessionId}");

                // CRITICAL: do NOT zero this player's Volume on the OS-session path. On
                // Windows mmdevice MediaPlayer.Volume IS the shared process audio session
                // (ISimpleAudioVolume), so silencing the outgoing player here also silenced
                // the now-active track — and the throw below left the session stuck at 0 for
                // the rest of the track (decoder kept running → "plays but no audio"). Only
                // pre-silence on the legacy per-player path; Stop() tears down this player's
                // own stream silently either way. Each step is isolated so a single failure
                // can't skip the media Dispose (a leak) or the volume re-assert.
                if (_sessionVolume == null)
                {
                    try { SetPlayerVolumeGuarded(inactivePlayer, 0); } catch { /* legacy per-player path */ }
                }
                try { inactivePlayer.Stop(); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupStep", $"Stop: {ex.GetType().Name}: {ex.Message}"); }
                try { inactivePlayer.SetEqualizer(null); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupStep", $"SetEqualizer: {ex.GetType().Name}"); }
                try { inactiveMedia?.Dispose(); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupStep", $"Dispose: {ex.GetType().Name}"); }

                // Belt-and-suspenders: ensure the shared session carries the user's level
                // for whatever is actually playing now, regardless of what teardown touched.
                ReapplySessionVolume();

                DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.CleanupEnd", $"session={sessionId}, elapsedMs={Environment.TickCount64 - cleanupStart}");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupFailed", $"{ex.GetType().Name}: {ex.Message}");
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

    // The steady LibVLC volume for the active player. With OS-session volume the
    // user level lives on the session, so LibVLC stays at full (100) and only
    // moves as the transient crossfade fade layer; otherwise it carries the
    // curved + ReplayGain-scaled user level directly.
    private int GetTargetVlcVolume() =>
        _sessionVolume != null || _wasapiOut != null || _exclusiveModeEnabled
            ? 100
            : ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));

    // Current user volume as a 0..1 amplitude for the WASAPI sinks: the perceptual
    // curve (with the ReplayGain scalar folded in, matching the session path)
    // cubed to the same taper the OS-session path used, so all paths sound
    // identical at a given slider position.
    private float WasapiGainLevel() =>
        CurvedVolumeToLevelMilli(ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)))) / 1000f;

    // The callback sink currently receiving LibVLC's decoded PCM: the
    // experimental env-gated one, or the settings-driven exclusive-mode one.
    private WasapiGainOutput? ActiveCallbackSink => _wasapiOut ?? _exclusiveOut;

    // ── WASAPI output callbacks (experimental gain path + exclusive mode) ──
    // LibVLC delivers decoded FL32 PCM here (EQ already applied upstream); we
    // forward it to the sink, which applies the user's volume per-sample. These
    // run on LibVLC's audio thread — they must never throw.
    private long _audioPlayCallCount;
    private void AudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        var sink = ActiveCallbackSink;
        if (sink == null) return;
        var bytes = checked((int)count * sink.Channels * 4);
        if (Interlocked.Increment(ref _audioPlayCallCount) <= 3)
            WasapiGainOutput.Diag($"AudioPlay #{_audioPlayCallCount}: count(frames)={count} -> {bytes}B, pts={pts}");
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(samples, buf, 0, bytes);
            sink.Write(buf, bytes);
        }
        catch (Exception ex)
        {
            if (_audioPlayCallCount <= 5) WasapiGainOutput.Diag($"AudioPlay threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    private void AudioPause(IntPtr data, long pts) => ActiveCallbackSink?.Pause();
    private void AudioResume(IntPtr data, long pts) => ActiveCallbackSink?.Resume();
    private void AudioFlush(IntPtr data, long pts) => ActiveCallbackSink?.Flush();
    private void AudioDrain(IntPtr data) => ActiveCallbackSink?.Drain();

    /// <summary>
    /// Push the current user level onto the OS audio session. LibVLC recreates
    /// its session at full volume whenever the output (re)opens — a new track,
    /// a restart, a crossfade swap — so the session level must be re-asserted
    /// once the new session exists, or playback would jump to 100%. No-op when
    /// OS-session volume isn't in use. Returns true once a session was set.
    /// </summary>
    private bool ReapplySessionVolume()
    {
        if (_sessionVolume == null) return false;
        var target = ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
        var milli = CurvedVolumeToLevelMilli(target);
        var ok = _sessionVolume.SetLevel(milli / 1000.0);
        if (ok)
        {
            lock (_volumeWriteLock) { _lastWrittenVolume = target; }
            // The session level was set outside the ramp (output reopen) — sync
            // the ramp's current so a later drag glides from the true level.
            Volatile.Write(ref _rampCurrentMilli, milli);
        }
        return ok;
    }

    /// <summary>
    /// Re-assert the session level as soon as the new output session appears,
    /// retrying briefly so the full-volume window after (re)open is inaudible.
    /// Runs on a worker; safe no-op when OS-session volume isn't used.
    /// </summary>
    private void ScheduleSessionVolumeReassert(long sessionId)
    {
        if (_sessionVolume == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // New output session on track start/swap — drop the previous track's
            // cached session so we re-resolve to the new active one (no accumulation).
            _sessionVolume.Invalidate();
            // The session is created a few ms after Play(); poll quickly so the
            // user level lands almost immediately instead of a 100% blip.
            for (var waited = 0; waited < 600; waited += 20)
            {
                if (_disposed || sessionId != CurrentSessionId) return;
                if (ReapplySessionVolume()) return;
                Thread.Sleep(20);
            }
        });
    }

    public void Pause()
    {
        if (_disposed) return;
        _keepAlive?.NotifyActivity();
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
        _keepAlive?.NotifyActivity();

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

        _keepAlive?.NotifyActivity();
        CancelSkipCts();
        CancelPreparedNext();
        ResetEndReachedPending();

        var len = _player.Length;
        if (len <= 0) return;

        var clampedMs = (long)Math.Clamp(position.TotalMilliseconds, 0, len);
        DebugLogger.Info(DebugLogger.Category.Playback, "Seek.Request", $"targetMs={clampedMs}, playerState={_player.State}");

        // Restart-instead-of-seek for the start region. An in-place backward seek
        // to the beginning desyncs LibVLC's mmdevice output clock on files with
        // encoder-delay priming: VLC logs "playback too early (-58000)" (the
        // inserted priming samples), abandons resampling, then drops + flushes
        // every buffer ~4×/sec permanently — the audible restart stutter, proven
        // in the VLC diagnostic log. A fresh Play() tears down and rebuilds the
        // audio output clock with correct priming, avoiding the desync. This
        // covers both the Previous-restart and drag-to-start paths, which both
        // funnel into Seek(~0). Forward / mid-track seeks keep the fast in-place
        // path below.
        if (clampedMs <= StartSeekRestartThresholdMs &&
            !string.IsNullOrEmpty(_currentMediaPath))
        {
            lock (_seekGate)
            {
                _latestSeekMs = -1; // discard any queued in-place seek for this drag
            }
            _positionTimer.Stop();
            // Apply the residual offset (e.g. drag to 0.4s) after the clean
            // restart; exact-zero restarts need no pending seek.
            Interlocked.Exchange(ref _pendingSeekMs, clampedMs > 0 ? clampedMs : -1);
            Play(_currentMediaPath);
            return;
        }

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

        // The timer runs only while audio is playing — it doubles as the
        // keep-alive's "still in use" heartbeat (cheap: one volatile write).
        _keepAlive?.NotifyActivity();

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
        Volatile.Write(ref _rampTargetMilli, -1); // stop the volume ramp worker

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
        if (_vlcDiagWriter != null)
        {
            try { _libVlc.Log -= OnVlcLog; } catch { }
            try { DebugLogger.EntryAdded -= OnDebugEntryForDiag; } catch { }
            lock (_vlcDiagLock)
            {
                try { _vlcDiagWriter.Flush(); _vlcDiagWriter.Dispose(); } catch { }
                _vlcDiagWriter = null;
            }
        }

        _sessionVolume?.Dispose();
        _wasapiOut?.Dispose();
        lock (_exclusiveSinkLock)
        {
            _exclusiveOut?.Dispose();
            _exclusiveOut = null;
        }
        _keepAlive?.Dispose();

        _currentMedia?.Dispose();
        _standbyMedia?.Dispose();
        _player.Dispose();
        _standbyPlayer.Dispose();
        _libVlc.Dispose();
        _playbackLock.Dispose();
    }

    // ── VLC diagnostics (gated; see field comment above) ───────────

    private void TryEnableVlcDiagnostics()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(dir, "noctis_vlc_diag.log");

            _vlcDiagWriter = new StreamWriter(path, append: false) { AutoFlush = true };
            _vlcDiagStartTicks = Stopwatch.GetTimestamp();

            WriteVlcDiag("=== Noctis VLC diagnostic log ===");
            WriteVlcDiag($"started {DateTime.Now:O}");
            WriteVlcDiag("Reproduce the stutter, then quit the app and send this file.");
            WriteVlcDiag("[APP] lines are our own markers; all other lines are LibVLC's own log.");
            WriteVlcDiag("--------------------------------------------------");

            // Mirror our Playback markers (Seek.Request / Seek.Apply / VLC.Play …)
            // into the same timeline so VLC's underrun lines can be tied to the
            // exact user action that triggered them.
            DebugLogger.IsEnabled = true;
            DebugLogger.EntryAdded += OnDebugEntryForDiag;

            _libVlc.Log += OnVlcLog;
        }
        catch
        {
            // Best-effort: diagnostics must never break playback.
            _vlcDiagWriter = null;
        }
    }

    private void OnDebugEntryForDiag(DebugLogger.LogEntry entry)
    {
        if (entry.Category != DebugLogger.Category.Playback) return;
        var meta = entry.Metadata != null ? $" | {entry.Metadata}" : "";
        WriteVlcDiag($"[APP] {entry.Action}{meta}");
    }

    private void OnVlcLog(object? sender, LogEventArgs e)
        => WriteVlcDiag($"[{e.Level}] {e.Module}: {e.Message}");

    private void WriteVlcDiag(string line)
    {
        var w = _vlcDiagWriter;
        if (w == null) return;
        var ms = (Stopwatch.GetTimestamp() - _vlcDiagStartTicks) * 1000L / Stopwatch.Frequency;
        lock (_vlcDiagLock)
        {
            try { w.WriteLine($"{ms,8} ms  {line}"); }
            catch { /* writer closing */ }
        }
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
