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
    // Writing _player.Time = Length makes VLC hit EOF and fire EndReached, which
    // advances/stops the track — so dragging the seek slider to the far right and
    // back would leave nothing playing. Hold every manual seek this far short of
    // the end so the track keeps playing and a drag-back resumes audio. Scaled
    // down for very short clips (see Seek()).
    private const long EndSeekGuardMs = 1000;
    // Brief volume fade-in after an in-place seek so the buffer-flush discontinuity
    // (audible as a click on every platform) is masked. This long, on the seek worker.
    private const int SeekFadeMs = 20;
    // Step size for FadePlayerVolumeFadeIn — fine enough that a 20ms seek fade still
    // gets several steps.
    private const int FadeInStepMs = 4;
    // Track-start fade-in on the native output (macOS/Linux) to mask the cold-device
    // clip — the first buffers are dropped while the audio device spins up.
    private const int TrackStartFadeMs = 40;
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
    // Gapless end-alignment: the handoff arrives ~0.5s before the outgoing input
    // ends (GaplessHandoffLeadSeconds); the pre-rolled standby is resumed only when
    // the outgoing is within UnpauseLead of its end (or has ended), so the seam is
    // neither a silence hole (resume too late) nor an overlap of both tracks on the
    // shared session (resume too early). The timeout caps the wait so a stalled
    // outgoing input can't wedge the handoff. Grace-deadline safety: EndReached
    // arms only when the outgoing actually ends, and the align loop exits within
    // one poll of that — so the armed window is at most unpause + standby warmup
    // (≤ StandbyWarmupTimeoutMs), well inside EndReachedGraceMs before the swap
    // clears it (see ResetEndReachedPending after the player swap).
    private const int GaplessEndAlignTimeoutMs = 800;
    private const int GaplessEndAlignPollMs = 10;
    private const int GaplessUnpauseLeadMs = 40;
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
    // suppresses it). macOS/Linux use VlcSilenceKeepAlive instead; null only on
    // NOCTIS_KEEPALIVE=0 / init failure.
    // See WasapiSilenceKeepAlive / VlcSilenceKeepAlive for the idle-park design.
    private readonly IAudioKeepAlive? _keepAlive;
    private MediaPlayer.LibVLCAudioPlayCb? _audioPlayCb;
    private MediaPlayer.LibVLCAudioPauseCb? _audioPauseCb;
    private MediaPlayer.LibVLCAudioResumeCb? _audioResumeCb;
    private MediaPlayer.LibVLCAudioFlushCb? _audioFlushCb;
    private MediaPlayer.LibVLCAudioDrainCb? _audioDrainCb;

    // ── True-gapless splice engine (NOCTIS_GAPLESS_ENGINE=1, Windows) ──
    // VLC 3 cannot do gapless: every input tears down and recreates its aout
    // stream, and two players can never be sample-aligned (independent clocks,
    // no latency feedback) — measured: a fully pre-rolled, end-aligned dual-
    // player handoff still gaps, because resume must decode the first frame
    // and cold-open a brand-new WASAPI stream. The engine instead has BOTH
    // players deliver S16N via amem into per-track segments spliced back-to-
    // back in ONE persistent shared-mode stream (GaplessSink) — the boundary
    // is crossed inside a single render read, zero inserted samples. Callbacks
    // are per-player closures (sender discrimination: the staging player's
    // transport events must never drive the shared stream) and are rooted in
    // these arrays for the players' lifetime (LibVLCSharp does not root them).
    // Slot = index into _enginePlayers, FIXED to the player objects while the
    // _player/_standbyPlayer roles swap around them.
    private bool _gaplessEngine;
    private GaplessSink? _gaplessSink;
    private readonly MediaPlayer[] _enginePlayers = new MediaPlayer[2];
    private readonly GaplessTrackSegment?[] _engineSegments = new GaplessTrackSegment?[2];
    private readonly long[] _enginePendingBaseMs = new long[2];
    // Diagnostics: expected pts of the next amem block per slot (µs); 0 = head
    // block pending. A jump between consecutive blocks means VLC dropped audio
    // upstream and the hole is butt-spliced into the ring.
    private readonly long[] _engineExpectedPts = new long[2];
    private FileStream? _engineInTap; // NOCTIS_ENGINE_TAP input-side capture (raw s16le)
    private readonly object _engineInTapLock = new();
    private int _engineInTapSinceFlush;
    private readonly MediaPlayer.LibVLCAudioPlayCb?[] _enginePlayCbs = new MediaPlayer.LibVLCAudioPlayCb?[2];
    private readonly MediaPlayer.LibVLCAudioPauseCb?[] _enginePauseCbs = new MediaPlayer.LibVLCAudioPauseCb?[2];
    private readonly MediaPlayer.LibVLCAudioResumeCb?[] _engineResumeCbs = new MediaPlayer.LibVLCAudioResumeCb?[2];
    private readonly MediaPlayer.LibVLCAudioFlushCb?[] _engineFlushCbs = new MediaPlayer.LibVLCAudioFlushCb?[2];
    private readonly MediaPlayer.LibVLCAudioDrainCb?[] _engineDrainCbs = new MediaPlayer.LibVLCAudioDrainCb?[2];
    private string? _engineStagedPath;

    // Settings-driven WASAPI exclusive output (Windows). When enabled, LibVLC's
    // decoded PCM is routed via the audio callbacks to an exclusive-mode sink
    // opened at the SOURCE sample rate (see PrepareExclusiveOutputFor). Like the
    // experimental _wasapiOut path this is single-stream: crossfade and
    // standby-prepare are gated off while enabled. The sink is created lazily
    // per track before Play and reused across tracks with the same rate; an
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
    // volatile: Dispose writes this from the UI thread while the volume-ramp worker, the
    // EQ queue, both fade loops and the seek worker read it in spin loops with no barrier.
    // The other shutdown flags in this class (_positionTickActive, _seekWorkerActive) are
    // already volatile; this one was the outlier.
    private volatile bool _disposed;

    // ── VLC internal-log diagnostics (gated by env NOCTIS_VLC_LOG=1) ──
    // Off by default and zero-cost. When enabled, LibVLC's OWN log (decoder,
    // audio-output / mmdevice underrun warnings, seek/flush messages) is
    // written to a file alongside our Playback markers, so the exact failure
    // at a stutter moment can be read directly instead of guessed at. Pure
    // instrumentation — it does not alter the playback path.
    private StreamWriter? _vlcDiagWriter;
    private readonly object _vlcDiagLock = new();
    private long _vlcDiagStartTicks;

    // ── Dev-mode VLC log bridge (Settings → Developer Mode session log) ──
    // Mirrors LibVLC Warning/Error lines into DebugLog so in-app "Copy Logs"
    // captures audio-engine complaints without the NOCTIS_VLC_LOG env var.
    // The VLC log callback is subscribed only while Developer Mode is on.
    private bool _devBridgeAttached;
    private readonly object _devBridgeLock = new();
    private string? _devBridgeLastMsg;
    private long _devBridgeLastTicks;

    // Pre-error context ring. Whatever levels VLC emits land here in memory (no I/O)
    // and the last DevBridgeRingSize are flushed to the session log when an Error
    // arrives, recovering two things the plain bridge throws away: sub-second ordering
    // (the session log stamps only to the second, so a dropout cluster arrives
    // unordered) and the un-collapsed run of warnings before the error, which is what
    // separates a one-off from a spiral. NOTE: at the default verbosity VLC emits only
    // warnings and errors to the callback, so the Debug-level mmdevice underrun /
    // demux-timing chatter that would settle input-vs-output starvation still needs
    // NOCTIS_VLC_LOG=1 (which adds --verbose=2 at construction).
    private const int DevBridgeRingSize = 40;
    private const int DevBridgeContextCooldownSec = 60;
    private readonly string?[] _devBridgeRing = new string?[DevBridgeRingSize];
    private int _devBridgeRingNext;
    private long _devBridgeLastContextTicks;

    // Serializes Play/Stop operations so rapid track switching
    // (e.g. spamming Next) doesn't overlap Stop+Play calls.
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    // Timer for polling position (100ms → 10Hz for smooth seek bar updates).
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

    // Seek() restarts the track for near-start seeks (see StartSeekRestartThresholdMs).
    // When that restart happens while paused, the new input must open paused too —
    // otherwise dragging the slider to the beginning while paused starts playback.
    private volatile bool _restartPausedRequest;

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
    /// <summary>Whether Song Transitions is switched on in Settings. Configuration only.</summary>
    private bool _crossfadeEnabled;

    /// <summary>
    /// True only while a transition fade is actually ramping the volume. The volume
    /// setters park on this so a user slider move can't fight the ramp.
    ///
    /// This used to be <see cref="_crossfadeEnabled"/>, which conflated "the feature is
    /// configured" with "a fade is running": ApplyAudioSettings arms _crossfadeEnabled
    /// from ~9 settings handlers, so simply switching Song Transitions on while a track
    /// was loaded latched the guard true and every subsequent volume write was silently
    /// swallowed until the next PlayInternal — the slider moved and nothing happened.
    /// </summary>
    private volatile bool _transitionInFlight;
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
                // Plugins live beside the dylibs (homebrew-style lib/plugins) or as
                // a sibling of lib/ (VLC.app and our bundled Contents/MacOS/libvlc
                // layout both use MacOS/{lib,plugins}) — probe both shapes.
                var pluginsPath = Path.Combine(macLibPath, "plugins");
                if (!Directory.Exists(pluginsPath))
                    pluginsPath = Path.GetFullPath(Path.Combine(macLibPath, "..", "plugins"));
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
        //                           NOT forced on Linux system-libvlc installs: distros
        //                           split VLC's plugins into packages (Arch ships avformat
        //                           only in the optional vlc-plugin-ffmpeg package), and
        //                           forcing a demux module that isn't installed makes EVERY
        //                           media open fail with "VLC is unable to open the MRL"
        //                           even though the file exists (issue #26). The AppImage
        //                           bundles the full plugin set and re-enables the flag via
        //                           NOCTIS_BUNDLED_VLC=1 (see ShouldForceAvformatDemux).
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
            "--no-audio-time-stretch",
            $"--file-caching={cachingMs}",
            $"--disc-caching={cachingMs}",
            $"--live-caching={cachingMs}",
            $"--network-caching={cachingMs}",
        };
        // See the --demux=avformat note above: only forced where the avformat
        // plugin is guaranteed to exist (Windows/macOS payloads bundle it; the
        // Linux AppImage's AppRun sets NOCTIS_BUNDLED_VLC=1). Plain Linux
        // system-libvlc installs keep VLC's native demuxers so a split plugin
        // set still plays.
        if (ShouldForceAvformatDemux(
                OperatingSystem.IsLinux(),
                Environment.GetEnvironmentVariable("NOCTIS_BUNDLED_VLC")))
            vlcArgs.Add("--demux=avformat");
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

        // Dev-mode bridge: attach now if Developer Mode was already on at
        // startup, and follow later toggles for the player's lifetime.
        DebugLog.VlcBridgeChanged += OnVlcBridgeChanged;
        OnVlcBridgeChanged();

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
                    // output rate/channels. VLC 3.x's amem ignores the format string
                    // and always delivers S16N — the sink's input chain matches.
                    _player.SetAudioCallbacks(_audioPlayCb, _audioPauseCb, _audioResumeCb, _audioFlushCb, _audioDrainCb);
                    _player.SetAudioFormat("S16N", (uint)wasapi.SampleRate, (uint)wasapi.Channels);
                    _callbackChannels = wasapi.Channels;
                    _player.Volume = 100;
                    WasapiGainOutput.Diag($"VlcAudioPlayer wired callbacks: S16N {wasapi.SampleRate}Hz {wasapi.Channels}ch");
                }
                catch (Exception ex)
                {
                    WasapiGainOutput.Diag($"VlcAudioPlayer wiring FAILED: {ex.GetType().Name}: {ex.Message}");
                    _callbackChannels = 0;
                    try { wasapi.Dispose(); } catch { }
                    wasapi = null;
                    // The delegate fields are deliberately NOT nulled here.
                    // SetAudioCallbacks has already handed libvlc the native thunks for
                    // this MediaPlayer, and LibVLCSharp does not root them (see the class
                    // comment above). Dropping our only references would make them
                    // collectable while libvlc still holds the function pointers, so the
                    // next amem callback would call freed memory. Dropping the sink is
                    // enough — AudioPlay no-ops when ActiveCallbackSink is null.
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

        // Splice-engine wiring (dev-gated). Fixed format at the device mix
        // rate: VLC resamples upstream (as the Windows mixer would anyway), so
        // every segment arrives sink-format and the splice is pure
        // concatenation. Volume stays on the OS session — the sink's stream
        // lives in the same process session the existing machinery drives.
        // Default ON (Windows): an env-var opt-in dies silently when the app is
        // launched from Explorer (no terminal environment), which reads as "the
        // fix does nothing". NOCTIS_GAPLESS_ENGINE=0 is the escape hatch; sink
        // creation failure still falls back to the classic path automatically.
        _gaplessEngine = OperatingSystem.IsWindows() && _wasapiOut == null &&
                         Environment.GetEnvironmentVariable("NOCTIS_GAPLESS_ENGINE") != "0";
        if (_gaplessEngine)
        {
            _gaplessSink = GaplessSink.TryCreate();
            if (_gaplessSink == null)
            {
                _gaplessEngine = false;
            }
            else
            {
                _enginePlayers[0] = _player;
                _enginePlayers[1] = _standbyPlayer;
                try
                {
                    for (var slot = 0; slot < 2; slot++)
                    {
                        var s = slot;
                        _enginePlayCbs[s] = (data, samples, count, pts) => EnginePlay(s, samples, count, pts);
                        _enginePauseCbs[s] = (data, pts) => { };
                        _engineResumeCbs[s] = (data, pts) => { };
                        _engineFlushCbs[s] = (data, pts) => EngineFlush(s);
                        _engineDrainCbs[s] = data => EngineDrain(s);
                        _enginePlayers[s].SetAudioCallbacks(
                            _enginePlayCbs[s]!, _enginePauseCbs[s]!, _engineResumeCbs[s]!,
                            _engineFlushCbs[s]!, _engineDrainCbs[s]!);
                        _enginePlayers[s].SetAudioFormat("S16N", (uint)_gaplessSink.SampleRate, (uint)_gaplessSink.Channels);
                        _enginePlayers[s].Volume = 100;
                    }
                    DebugLogger.Info(
                        DebugLogger.Category.Playback,
                        "GaplessEngine.Wired",
                        $"rate={_gaplessSink.SampleRate}, channels={_gaplessSink.Channels}");
                    // NOCTIS_ENGINE_TAP: also capture what VLC DELIVERS (pre-ring),
                    // so render-side glitches can be attributed upstream vs ours.
                    // Raw s16le at sink rate/channels — headerless survives kill.
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOCTIS_ENGINE_TAP")))
                    {
                        try
                        {
                            _engineInTap = new FileStream(
                                Path.Combine(Path.GetTempPath(), "noctis-engine-in-tap.raw"),
                                FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                        }
                        catch { /* diagnostic only */ }
                    }
                }
                catch (Exception ex)
                {
                    // Delegates deliberately NOT nulled (see the _audioPlayCb note):
                    // libvlc may already hold thunks. Dropping the sink is enough —
                    // EnginePlay no-ops when _gaplessSink is null.
                    DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.WireFailed", ex.Message);
                    try { _gaplessSink.Dispose(); } catch { }
                    _gaplessSink = null;
                    _gaplessEngine = false;
                }
            }
        }

        // Start the keep-alive immediately: construction happens at app launch,
        // which is exactly the window before the reported first-play stutter.
        // Windows uses a silent WASAPI stream; macOS/Linux use a silent looping
        // LibVLC player (see VlcSilenceKeepAlive) — both keep the device warm so
        // the first Play() / every transition opens against a running endpoint.
        _keepAlive = OperatingSystem.IsWindows()
            ? WasapiSilenceKeepAlive.TryStart()
            : VlcSilenceKeepAlive.TryStart(_libVlc);

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

    // User's mute intent, tracked separately from _player.Mute: on PulseAudio /
    // PipeWire the OS can restore a muted state onto a freshly created stream
    // (stream-restore entries are per-app, and older builds' keep-alive stream
    // recorded itself muted there), so _player.Mute can read true when the user
    // never muted. PlayInternal re-asserts this intent on every play.
    private bool _userMuted;

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
            if (_transitionInFlight && _currentMedia != null)
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
            if (_transitionInFlight && _currentMedia != null)
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
        if (_transitionInFlight && _currentMedia != null)
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
                _volumeTrailingCts?.Dispose();
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
            _userMuted = value;
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
    /// silently — the outer call is the one that actually writes. Returns false when
    /// the write was dropped by the guard.
    /// </summary>
    private bool SetPlayerVolumeGuarded(MediaPlayer player, int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (Interlocked.CompareExchange(ref _applyingVolume, 1, 0) != 0)
            return false; // reentrant write from VolumeChanged echo — drop it
        try { player.Volume = clamped; }
        catch { /* player disposed / transitioning */ }
        finally { Interlocked.Exchange(ref _applyingVolume, 0); }
        return true;
    }

    /// <summary>
    /// A volume write that must not be lost. The guard above drops concurrent
    /// writers silently — correct for VolumeChanged echoes, but the handoff writes
    /// that un-park a standby player from its parked volume 0 are contended at
    /// exactly that moment (ramp ticks, the metadata-save VolumeAdjust write), and
    /// a dropped un-park leaves the entire next track silent until a manual skip
    /// rebuilds the session. Waits out the other writer instead; a property write
    /// holds the guard for microseconds. Never called from a VolumeChanged frame,
    /// so it cannot wait on its own thread.
    /// </summary>
    private void SetPlayerVolumeInsistent(MediaPlayer player, int value)
    {
        for (var tries = 0; tries < 50; tries++)
        {
            if (SetPlayerVolumeGuarded(player, value)) return;
            Thread.Sleep(1);
        }
        DebugLogger.Warn(DebugLogger.Category.Playback, "Volume.GuardContended",
            $"insistent volume write still dropped after 50 retries; value={value}");
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
            _volumeTrailingCts?.Dispose();
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
    /// Fades the player volume from silence up to <paramref name="toVolume"/> over
    /// <paramref name="durationMs"/>, masking an audio-output discontinuity (the
    /// click after an in-place seek, or the cold-device drop at track start). Stops
    /// stepping the instant a Play() swaps the track (<paramref name="expectedMedia"/>
    /// no longer current) so it can never fight PlayInternal's own ramp for the
    /// incoming track. Uses fine ~4ms steps — distinct from
    /// <see cref="FadePlayerVolumeBlocking"/>, whose 35ms crossfade steps are far too
    /// coarse for a sub-frame fade. Native / per-player volume path only.
    ///
    /// The landing write is insistent and happens on EVERY exit path including the
    /// bail — see <see cref="RunVolumeFadeIn"/> for why.
    /// </summary>
    private void FadePlayerVolumeFadeIn(int toVolume, int durationMs, Media? expectedMedia)
    {
        RunVolumeFadeIn(
            toVolume,
            durationMs,
            FadeInStepMs,
            () => !_disposed && ReferenceEquals(_currentMedia, expectedMedia),
            v => SetPlayerVolumeGuarded(_player, v),
            v => { if (!_disposed) SetPlayerVolumeInsistent(_player, v); },
            () => Thread.Sleep(FadeInStepMs));
    }

    /// <summary>
    /// The step schedule of <see cref="FadePlayerVolumeFadeIn"/>, with the player
    /// writes injected so the landing guarantee is testable without LibVLC.
    ///
    /// On the native path (macOS/Linux — no OS-session handle, no callback sink)
    /// this fade is the ONLY thing that un-parks MediaPlayer.Volume from the 0 its
    /// caller just wrote, and nothing else ever re-asserts it:
    /// ScheduleSessionVolumeReassert returns immediately while _sessionVolume is
    /// null, and ScheduleMuteIntentReassert only touches Mute. So a run that ends
    /// below <paramref name="toVolume"/> leaves the track quiet — or, if it stopped
    /// before its first step, completely silent — for the rest of its duration,
    /// until the user forces a new track. That is the "plays but no audio, skip
    /// forward then back and it works" report: PlayInternal recomputes the volume
    /// from GetTargetVlcVolume() on the next track, which heals it.
    ///
    /// Two ways the old loop ended low, both fixed here:
    ///   * it bailed with a bare return, stranding the player at the partial step it
    ///     had reached (0 when the very first check failed). A seek worker racing a
    ///     track change hits exactly this: the worker parks the volume, the media
    ///     reference then changes under it, and the fade that was supposed to give
    ///     the volume back walks away. Landing on <paramref name="toVolume"/> is safe
    ///     even when an incoming track's own fade is in flight — both call sites now
    ///     target GetTargetVlcVolume(), so the two are converging on the same value.
    ///   * its final step used the droppable guarded write, which returns false and
    ///     writes nothing under contention (a ramp tick, the metadata VolumeAdjust
    ///     write). That left the player one step short — and while the post-seek
    ///     restore was read live off MediaPlayer.Volume, each short run became the
    ///     next seek's ceiling, so scrubbing ratcheted the volume down to nothing.
    /// </summary>
    /// <param name="shouldContinue">False once the track swapped or we are disposing.</param>
    /// <param name="step">Droppable guarded write, for the intermediate steps.</param>
    /// <param name="land">Insistent write; must not be lost.</param>
    public static void RunVolumeFadeIn(
        int toVolume, int durationMs, int stepMs,
        Func<bool> shouldContinue, Action<int> step, Action<int> land, Action sleep)
    {
        var steps = Math.Max(1, durationMs / Math.Max(1, stepMs));

        for (var i = 1; i < steps; i++)
        {
            if (!shouldContinue())
            {
                land(toVolume);
                return;
            }
            step(toVolume * i / steps);
            sleep();
        }

        // The target itself never rides a droppable write.
        land(toVolume);
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
                try
                {
                    ApplyAdvancedEqualizerSnapshot(version);
                }
                catch (Exception ex)
                {
                    // Count a failed apply as consumed: retrying the same snapshot
                    // throws the same way, and the finally-requeue below then
                    // respawned this task in a tight loop (~10K faulted tasks/s
                    // when SetEqualizer(null) NRE'd) until memory ran out. Log
                    // and wait for a genuinely new request instead.
                    DebugLogger.Error(DebugLogger.Category.Playback, "EQ.Apply", ex.Message);
                }
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

    /// <summary>
    /// A curve that leaves the signal untouched: every band and the preamp at 0 dB
    /// (within the 0.05 dB the UI can express). Such a curve is bypassed entirely
    /// rather than routed through VLC's equalizer — see the apply path below.
    /// </summary>
    private static bool IsFlatCurve(float[] bands, float preamp)
    {
        if (Math.Abs(preamp) >= 0.05f) return false;
        for (var i = 0; i < bands.Length; i++)
            if (Math.Abs(bands[i]) >= 0.05f) return false;
        return true;
    }

    /// <summary>
    /// True only when the equalizer actually alters the signal. The master toggle
    /// alone is not enough: the default "Flat" preset is applied as a true bypass,
    /// so an enabled-but-flat EQ changes nothing and must not count as DSP.
    /// </summary>
    public bool EqualizerActive
    {
        get
        {
            lock (_equalizerLock)
                return _advancedEqEnabled && !IsFlatCurve(_advancedEqBands, _advancedEqPreamp);
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

        // A flat curve (every band 0 dB and 0 preamp — e.g. the default "Flat"
        // preset, which has no UI "off" switch) is applied as a true bypass via
        // the UnsetEqualizer branch below rather than routed through VLC's
        // equalizer. VLC's EQ filter scales its input by EQZ_IN_FACTOR (0.25 =
        // −12 dB) and relies on the preamp for make-up, so a flat curve at
        // preamp 0 plays ~12 dB under native (the "quieter than other players"
        // reports); bypassing keeps Flat at unity. Non-flat curves carry
        // ParametricEqMath.VlcEqUnityPreampDb (or the preset's own preamp) as
        // the make-up instead.
        var isFlat = IsFlatCurve(bands, preamp);

        if (enabled && !isFlat)
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
                // Removing "equalizer" from a LIVE output's filter chain forces
                // an output restart — an audible ~1s dropout (VLC's FilterCallback
                // only restarts when the audio-filter string CHANGES). So while a
                // track is playing with a filter engaged, neutralize it to a
                // unity-flat curve instead of unsetting: mathematically transparent
                // (out = 4·(0.25·x + 0) = x), same level as true bypass, no restart.
                // The filter is genuinely removed the next time a flat/disabled
                // curve lands while nothing is playing (e.g. app start).
                if (_equalizer != null && _player is { IsPlaying: true })
                {
                    _equalizer.SetPreamp(ParametricEqMath.VlcEqUnityPreampDb);
                    for (uint i = 0; i < 10; i++)
                        _equalizer.SetAmp(0f, i);
                    _player.SetEqualizer(_equalizer);
                    if (_standbyPrepared)
                        _standbyPlayer.SetEqualizer(_equalizer);
                }
                else
                {
                    // UnsetEqualizer, not SetEqualizer(null): LibVLCSharp dereferences
                    // the argument unconditionally, so the null form throws NRE on
                    // every call — which the apply queue then retried forever (see
                    // ProcessAdvancedEqualizerQueue).
                    if (_player != null)
                        _player.UnsetEqualizer();
                    if (_standbyPrepared)
                        _standbyPlayer.UnsetEqualizer();
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
        // The splice engine's callbacks die with the rebuilt players; disable it
        // (until app restart) so segment bookkeeping can't run against players
        // that no longer deliver PCM — that would render silence forever.
        if (_gaplessEngine)
        {
            _gaplessEngine = false;
            EngineClearAll();
            try { _gaplessSink?.Dispose(); } catch { }
            _gaplessSink = null;
            DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.Disabled", "output mode rebuilt");
        }

        var wasActive = _currentMedia != null && (_player.IsPlaying || _isPaused);
        var resumePath = _currentMediaPath;
        long resumeMs = 0;
        if (wasActive)
        {
            try { resumeMs = Math.Max(0, _player.Time); } catch { }
        }

        ResetEndReachedPending();
        lock (_seekGate) { _latestSeekMs = -1; }
        _positionTimer.Stop();
        DrainPositionTimerCallback();
        DrainSeekWorker();
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
        // Deferred disposal: UI property getters (State/Duration/Position) and any
        // straggler VLC thread read _player without a lock, so freeing the handle
        // here could still be a use-after-free. Swap the field first; reclaim the
        // old instances once no reader can plausibly still hold them.
        DisposePlayerDeferred(_player);
        DisposePlayerDeferred(_standbyPlayer);

        _player = new MediaPlayer(_libVlc);
        _standbyPlayer = new MediaPlayer(_libVlc);
        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnError;
        _standbyPlayer.EndReached += OnEndReached;
        _standbyPlayer.EncounteredError += OnError;

        if (exclusive)
        {
            _audioPlayCb ??= AudioPlay;
            _audioPauseCb ??= AudioPause;
            _audioResumeCb ??= AudioResume;
            _audioFlushCb ??= AudioFlush;
            _audioDrainCb ??= AudioDrain;
            // NOT SetAudioFormatCallback: VLC 3.x's amem module hard-rejects any
            // format but "S16N" from the dynamic setup callback (strcmp in
            // amem.c Start) — writing "FL32" there makes the aout fail entirely
            // (dead silence). The fixed-format API works, but its format string
            // is equally ignored (amem always outputs S16N); only the
            // rate/channels vars matter, pinned per track before Play
            // (PrepareExclusiveOutputFor). This provisional value only covers
            // the window until the first Play.
            _player.SetAudioCallbacks(_audioPlayCb, _audioPauseCb, _audioResumeCb, _audioFlushCb, _audioDrainCb);
            try { _player.SetAudioFormat("S16N", 44100, 2); _callbackChannels = 2; }
            catch { _callbackChannels = 0; }
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
    /// Exclusive mode, per track before Play: open (or reuse) the sink at the
    /// track's source rate and pin LibVLC's output format to it. On an exclusive
    /// open failure falls back to a shared-mode sink so playback continues,
    /// raising OutputModeChanged either way.
    ///
    /// This deliberately does NOT use libvlc's dynamic format callback: VLC 3.x's
    /// amem module hard-rejects any format but "S16N" from that callback
    /// (strcmp in amem.c Start — "TODO: amem-format"). The fixed-format API is
    /// used instead for its rate/channels vars, which ARE read at each aout
    /// start; the sample format is always S16N regardless (16-bit sources stay
    /// bit-perfect; >16-bit content is truncated upstream by VLC 3.x — a hard
    /// LibVLC limitation). The track's rate comes from the already-parsed Media.
    /// </summary>
    private void PrepareExclusiveOutputFor(Media media)
    {
        try
        {
            int rate = 44100, channels = 2;
            foreach (var t in media.Tracks)
            {
                if (t.TrackType != TrackType.Audio) continue;
                if (t.Data.Audio.Rate > 0) rate = (int)t.Data.Audio.Rate;
                if (t.Data.Audio.Channels > 0) channels = (int)t.Data.Audio.Channels;
                break;
            }
            // amem rejects rates above 384 kHz; the sink renders at most stereo
            // (LibVLC downmixes to what we pin below).
            rate = Math.Clamp(rate, 8000, 384000);
            channels = Math.Clamp(channels, 1, 2);

            string? notice = null;
            int sinkRate, sinkChannels;

            lock (_exclusiveSinkLock)
            {
                // Reuse the open device stream when the format matches; otherwise
                // close it (rate change, or a shared fallback that can retry
                // exclusive now that the device may be free).
                if (_exclusiveOut != null &&
                    (!_exclusiveOut.IsExclusive ||
                     _exclusiveOut.SampleRate != rate ||
                     _exclusiveOut.Channels != channels))
                {
                    _exclusiveOut.Dispose();
                    _exclusiveOut = null;
                }

                if (_exclusiveOut == null)
                {
                    var sink = WasapiGainOutput.TryCreateExclusive(rate, channels, out var reason);
                    if (sink != null)
                    {
                        notice = $"Exclusive output active — {sink.SampleRate / 1000.0:0.#} kHz / {(sink.BitsPerSample == 32 ? "32-bit float" : $"{sink.BitsPerSample}-bit")}";
                    }
                    else
                    {
                        sink = WasapiGainOutput.TryCreate();
                        // Read as an error before: it said what failed but not that
                        // playback is fine, nor that the next track retries exclusive
                        // (the reuse check above drops a shared fallback sink).
                        notice = $"Playing through the shared system mixer — {reason}. " +
                                 "Audio is unaffected; exclusive output is retried when the next track starts.";
                    }

                    if (sink == null)
                    {
                        // No usable output at all. amem *succeeded*, so VLC reports
                        // nothing and AudioPlay silently drops every buffer: the clock
                        // runs, the position bar and lyrics advance, and there is no
                        // sound and no message. Turn exclusive mode off and rebuild on
                        // LibVLC's own mmdevice output so the user gets audio back, and
                        // say why.
                        DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.SetupFailed",
                            "no WASAPI output available");
                        _exclusiveModeEnabled = false;
                        RebuildOutputModeLocked(false);
                        OutputModeChanged?.Invoke(this,
                            "No audio output could be opened in exclusive mode — " +
                            "Exclusive Mode was turned off and playback moved back to the shared system mixer.");
                        return;
                    }

                    _exclusiveOut = sink;
                    sink.SetGainTarget(WasapiGainLevel());

                    // A device that disappears mid-track (USB DAC unplugged, Bluetooth
                    // drop, default device switched) otherwise leaves this sink wedged
                    // forever. Fall back to shared output instead of going silent.
                    sink.Faulted += OnExclusiveSinkFaulted;
                }

                sinkRate = _exclusiveOut.SampleRate;
                sinkChannels = _exclusiveOut.Channels;

                // RebuildOutputModeLocked parks the silent keep-warm stream on the
                // assumption the endpoint is held exclusively. When the exclusive open
                // failed and we fell back to a shared sink, nothing un-parked it, so the
                // cold-device protection stayed off for the rest of the session.
                _keepAlive?.SetSuspended(_exclusiveOut.IsExclusive);
            }

            // The format string is IGNORED by VLC 3.x's amem ("TODO: amem-format"
            // — it always outputs S16N); only the rate/channels vars are read.
            // "S16N" is passed so the call documents what actually flows.
            //
            // AudioPlay sizes its Marshal.Copy from the channel count VLC was told to
            // deliver, not from whatever sink happens to be installed: the sink is
            // assigned above, before this call, and this call is inside the method-wide
            // try below. If it threw after a 1ch → 2ch sink swap, VLC would keep sending
            // mono blocks while AudioPlay read twice the block length — the same
            // over-read that produced the 0x80131506 exclusive-mode crash.
            try
            {
                _player.SetAudioFormat("S16N", (uint)sinkRate, (uint)sinkChannels);
                _callbackChannels = sinkChannels;
            }
            catch (Exception ex)
            {
                // The format was not pinned, so nothing can be said about the block
                // layout VLC will deliver. Drop the sink: AudioPlay no-ops when
                // ActiveCallbackSink is null, which is silence rather than an over-read
                // on libvlc's aout thread.
                DebugLogger.Error(DebugLogger.Category.Playback, "Exclusive.FormatFailed",
                    $"{ex.GetType().Name}: {ex.Message}");
                _callbackChannels = 0;
                lock (_exclusiveSinkLock)
                {
                    try { _exclusiveOut?.Dispose(); } catch { }
                    _exclusiveOut = null;
                }
                throw;
            }

            if (notice != null)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "Exclusive.Setup", notice);
                OutputModeChanged?.Invoke(this, notice);
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.SetupFailed", ex.Message);
        }
    }

    /// <summary>
    /// Channel count last accepted by <c>SetAudioFormat</c>, i.e. what libvlc's amem is
    /// actually delivering. 0 when no format is pinned. Read on libvlc's aout thread.
    /// </summary>
    private volatile int _callbackChannels;

    /// <summary>
    /// Raised on NAudio's thread when the exclusive endpoint goes away. Drops exclusive
    /// mode and re-opens on the current default device; without this the sink's buffer
    /// never drains again and every LibVLC audio callback stalls for two seconds.
    /// </summary>
    private void OnExclusiveSinkFaulted(WasapiGainOutput sink)
    {
        if (_disposed) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_disposed || !ReferenceEquals(_exclusiveOut, sink)) return;
                DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.DeviceLost",
                    "output device disappeared; falling back to shared output");
                _exclusiveModeEnabled = false;
                if (!_playbackLock.Wait(2000)) return;
                try { RebuildOutputModeLocked(false); }
                finally { _playbackLock.Release(); }
                OutputModeChanged?.Invoke(this,
                    "Audio device disconnected — switched back to shared output.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "Exclusive.RecoverFailed", ex.Message);
            }
        });
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

        var (track, album) = ReadReplayGainTagsCached(_currentMediaPath);
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
        if (_transitionInFlight && _currentMedia != null) return;
        var target = ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100));
        target = ApplyReplayGainScalar(target);
        ScheduleVolumeWrite(target);
    }

    private int ApplyReplayGainScalar(int curvedVolume)
    {
        if (Math.Abs(_replayGainScalar - 1.0) < 0.0001) return curvedVolume;
        // _replayGainScalar is an AMPLITUDE ratio (10^(dB/20)), but every consumer
        // of this value is mapped to amplitude through the mmdevice cubic taper
        // afterwards (CurvedVolumeToLevelMilli / WasapiGainLevel cube ÷100, the
        // player-volume paths are re-cubed by the aout) — multiplying it in here
        // raw meant the cube applied scalar³: every ReplayGain dB landed ×3, so a
        // −8.4 dB loudness tag wrote the session to 0.055 (mixer row "5") instead
        // of 0.38. Fold in the CUBE ROOT so the taper yields exactly scalar×.
        var scaled = (int)Math.Round(curvedVolume * Math.Cbrt(_replayGainScalar));
        return Math.Clamp(scaled, 0, 100);
    }

    // Cached ReplayGain tags for the currently loaded file. The values only change when
    // the track changes, but ApplyReplayGain is called from ~9 settings handlers and,
    // worse, once per tick while dragging the ReplayGain pre-amp slider — each call was
    // a blocking TagLib open+parse of the playing file on the UI thread, which on a
    // NAS/removable-drive library stalled the whole window.
    private readonly object _rgCacheLock = new();
    private string? _rgCachePath;
    private (double? track, double? album) _rgCacheValue;

    private (double? track, double? album) ReadReplayGainTagsCached(string filePath)
    {
        lock (_rgCacheLock)
        {
            if (string.Equals(_rgCachePath, filePath, StringComparison.OrdinalIgnoreCase))
                return _rgCacheValue;
        }

        var parsed = ReadReplayGainTags(filePath);

        lock (_rgCacheLock)
        {
            _rgCachePath = filePath;
            _rgCacheValue = parsed;
        }
        return parsed;
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
            var prepareStart = Environment.TickCount64;
            DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.DualPrepareStart", $"path={Path.GetFileName(normalizedPath)}, startMs={startPositionMs}");

            if (_disposed || _currentMedia == null)
                return;

            // Parse OUTSIDE _playbackLock: the wait can block up to 8 s on slow media
            // (NAS, spun-down HDD) and every other playback entry point queues behind
            // the lock, so holding it here made Next/Resume/Stop wait out the parse.
            // The skip token aborts the wait so a user skip isn't held up either.
            CancellationToken cancel;
            try { cancel = _skipCts.Token; }
            catch (ObjectDisposedException) { return; }
            // _skipCts is re-armed only by the next Play(): after a pause/seek it
            // stays CANCELLED for the rest of the track, so a prepare STARTING
            // after that point captured a stale cancellation and aborted before
            // parsing — silently killing the gapless/AutoMix standby for the
            // upcoming transition (log signature: DualPrepareStart with no
            // DualPrepared, then the cold-open fallback at the seam). The token
            // models "a skip superseded THIS prepare"; a cancellation that
            // predates the prepare doesn't. Late supersedes are still handled by
            // the under-lock re-validation and the caller's CancelPreparedNext.
            if (cancel.IsCancellationRequested)
                cancel = CancellationToken.None;

            Media media;
            try
            {
                media = new Media(_libVlc, normalizedPath, FromType.FromPath);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", ex.Message);
                return;
            }

            try
            {
                if (_normalizationEnabled)
                {
                    media.AddOption(":audio-replay-gain-mode=track");
                    media.AddOption(":audio-replay-gain-preamp=0.0");
                    media.AddOption(":audio-replay-gain-default=-7.0");
                }

                var parseTask = media.Parse(MediaParseOptions.ParseLocal, timeout: 8000);
                bool parsed;
                try
                {
                    parsed = parseTask.Wait(8000, cancel) && parseTask.Result == MediaParsedStatus.Done;
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a skip/track change while parsing.
                    DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.DualPrepareCancelled", "parse superseded by skip");
                    media.Dispose();
                    return;
                }
                if (!parsed)
                {
                    media.Dispose();
                    DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", $"path={Path.GetFileName(normalizedPath)}");
                    return;
                }
            }
            catch (Exception ex)
            {
                media.Dispose();
                DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.DualPrepareFailed", ex.Message);
                return;
            }

            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { media.Dispose(); return; }

            try
            {
                // Re-validate under the lock — a Play/Stop or a duplicate prepare may
                // have superseded this parse while it ran unlocked.
                var duplicate = _standbyPrepared &&
                    string.Equals(_standbyPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    _standbyStartPositionMs == startPositionMs;
                if (_disposed || _currentMedia == null || cancel.IsCancellationRequested || duplicate)
                {
                    if (!_disposed && !duplicate)
                        DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.DualPrepareCancelled", "superseded before store");
                    media.Dispose();
                    return;
                }

                ReleasePreparedNext();

                _standbyMedia = media;
                _standbyPath = normalizedPath;
                _standbyStartPositionMs = startPositionMs;
                Interlocked.Exchange(ref _standbyPreparedTicksUtc, DateTime.UtcNow.Ticks);
                _standbyPrepared = true;
                // Gapless (no crossfade) pre-rolls the standby below, so its volume
                // write can go LIVE on the shared Windows session instead of being
                // cached against a closed aout. Park it at the session-open level
                // there, not 0 — a 0 that lands on (or is cached into) the shared
                // session silences the CURRENT track, the exact failure mode of the
                // old handoff zeroing (see QueueInactivePlayerCleanup). The paused
                // pre-roll itself is what keeps the standby silent. All other paths
                // keep the parked 0 (per-player volumes are independent, and a
                // stopped standby's write stays cached until its aout opens).
                var preRoll = !_gaplessEngine && _gaplessEnabled && !_crossfadeEnabled;
                // Insistent when pre-rolling: ReleasePreparedNext just cached a 0
                // into the stopped standby, and if this re-park write is dropped by
                // the one-shot guard (ramp tick / VolumeAdjust write in flight) the
                // pre-roll opens its aout with cached 0 → shared session zeroed →
                // current track silenced. Parse-only prepares keep the droppable
                // write (their cached 0 is the intended park). Engine mode: amem
                // player volume is a software gain on the PCM — pin at 100.
                if (_gaplessEngine)
                    SetPlayerVolumeInsistent(_standbyPlayer, 100);
                else if (preRoll && _sessionVolume != null)
                    SetPlayerVolumeInsistent(_standbyPlayer, GetSessionOpenVolume());
                else
                    SetPlayerVolumeGuarded(_standbyPlayer, 0);
                _standbyPlayer.Mute = _player.Mute;

                // Under the lock — see PlayInternal: a captured equalizer reference
                // used outside the lock races the snapshot path's Dispose.
                lock (_equalizerLock)
                {
                    if (_advancedEqEnabled && _equalizer != null)
                        _standbyPlayer.SetEqualizer(_equalizer);
                }

                if (_gaplessEngine)
                {
                    // Splice-engine staging: start decoding the next track NOW.
                    // Its PCM lands in its own segment queued behind the active
                    // one; the decoder free-runs ~2s ahead then back-pressures on
                    // the ring, and the sink crosses the boundary sample-adjacent
                    // when the active segment drains. No :start-paused (a paused
                    // input decodes nothing — VLC 3 source-verified), no volume
                    // dance (the OS session owns the sink's stream).
                    _engineStagedPath = normalizedPath;
                    EngineBeginSegment(_standbyPlayer, Math.Max(0, startPositionMs));
                    _standbyPlayer.Play(media);
                    if (startPositionMs > 0)
                    {
                        try { _standbyPlayer.Time = startPositionMs; } catch { /* input not up yet */ }
                    }
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.Staged", $"path={Path.GetFileName(normalizedPath)}");
                }
                else if (preRoll)
                {
                    // Pre-roll: open the standby input now, paused on its first
                    // frame (the same :start-paused primitive the paused-restart
                    // path relies on to never become audible). This warms the
                    // demuxer ~8s ahead so the gapless handoff resumes instead of
                    // parsing; the residual seam is the aout cold-open (see the
                    // splice engine above for the real fix).
                    media.AddOption(":start-paused");
                    _standbyPlayer.Play(media);
                    if (startPositionMs > 0)
                    {
                        // Best-effort while the input is still opening; the handoff
                        // re-applies the offset as a silent paused seek.
                        try { _standbyPlayer.Time = startPositionMs; } catch { /* input not up yet */ }
                    }
                }

                DebugLogger.Info(
                    DebugLogger.Category.Playback,
                    "AutoMix.DualPrepared",
                    $"path={Path.GetFileName(normalizedPath)}, startMs={startPositionMs}, preRoll={preRoll}, elapsedMs={Environment.TickCount64 - prepareStart}");
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

    /// <summary>
    /// True when the path is a remote http(s) stream (media-server track) rather
    /// than a local file. Remote streams open with FromType.FromLocation and parse
    /// over the network; they never use the standby/crossfade machinery (which is
    /// built around normalized local paths and pre-parsed local media). Stream URLs
    /// can embed auth tokens, so they must never be logged or shown verbatim.
    /// </summary>
    internal static bool IsRemoteStreamPath(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Play(string filePath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath)) return;

        if (!IsRemoteStreamPath(filePath) && !File.Exists(filePath))
        {
            PlaybackError?.Invoke(this, $"File not found: {filePath}");
            return;
        }

        DebugLogger.Info(DebugLogger.Category.Playback, "VLC.Play",
            $"path={(IsRemoteStreamPath(filePath) ? "<remote stream>" : Path.GetFileName(filePath))}");
        _keepAlive?.NotifyActivity();
        _currentMediaPath = filePath;

        // Capture on the calling thread so a competing Play() queued right after
        // cannot consume a restart-paused request meant for this call.
        var startPaused = _restartPausedRequest;
        _restartPausedRequest = false;

        // All heavy work on ThreadPool, serialized by the lock.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; } // Dispose() ran after the _disposed check above

            try
            {
                PlayInternal(filePath, startPaused);
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
    private void PlayInternal(string filePath, bool startPaused = false)
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
            lock (_seekGate) { _latestSeekMs = -1; }
            Interlocked.Exchange(ref _lastKnownLengthMs, 0);

            // Any transition belonging to the previous track is superseded by this one.
            // Clearing here bounds a latched guard to a single track even if a fade
            // worker faults or is cancelled before reaching its own clear.
            _transitionInFlight = false;

            // Re-apply ReplayGain for the new track (tag read does file IO, so it
            // runs here on the worker, before the target volume is computed). If
            // the mode is "Off" this is a no-op and _replayGainScalar stays 1.0.
            _currentMediaPath = filePath;
            if (!string.Equals(_rgMode, "Off", StringComparison.OrdinalIgnoreCase))
                ApplyReplayGain(_rgMode, _rgPreampDb);

            var hadPreviousMedia = _currentMedia != null;
            var targetVolume = GetTargetVlcVolume();
            // Remote streams take the plain open path: the standby/crossfade helpers
            // compare Path.GetFullPath-normalized local paths and the standby player
            // is never prepared for URLs (PrepareNext rejects them).
            var isRemote = IsRemoteStreamPath(filePath);
            // Crossfade needs two simultaneous streams; the WASAPI callback sinks
            // are single-stream, so disable the transition fade on those paths.
            var canTransitionFade = _crossfadeEnabled && hadPreviousMedia && !_player.Mute &&
                                    _wasapiOut == null && !_exclusiveModeEnabled && !_gaplessEngine &&
                                    !startPaused && !isRemote;
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
                // The in-flight flag is armed here (the only place a transition actually
                // begins) and cleared on every exit path below.
                _transitionInFlight = true;
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
                // The transition didn't start — fall through to the normal open path,
                // which must not run with the volume guard armed.
                _transitionInFlight = false;
            }

            // True-gapless splice engine: the next track was staged into the
            // sink by PrepareNext — its PCM is already queued sample-adjacent
            // behind the active segment, so the audible boundary needs NOTHING
            // from us. This "track change" is pure bookkeeping: swap the player
            // roles and let the outgoing segment play its tail out of the ring.
            if (_gaplessEngine && !startPaused && !isRemote && hadPreviousMedia &&
                _engineStagedPath != null && _standbyMedia != null &&
                string.Equals(_engineStagedPath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
            {
                // Bookkeeping-only is valid ONLY when the outgoing input already hit
                // EOF (decode-ahead). Taken mid-track (transition-mode advance at
                // fadeStart, manual Next inside the staging window) the deferred
                // Stop() below fires a flush on a LIVE segment: the ring is cleared
                // but the segment never finishes, so the provider waits on it
                // forever — permanent silence with a frozen timeline. Cut a live
                // outgoing segment over to the staged one instead (Abandon also
                // unblocks its writer before that Stop joins the decoder).
                var outgoingSeg = Volatile.Read(ref _engineSegments[EngineSlotOf(_player)]);
                if (outgoingSeg != null && !outgoingSeg.EndOfStream)
                    outgoingSeg.Abandon();
                var outgoingPlayer = _player;
                var outgoingMedia = _currentMedia;
                _player = _standbyPlayer;
                _currentMedia = _standbyMedia;
                _standbyPlayer = outgoingPlayer;
                _standbyMedia = null;
                _standbyPath = null;
                _engineStagedPath = null;
                _standbyStartPositionMs = -1;
                Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
                _standbyPrepared = false;
                ResetEndReachedPending();
                _transitionInFlight = false;
                _isPaused = false;
                // A pause parks the sink (the ring holds seconds); a play through
                // the splice path must un-park it or the new track renders into a
                // paused stream — silence with a moving timeline.
                _gaplessSink?.Resume();
                Interlocked.Exchange(ref _pendingSeekMs, -1);
                _positionTimer.Start();
                DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.Spliced", $"path={Path.GetFileName(filePath)}");
                // Outgoing input already hit EOF (decode-ahead) — the deferred
                // Stop() cannot block on a writer, and its segment keeps playing
                // from the ring untouched.
                QueueInactivePlayerCleanup(_standbyPlayer, outgoingMedia, sessionId);
                return;
            }
            if (_gaplessEngine)
            {
                // Manual skip / fresh play on the engine: drop everything queued
                // (also unblocks writers before the Stop below joins decoders).
                EngineClearAll();
            }

            // Gapless: no crossfade, but the next track was prepared on the
            // standby player — hand off to it instantly at full volume instead
            // of the audible stop/parse/start path.
            if (!canTransitionFade && !startPaused && !isRemote && _gaplessEnabled && hadPreviousMedia && !_standbyPrepared)
            {
                // No standby to hand off to — the audible cold open below is the
                // seam. Logged so a "gapless still gaps" report can be tied to a
                // dead prepare (see AutoMix.DualPrepareCancelled) from the log.
                DebugLogger.Info(DebugLogger.Category.Playback, "Gapless.ColdFallback", "no prepared standby");
            }
            if (!canTransitionFade && !startPaused && !isRemote && _gaplessEnabled && _standbyPrepared && hadPreviousMedia &&
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
            // Remote media-server streams are locations, not paths, and their
            // headers must be parsed over the network.
            var media = new Media(_libVlc, filePath, isRemote ? FromType.FromLocation : FromType.FromPath);

            // Parse the file header synchronously. This reads container
            // metadata (codec, sample rate, duration, channel layout).
            // Without this, M4A/ALAC/AAC can fail to decode.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(8000);
            var parseTask = media.Parse(isRemote ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal, timeout: 8000);
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
                // Parsing failed or timed out — file may be corrupted. Stream URLs
                // can embed auth tokens, so never echo them into the error surface.
                media.Dispose();
                PlaybackError?.Invoke(this, isRemote
                    ? "Could not open the remote stream. Check the server connection."
                    : $"Could not parse: {filePath}");
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

            // Restart requested while paused (drag-to-start / Previous): open the
            // new input paused on its first frame so the restart never becomes
            // audible playback.
            if (startPaused)
                _currentMedia.AddOption(":start-paused");

            // Start position (restore-resume / per-track start time / ended-seek
            // restart): open the demuxer AT the position via :start-time instead
            // of seeking after Play(). Play-from-0 + in-place SetTime fired a
            // post-open seek on every restored play, and on VLC 3's WASAPI aout an
            // in-place seek can wedge the output clock permanently ~600ms late
            // ("playback too late" → up-sampling → "buffer too late: dropped",
            // once per second until the aout is rebuilt) — the hi-res 96k→48k
            // field report. Opening at the position never flushes, so that trigger
            // disappears (and the engine's ring never holds pre-seek frames).
            var pendingMs = Interlocked.Exchange(ref _pendingSeekMs, -1);
            if (pendingMs > 0)
                _currentMedia.AddOption(FormattableString.Invariant($":start-time={pendingMs / 1000.0:0.###}"));

            // Exclusive mode: open/reuse the device sink at this track's source
            // rate and pin the output format before the aout starts.
            if (_exclusiveModeEnabled && _wasapiOut == null && OperatingSystem.IsWindows())
                PrepareExclusiveOutputFor(_currentMedia);

            // 5. Start playback
            if (_gaplessEngine)
            {
                // Seed the segment base with the start position (restore-resume,
                // per-track start time, ended-restart): the engine's position is
                // sink-derived (base + consumed), and only Seek() used to set
                // _enginePendingBaseMs. Started mid-track with base 0, audio
                // played from the saved position while the timeline counted up
                // from 0:00 (and a later pause persisted that bogus position).
                // EngineBeginSegment stores the base in both the segment and
                // _enginePendingBaseMs, so an input-open flush re-bases to the
                // same value the :start-time open actually begins at.
                EngineBeginSegment(_player, Math.Max(0, pendingMs));
                // Pause parks the sink; a fresh play must un-park it (pause → pick
                // a new track otherwise renders into a paused stream: silence with
                // a moving timeline, track after track). Paused restarts stay
                // parked — Resume() un-parks when the user actually resumes.
                if (!startPaused)
                    _gaplessSink?.Resume();
            }
            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            _player.Play(_currentMedia);
            if (startPaused)
                _isPaused = true; // input opens paused (:start-paused) — keep reported state in sync

            // Re-apply volume curve and equalizer after starting new media
            if (fadeInMs > 0)
            {
                // Single-player approximation of crossfade: fade out old track, then fade in new one.
                // NOT to targetVolume: on the OS-session path _player.Volume IS the shared
                // mmdevice session and targetVolume is 100 there, so the incoming track would
                // swell to FULL session volume and only snap down to the slider level when the
                // reassert below lands. Fade to the session-open equivalent of the user's level
                // instead (on non-session paths GetSessionOpenVolume == targetVolume already).
                SetPlayerVolumeGuarded(_player, 0);
                FadePlayerVolumeBlocking(0, GetSessionOpenVolume(), fadeInMs, cancel);
            }
            else if (ActiveCallbackSink != null || _gaplessEngine)
            {
                // Callback sink (Exclusive Mode / WASAPI gain / splice engine): the sink owns gain.
                // _player.Volume must stay pinned at 100 so libVLC's software mixer does
                // not scale the PCM before it reaches amem — GainSampleProvider then
                // applies the user level once. This branch has to come *before* the
                // _sessionVolume check: on Windows _sessionVolume is non-null by default,
                // so the session branch below used to win and wrote the user's level here
                // too, attenuating twice (~-5 dB extra at slider 30) and making the
                // "bit-perfect" claim false at any level under 100.
                SetPlayerVolumeGuarded(_player, 100);
            }
            else if (_sessionVolume != null)
            {
                // Windows mmdevice: _player.Volume IS the OS session, so setting it to
                // 100 (targetVolume) opens the new track's session at full volume — the
                // "volume blips to full for ~1s on track change" bug, because the float
                // reassert below only catches up once the new session appears. Open it
                // at the user's current level instead (the same trick the crossfade uses
                // to start a player without an open-blip); the reassert then refines to
                // the exact float.
                var milli = Volatile.Read(ref _rampCurrentMilli);
                if (milli < 0)
                    milli = CurvedVolumeToLevelMilli(
                        ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
                SetPlayerVolumeGuarded(_player, MilliToPlayerVolume(milli));
            }
            else
            {
                // Native output (macOS/Linux): the audio device opens cold and drops the
                // first buffers — the clipped track start. Fade in from silence across
                // the warmup so the onset isn't an abrupt cut. Bails if the track changes.
                SetPlayerVolumeGuarded(_player, 0);
                FadePlayerVolumeFadeIn(targetVolume, TrackStartFadeMs, _currentMedia);
            }
            _transitionInFlight = false;

            // SetEqualizer stays under the lock: the snapshot path can Dispose the
            // shared equalizer concurrently, so a reference captured then used
            // outside the lock could be freed mid-call (native use-after-free).
            lock (_equalizerLock)
            {
                if (_advancedEqEnabled && _equalizer != null)
                    _player.SetEqualizer(_equalizer);
            }

            // 6. Start position timer and fire initial duration update after brief delay
            // (paused restarts leave it stopped, exactly like Pause() does)
            if (!startPaused)
                _positionTimer.Start();

            // The new output session opens at 100% — push the user level onto it
            // as soon as it appears so there's no full-volume blip on track start.
            ScheduleSessionVolumeReassert(sessionId);
            ScheduleMuteIntentReassert(sessionId);

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
                _volumeTrailingCts?.Dispose();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
            }
            // Park the volume ramp so it can't fight the crossfade's direct fades.
            Volatile.Write(ref _rampTargetMilli, Volatile.Read(ref _rampCurrentMilli));

            // Gapless handoff starts the incoming track at the user's current level
            // right away; the crossfade starts it silent and fades it in. Opening at
            // the level (not full) is what stops the OS-session path from blipping to
            // 100% on every track change — see GetSessionOpenVolume. Insistent: a
            // non-pre-rolled standby is parked at 0 from PrepareNext, so this write
            // getting dropped by the guard means the whole incoming track opens
            // silent (a pre-rolled one is parked at the session-open level, where a
            // dropped write is merely stale by a few seconds of slider motion).
            SetPlayerVolumeInsistent(_standbyPlayer, instantHandoff ? GetSessionOpenVolume() : 0);
            _standbyPlayer.Mute = _player.Mute;

            var preparedAgeMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _standbyPreparedTicksUtc)) / TimeSpan.TicksPerMillisecond;

            DebugLogger.Info(
                DebugLogger.Category.Playback,
                instantHandoff ? "Gapless.DualStarted" : "AutoMix.DualStarted",
                $"path={Path.GetFileName(filePath)}, durationMs={_crossfadeDurationMs}, curve={_crossfadeFadeCurve}, preparedAgeMs={preparedAgeMs}");

            var startMs = Math.Max(_standbyStartPositionMs, Interlocked.Read(ref _pendingSeekMs));
            var standbyPreRolled = IsStandbyPreRolled();

            // Pre-rolled standby is parked on its first frame: apply any start
            // offset as a silent paused seek before it resumes (both the instant
            // handoff and the crossfade variant resume it below).
            if (standbyPreRolled && startMs > 0)
            {
                try { _standbyPlayer.Time = startMs; } catch { /* input still opening */ }
            }

            if (instantHandoff && standbyPreRolled)
            {
                // Let the outgoing input play its real tail out before resuming.
                // Resuming a warm pipeline is what keeps the seam small; the old
                // cold Play() here took longer than the handoff lead and left
                // 100-400ms of recorded silence between tracks. Resuming without
                // the wait is just as wrong: the incoming would overlay the
                // outgoing's final ~0.5s (one shared session — both audible).
                WaitForOutgoingTrackEnd(cancel);
                if (cancel.IsCancellationRequested || _disposed)
                {
                    ReleasePreparedNext();
                    return false;
                }
            }

            Interlocked.Exchange(ref _lastPlayStartTicksUtc, DateTime.UtcNow.Ticks);
            StartOrResumeStandby(standbyPreRolled);

            if (!standbyPreRolled && startMs > 0)
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
                // Per-player path ONLY: on Windows mmdevice both players share
                // ONE volume control (the process audio session), so zeroing the
                // outgoing here also zeroes the just-started incoming — and when
                // the incoming's aout hasn't opened yet, its finalVolume write
                // below is cached inside VLC instead of hitting the session,
                // which then sits at 0 until the reassert lands: the intermittent
                // clipped start of the next track. The outgoing is at its end and
                // deferred cleanup Stop()s it, so skipping the zero is inaudible.
                if (_sessionVolume == null)
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

            // Open level (not full) so the swapped-in player doesn't re-blip the
            // OS session to 100% before the reassert lands — see GetSessionOpenVolume.
            var finalVolume = GetSessionOpenVolume();
            if (cancel.IsCancellationRequested || _disposed)
            {
                SetPlayerVolumeInsistent(_player, finalVolume);
                // Cancel may have no follow-up Play(): disarm the transition guard so
                // the volume setters aren't swallowed until the next track change.
                _transitionInFlight = false;
                ReleasePreparedNext();
                return true;
            }

            var outgoingPlayer = _player;
            var outgoingMedia = _currentMedia;
            _player = _standbyPlayer;
            SetPlayerVolumeInsistent(_player, finalVolume);
            _currentMedia = _standbyMedia;
            _standbyPlayer = outgoingPlayer;
            _standbyMedia = null;
            _standbyPath = null;
            _standbyStartPositionMs = -1;
            Interlocked.Exchange(ref _standbyPreparedTicksUtc, 0);
            _standbyPrepared = false;
            // The outgoing track can hit its real end during warmup/fade (gapless hands
            // off ~0.3 s before the end) and arm the grace deadline with the incoming
            // session's id — clear it post-swap like the sequential/overlap paths do,
            // or TrackEnded fires 1.2 s in and double-advances the queue.
            ResetEndReachedPending();

            // Crossfade just wrote finalVolume directly to _player.Volume — sync
            // the throttle deadband baseline so the next slider write isn't
            // erroneously suppressed (or accepted) by a stale _lastWrittenVolume.
            lock (_volumeWriteLock)
            {
                _volumeTrailingCts?.Cancel();
                _volumeTrailingCts?.Dispose();
                _volumeTrailingCts = null;
                _pendingVolumeTarget = -1;
                _lastWrittenVolume = finalVolume;
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            _transitionInFlight = false;
            _isPaused = false;
            _positionTimer.Start();

            // Restore the user level on the session after the crossfade (the fade
            // drove the session volume up to full as the incoming track came in).
            ScheduleSessionVolumeReassert(sessionId);
            ScheduleMuteIntentReassert(sessionId);

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
                _volumeTrailingCts?.Dispose();
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
                // The cancel may come from pause/seek/settings with NO follow-up Play():
                // resync the ramp baseline to the restore (the cancelled fade left it at 0,
                // which the seek duck would later "restore" as silence) and disarm the
                // transition guard so the volume setters aren't swallowed until next track.
                Volatile.Write(ref _rampCurrentMilli, userMilli);
                _transitionInFlight = false;
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

            // Carry the EQ onto the now-active player. When engaged, the standby was
            // already prepped with this equalizer before Play, so this only syncs
            // values (same filter string → no output restart). When flat/bypassed,
            // Unset is a no-op on the now-filterless player (AutoMix cleanup no
            // longer plants a flat equalizer) — kept as a safety net so a stale
            // curve can never survive a swap.
            // Under the lock — see PlayInternal: a captured equalizer reference
            // used outside the lock races the snapshot path's Dispose.
            lock (_equalizerLock)
            {
                if (_advancedEqEnabled && _equalizer != null)
                    _player.SetEqualizer(_equalizer);
                else
                    _player.UnsetEqualizer();
            }

            _positionTimer.Start();
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.SeqSwap", $"session={sessionId}, warmupMs={warmupMs}");

            // 4. Fade the incoming in via the OS session (from the audible floor for the
            //    no-silence handoff, from zero for plain Crossfade).
            FadeSessionLevelBlocking(fadeInFromMilli, userMilli, fadeInMs, cancel);

            _transitionInFlight = false;
            lock (_volumeWriteLock)
            {
                _lastWrittenVolume = ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            // Re-assert in case the session resolved late, then tear down the outgoing.
            ScheduleSessionVolumeReassert(sessionId);
            ScheduleMuteIntentReassert(sessionId);
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
                _volumeTrailingCts?.Dispose();
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
                    // Cancel may have no follow-up Play(): resync the ramp baseline
                    // (parked at blendMilli above) and disarm the transition guard.
                    Volatile.Write(ref _rampCurrentMilli, userMilli);
                    _transitionInFlight = false;
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

            // Engaged: value-only sync (standby was prepped with this equalizer).
            // Flat: no-op safety Unset on a filterless player; see the SeqSwap note.
            // Under the lock — a captured reference used outside it races the
            // snapshot path's Dispose (native use-after-free).
            lock (_equalizerLock)
            {
                if (_advancedEqEnabled && _equalizer != null)
                    _player.SetEqualizer(_equalizer);
                else
                    _player.UnsetEqualizer();
            }

            _positionTimer.Start();
            DebugLogger.Info(DebugLogger.Category.Playback, "Crossfade.OverlapSwap", $"session={sessionId}");

            // 4. Rise the incoming back to the full user level.
            FadeSessionLevelBlocking(blendMilli, userMilli, riseMs, cancel);

            _transitionInFlight = false;
            lock (_volumeWriteLock)
            {
                _lastWrittenVolume = ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100)));
                _lastVolumeWriteTicks = Stopwatch.GetTimestamp();
            }

            ScheduleSessionVolumeReassert(sessionId);
            ScheduleMuteIntentReassert(sessionId);
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

                // A pre-rolled input opens paused on its first frame (:start-paused).
                // A resume issued while it was still Opening can be dropped by the
                // input thread, so re-nudge here each poll until it actually plays.
                if (standby.State == VLCState.Paused)
                    standby.SetPause(false);
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

    // A pre-rolled standby has a live input: Opening/Buffering while it spins up,
    // Paused once :start-paused parks it on the first frame. (Playing would mean
    // the park failed and it is already audible — resume semantics are still the
    // right ones there.) A merely parsed standby is NothingSpecial/Stopped.
    private bool IsStandbyPreRolled()
    {
        try
        {
            return _standbyPlayer.State
                is VLCState.Opening or VLCState.Buffering or VLCState.Paused or VLCState.Playing;
        }
        catch
        {
            return false;
        }
    }

    // Start the prepared standby, or resume it when it was pre-rolled. A pre-rolled
    // input must NOT go through Play(media): set_media tears the warm pipeline down
    // and re-opens it cold — the exact seam the pre-roll exists to avoid. SetPause
    // on a still-opening input can be dropped; WaitForStandbyPlaybackReady re-nudges
    // a Paused standby every poll until it actually plays.
    private void StartOrResumeStandby(bool preRolled)
    {
        if (preRolled)
        {
            try { _standbyPlayer.SetPause(false); } catch { /* transitional state */ }
            return;
        }
        if (_standbyMedia != null) // callers validate; guard for the compiler + races
            _standbyPlayer.Play(_standbyMedia);
    }

    // Gapless end-alignment: hold the handoff until the outgoing input has actually
    // ended (or is within GaplessUnpauseLeadMs of it). The outgoing's audio-output
    // tail drains for roughly as long as the resumed standby takes to reach the
    // device, so unpausing at input end lands the two streams close to back-to-back.
    // Exits early on skip/stop (cancel), on any non-Playing state (Ended, or a user
    // pause landing in the final half-second), and on the safety timeout so a
    // stalled input can't wedge the handoff. Runs under _playbackLock like the
    // blocking crossfade fades that precede it on the other paths.
    private void WaitForOutgoingTrackEnd(CancellationToken cancel)
    {
        var start = Environment.TickCount64;
        var deadline = start + GaplessEndAlignTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_disposed || cancel.IsCancellationRequested)
                return;

            try
            {
                if (_player.State != VLCState.Playing)
                    break;

                var len = _player.Length;
                if (len <= 0)
                    len = Interlocked.Read(ref _lastKnownLengthMs);
                var time = _player.Time;
                if (len > 0 && time >= 0 && len - time <= GaplessUnpauseLeadMs)
                    break;
            }
            catch
            {
                break; // player in a transitional state — hand off now
            }

            Thread.Sleep(GaplessEndAlignPollMs);
        }

        DebugLogger.Info(
            DebugLogger.Category.Playback,
            "Gapless.EndAligned",
            $"waitedMs={Environment.TickCount64 - start}");
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
                // Engine: a still-live segment on this player must be finished before
                // Stop() — the stop's flush clears the ring without ending the
                // segment and the provider would wait on it forever. EOS'd segments
                // are immune (EngineFlush ignores the teardown flush; the tail plays
                // out of the ring untouched).
                if (_gaplessEngine)
                {
                    try
                    {
                        var liveSeg = Volatile.Read(ref _engineSegments[EngineSlotOf(inactivePlayer)]);
                        if (liveSeg != null && !liveSeg.EndOfStream)
                            liveSeg.Abandon();
                    }
                    catch { /* engine not wired for this player */ }
                }
                try { inactivePlayer.Stop(); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupStep", $"Stop: {ex.GetType().Name}: {ex.Message}"); }
                // Clear any leftover curve with UnsetEqualizer (safe on a stopped
                // player — the old SetEqualizer(null) NRE was LibVLCSharp null
                // marshaling, not player state). Do NOT plant a flat Equalizer
                // here: it left "equalizer" in this player's filter chain, and
                // stripping it at the next swap (UnsetEqualizer on the by-then
                // LIVE incoming player) restarted its output — the split-second
                // cut at the start of the next track. A stopped player has no
                // output, so unsetting here is inaudible and the reused player
                // starts filterless.
                try { inactivePlayer.UnsetEqualizer(); }
                catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.CleanupStep", $"UnsetEqualizer: {ex.GetType().Name}"); }
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

    // The LibVLC player volume to open a NEW output session at WITHOUT an audible
    // blip. On the Windows OS-session path MediaPlayer.Volume IS the mmdevice
    // session, so opening at GetTargetVlcVolume() (100) starts the incoming track's
    // session at FULL volume; the async reassert then has to drag it down to the
    // user level — heard as the next track blipping loud then dropping (and racy,
    // because during a handoff two sessions are active and the reassert can resolve
    // the wrong one first). Map the user's current level back through the mmdevice
    // cubic instead, exactly as the single-player PlayInternal path does; the
    // reassert then only refines to the precise float. Non-session paths keep the
    // curved user volume (there GetTargetVlcVolume already returns it).
    private int GetSessionOpenVolume()
    {
        if (_sessionVolume == null)
            return GetTargetVlcVolume();
        var milli = Volatile.Read(ref _rampCurrentMilli);
        if (milli < 0)
            milli = CurvedVolumeToLevelMilli(
                ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
        return MilliToPlayerVolume(milli);
    }

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
    // LibVLC delivers decoded S16N PCM here (EQ already applied upstream); we
    // forward it to the sink, which applies the user's volume per-sample. These
    // run on LibVLC's audio thread — they must never throw.
    // count is FRAMES of 16-bit samples (VLC 3.x amem outputs S16N only) —
    // 2 bytes per sample. Reading ×4 here over-read past VLC's native block
    // and killed the process with a fatal AV once the read crossed an
    // unmapped page (the 0x80131506 exclusive-mode crash).
    private long _audioPlayCallCount;
    private void AudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        // Runs on libvlc's aout thread. Anything that escapes here kills the process,
        // so the size arithmetic and the Rent are inside the try as well — they were
        // outside it, and both can throw (checked overflow, OutOfMemory).
        byte[]? buf = null;
        var bytes = 0;
        try
        {
            var sink = ActiveCallbackSink;
            if (sink == null) return;

            // Size from the channel count SetAudioFormat pinned, NOT from the sink's:
            // the sink is installed before the format is pinned, so after a
            // 1ch → 2ch swap whose SetAudioFormat failed, VLC is still delivering
            // mono blocks while sink.Channels reads 2 — a 2× over-read off the end of
            // libvlc's buffer, which is exactly the 0x80131506 exclusive-mode crash.
            var channels = _callbackChannels;
            if (channels <= 0) return;

            bytes = checked((int)count * channels * 2);
            if (Interlocked.Increment(ref _audioPlayCallCount) <= 3)
                WasapiGainOutput.Diag($"AudioPlay #{_audioPlayCallCount}: count(frames)={count} -> {bytes}B, pts={pts}");

            buf = System.Buffers.ArrayPool<byte>.Shared.Rent(bytes);
            System.Runtime.InteropServices.Marshal.Copy(samples, buf, 0, bytes);
            sink.Write(buf, bytes);
        }
        catch (Exception ex)
        {
            if (_audioPlayCallCount <= 5) WasapiGainOutput.Diag($"AudioPlay threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (buf != null) System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void AudioPause(IntPtr data, long pts) => ActiveCallbackSink?.Pause();
    private void AudioResume(IntPtr data, long pts) => ActiveCallbackSink?.Resume();
    private void AudioFlush(IntPtr data, long pts) => ActiveCallbackSink?.Flush();
    private void AudioDrain(IntPtr data) => ActiveCallbackSink?.Drain();

    // ── Splice-engine callbacks (per-player closures; libvlc threads — never throw) ──

    private void EnginePlay(int slot, IntPtr samples, uint count, long pts)
    {
        try
        {
            var seg = Volatile.Read(ref _engineSegments[slot]);
            var sink = _gaplessSink;
            if (seg == null || sink == null || count == 0)
                return;
            // count is FRAMES; S16N interleaved → samples = frames × channels.
            var sampleCount = checked((int)count * sink.Channels);
            var buf = System.Buffers.ArrayPool<short>.Shared.Rent(sampleCount);
            try
            {
                Marshal.Copy(samples, buf, 0, sampleCount);

                // Diagnostics: a pts jump between consecutive blocks = VLC
                // dropped/skipped audio upstream; the legacy aout re-times over
                // such holes, the splice ring butt-joins them. Head blocks log
                // their peak so decoder warm-up garble is visible in the field.
                var expectedPts = _engineExpectedPts[slot];
                if (expectedPts > 0 && Math.Abs(pts - expectedPts) > 20_000)
                    DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.PtsGap",
                        $"slot={slot}, gapMs={(pts - expectedPts) / 1000.0:0.#}, frames={count}");
                if (expectedPts == 0)
                {
                    var peak = 0;
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var v = Math.Abs((int)buf[i]);
                        if (v > peak) peak = v;
                    }
                    DebugLogger.Info(DebugLogger.Category.Playback, "GaplessEngine.SegHead",
                        $"slot={slot}, pts={pts}, frames={count}, peak={peak}");
                }
                _engineExpectedPts[slot] = pts + (long)count * 1_000_000 / sink.SampleRate;

                if (_engineInTap is { } inTap)
                {
                    try
                    {
                        lock (_engineInTapLock)
                        {
                            inTap.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                                buf.AsSpan(0, sampleCount)));
                            _engineInTapSinceFlush += sampleCount;
                            if (_engineInTapSinceFlush >= sink.SampleRate)
                            {
                                inTap.Flush();
                                _engineInTapSinceFlush = 0;
                            }
                        }
                    }
                    catch { /* diagnostic only */ }
                }

                // Blocks when the ring is full (back-pressures this player's
                // decoder); returns false when abandoned — just drop the block.
                seg.Write(buf.AsSpan(0, sampleCount));
            }
            finally
            {
                System.Buffers.ArrayPool<short>.Shared.Return(buf);
            }
        }
        catch { /* libvlc decoder thread */ }
    }

    private void EngineFlush(int slot)
    {
        try
        {
            _engineExpectedPts[slot] = 0; // pts continuity ends at any flush
            var seg = Volatile.Read(ref _engineSegments[slot]);
            // VLC also flushes at input teardown AFTER drain; clearing then
            // would eat the un-played tail mid-splice. A drained segment is
            // final — ignore the flush.
            if (seg != null && !seg.EndOfStream)
                seg.Flush(Interlocked.Read(ref _enginePendingBaseMs[slot]));
        }
        catch { /* libvlc thread */ }
    }

    private void EngineDrain(int slot)
    {
        try { Volatile.Read(ref _engineSegments[slot])?.MarkEndOfStream(); }
        catch { /* libvlc thread */ }
    }

    private int EngineSlotOf(MediaPlayer player) =>
        ReferenceEquals(player, _enginePlayers[0]) ? 0 : 1;

    // The engine's audible tail segment for the CURRENT player while it is
    // still rendering. Non-null exactly when the input has outrun the speaker
    // (input EOF leads the audible end by the ring depth), which is when
    // end-of-track bookkeeping must follow the SINK, not VLC's clock.
    private GaplessTrackSegment? EngineActiveTailSegment()
    {
        if (!_gaplessEngine)
            return null;
        var seg = _gaplessSink?.Provider.ActiveSegment;
        return seg != null && seg.Source is int slot && slot == EngineSlotOf(_player) && !seg.IsFinished
            ? seg
            : null;
    }

    /// <summary>
    /// Open a fresh segment for this player's next input and queue it behind
    /// whatever the sink is rendering. Call BEFORE the player's Play().
    /// </summary>
    private void EngineBeginSegment(MediaPlayer player, long basePositionMs)
    {
        var sink = _gaplessSink;
        if (sink == null) return;
        var slot = EngineSlotOf(player);
        Interlocked.Exchange(ref _enginePendingBaseMs[slot], Math.Max(0, basePositionMs));
        _engineExpectedPts[slot] = 0; // fresh input: next block is a head block
        var seg = new GaplessTrackSegment(
            sink.SampleRate, sink.Channels, slot, capacitySeconds: 20, Math.Max(0, basePositionMs));
        Volatile.Write(ref _engineSegments[slot], seg);
        sink.Provider.Enqueue(seg);
    }

    /// <summary>
    /// Manual/new playback on the engine: everything queued is stale. Abandon
    /// it all (also unblocks any writer BEFORE the callers' player.Stop() joins
    /// the decoder thread — a blocked Write there deadlocks the stop).
    /// </summary>
    private void EngineClearAll()
    {
        _engineStagedPath = null;
        try { _gaplessSink?.Provider.Clear(); } catch { }
    }

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
    // Native-output counterpart to ScheduleSessionVolumeReassert: on PulseAudio /
    // PipeWire a new stream can open with a restored mute from the app's
    // stream-restore entry (poisoned by older builds' keep-alive stream, which
    // recorded itself muted under the shared app identity), and VLC only syncs
    // that state back a moment after the stream connects. Poll briefly and
    // overwrite any mute that contradicts the user's intent; the corrective
    // write also heals the OS-side restore entry for future streams.
    private void ScheduleMuteIntentReassert(long sessionId)
    {
        if (_sessionVolume != null || ActiveCallbackSink != null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var waited = 0; waited < 2000; waited += 50)
            {
                if (_disposed || sessionId != CurrentSessionId) return;
                try
                {
                    if (_player.Mute != _userMuted)
                        _player.Mute = _userMuted;
                }
                catch { /* player transitioning */ }
                Thread.Sleep(50);
            }
        });
    }

    private void ScheduleSessionVolumeReassert(long sessionId)
    {
        // Gated on the callback sink as well as the session handle. When exclusive mode
        // falls back to the shared NAudio sink, _sessionVolume is still non-null, so this
        // kept driving the process session on top of the sink's own gain — a third
        // attenuation stacked on the two in PlayInternal.
        if (_sessionVolume == null || ActiveCallbackSink != null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // New output session on track start/swap — drop the previous track's
            // cached session so we re-resolve to the new active one (no accumulation).
            _sessionVolume.Invalidate();
            // The session is created a few ms after Play(); poll quickly so the
            // user level lands almost immediately instead of a 100% blip. Only a
            // write that landed on the ACTIVE (rendering) session ends the loop:
            // during a handoff the just-stopped outgoing session can still be the
            // resolver's pick, and stopping at that success left the incoming
            // session at whatever level VLC opened it with — 0 when the un-park
            // write was lost, i.e. a whole track of "plays but no audio" until a
            // manual skip rebuilt the session. Re-resolve each round so the
            // incoming is picked up the moment it appears; the repeated writes
            // all carry the same level, so they're inaudible. Window is generous
            // because the session can surface late under save/artwork I/O load.
            for (var waited = 0; waited < 1500; waited += 20)
            {
                if (_disposed || sessionId != CurrentSessionId) return;
                if (ReapplySessionVolume() && _sessionVolume.HoldsActiveSession) return;
                _sessionVolume.Invalidate();
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

        // Queued to the ThreadPool + serialized by _playbackLock like every other
        // playback entry point (Play, Stop, Resume, PrepareNext). Inline, a pause
        // landing while a transition worker held the lock read the pre-swap _player
        // and was then overwritten by the swap's _isPaused = false — audio kept
        // playing while the UI showed paused. The CancelSkipCts above makes any
        // in-flight fade bail within one step, so the lock wait stays short.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (_disposed) return;
                if (_player.IsPlaying)
                {
                    ResetEndReachedPending();
                    _player.Pause();
                    // Engine: the ring holds seconds of decoded audio — pausing
                    // only VLC would keep the sink audibly playing it out.
                    if (_gaplessEngine)
                        _gaplessSink?.Pause();
                    _isPaused = true;
                    _positionTimer.Stop();
                }
            }
            catch (ObjectDisposedException) { /* disposed mid-pause */ }
            finally
            {
                try { _playbackLock.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    public void Resume()
    {
        if (_disposed || _currentMedia == null) return;
        _keepAlive?.NotifyActivity();

        // Queued to the ThreadPool like every other playback entry point (Play, Stop,
        // PrepareNext, SetExclusiveMode). This used to take _playbackLock inline, and
        // PlayerViewModel.PlayPause is a [RelayCommand] — i.e. it ran on the UI thread.
        // Lock holders include PrepareNext's non-cancellable parseTask.Wait(8000) and the
        // native _player.Stop(), so unpausing while the next track was being prepared
        // from a slow or network path froze the window for up to 8 seconds.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (_disposed || _currentMedia == null) return;
                if (_isPaused)
                {
                    ResetEndReachedPending();
                    if (_gaplessEngine)
                        _gaplessSink?.Resume();
                    // VLC's Pause() toggles between pause and play
                    _player.Pause();
                    _isPaused = false;
                    _positionTimer.Start();
                }
            }
            catch (ObjectDisposedException) { /* disposed mid-resume */ }
            finally
            {
                try { _playbackLock.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    public void Stop()
    {
        if (_disposed) return;

        CancelSkipCts();
        ResetEndReachedPending();
        // Written under _seekGate like every other mutation of this field; Stop() racing
        // a Seek() could otherwise let the seek target survive the clear and be applied
        // to the *next* track.
        lock (_seekGate) { _latestSeekMs = -1; }
        _positionTimer.Stop();
        _isPaused = false;
        _transitionInFlight = false;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _playbackLock.Wait(); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (_gaplessEngine)
                    EngineClearAll(); // unblock all writers before the Stops below
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
        // Engine: the seek's flush callback re-bases the active segment's
        // position from this value (the flush itself clears the stale ring).
        if (_gaplessEngine)
            Interlocked.Exchange(
                ref _enginePendingBaseMs[EngineSlotOf(_player)],
                (long)position.TotalMilliseconds);

        // After a track ends (or is stopped) the audio output is torn down and
        // _player.Length reports 0, so the in-place seek below early-returns and the
        // write is ignored — dragging the slider back from the end then plays nothing.
        // Restart the media and apply the dropped position as a pending seek so audio
        // resumes from there (same mechanism as the start-region restart). Length is
        // 0 once ended, so fall back to the last known length for the end guard.
        var state = _player.State;
        if ((state == VLCState.Ended || state == VLCState.Stopped) &&
            !string.IsNullOrEmpty(_currentMediaPath))
        {
            var endedLen = _player.Length;
            if (endedLen <= 0) endedLen = Interlocked.Read(ref _lastKnownLengthMs);
            long restartMs = -1;
            if (endedLen > 0)
            {
                var maxMs = endedLen - Math.Min(EndSeekGuardMs, endedLen / 20);
                var clamped = (long)Math.Clamp(position.TotalMilliseconds, 0, maxMs);
                if (clamped > 0) restartMs = clamped;
            }
            lock (_seekGate) { _latestSeekMs = -1; }
            _positionTimer.Stop();
            Interlocked.Exchange(ref _pendingSeekMs, restartMs);
            Play(_currentMediaPath);
            return;
        }

        var len = _player.Length;
        if (len <= 0) return;

        // Keep manual seeks a guard's-width short of the end (scaled down on very
        // short clips) so seeking to the far right never trips EndReached and
        // advances the track. Natural end-of-track playback is unaffected — this
        // only bounds explicit seeks.
        var maxSeekMs = len - Math.Min(EndSeekGuardMs, len / 20);
        var clampedMs = (long)Math.Clamp(position.TotalMilliseconds, 0, maxSeekMs);
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
            _restartPausedRequest = _isPaused;
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

    // VLC invokes these on its own native event thread. An exception unwinding out of a
    // managed handler back into native code terminates the process — it cannot be caught
    // upstream. Both handlers are therefore wrapped whole. (The concrete case: Dispose()
    // frees _positionTimer while an EndReached is in flight, and the _positionTimer.Start()
    // at the end of the handler — outside the old inner try — threw ObjectDisposedException.)
    private void OnEndReached(object? sender, EventArgs e)
    {
        try { OnEndReachedCore(sender); }
        catch (Exception ex)
        {
            try { DebugLogger.Warn(DebugLogger.Category.Playback, "VLC.EndReached.Threw", ex.Message); }
            catch { }
        }
    }

    private void OnEndReachedCore(object? sender)
    {
        if (_disposed) return;

        // Engine EOS fallback: VLC 3's amem drain callback "may" fire at end of
        // stream — EndReached is the guaranteed signal. Without EndOfStream the
        // sender's segment never finishes, and the sink would pad silence
        // forever instead of splicing to the queued next track. Idempotent, and
        // applies to BOTH roles (the staged player's early input-EOF included),
        // so it runs before the sender/_player filtering below.
        if (_gaplessEngine && sender is MediaPlayer endedPlayer)
        {
            try { Volatile.Read(ref _engineSegments[EngineSlotOf(endedPlayer)])?.MarkEndOfStream(); }
            catch { /* transitional */ }
        }
        if (!ReferenceEquals(sender, _player))
        {
            // Engine diagnosis: name the sender so a wedged end can be traced.
            if (_gaplessEngine && sender is MediaPlayer sp)
                DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredInactive",
                    $"senderSlot={EngineSlotOf(sp)}, playerSlot={EngineSlotOf(_player)}");
            else
                DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredInactive");
            return;
        }

        var sessionId = CurrentSessionId;
        if (_transitionInFlight)
        {
            // A crossfade/AutoMix transition is committing: this EndReached is the
            // OUTGOING track reaching its natural end mid-fade (sender is still the
            // pre-swap _player), but sessionId already names the INCOMING track —
            // PlayInternal bumps it before the transition worker runs. Arming the
            // grace deadline here would fire TrackEnded for the new session ~1.2 s
            // later and double-advance the queue (a track gets skipped). The
            // transition owns the advance; every cancel/fault path clears the flag.
            DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredTransition", $"session={sessionId}");
            return;
        }

        var elapsedSinceStartMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPlayStartTicksUtc)) / TimeSpan.TicksPerMillisecond;
        if (elapsedSinceStartMs is >= 0 and < 500)
        {
            // A track genuinely shorter than the stale window ends this fast for
            // real — swallowing its EndReached stalled the queue forever. Only treat
            // the event as stale when the current media is known to be longer.
            var knownLenMs = Interlocked.Read(ref _lastKnownLengthMs);
            if (knownLenMs <= 0)
            {
                try { knownLenMs = _player.Length; } catch { /* transitional state */ }
            }
            if (knownLenMs <= 0 || knownLenMs >= 500)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "VLC.EndReached.IgnoredStale", $"session={sessionId}");
                return;
            }
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
            // Engine: the audible tail is still rendering — report the honest
            // audible position instead of snapping to the end ~2s early. The
            // VM's natural-end fallback arms off these reports; a full-duration
            // report here would advance early and re-cut the tail.
            if (EngineActiveTailSegment() is { } endSeg)
                PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(endSeg.PositionMs));
            else if (len > 0)
                PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(len));
        }
        catch { /* Player may be in transitional state */ }

        // Engine: input EOF leads the AUDIBLE end by the ring depth, and EOS was
        // marked above so BufferedSamples only drains from here. Extend the grace
        // to the audible end (+ margin for sink latency / timer jitter) so
        // TrackEnded stops firing ~0.8s early — that early advance cut the tail
        // and started the next track over it, which with gapless OFF sounded
        // like gapless was still on (same root as the repeat-one / queue-end
        // tail cut). One-shot upper bound, self-capped by the ring capacity.
        var graceMs = (long)EndReachedGraceMs;
        if (EngineActiveTailSegment() is { } tailSeg)
        {
            var bufferedMs = (long)tailSeg.BufferedSamples * 1000
                             / (tailSeg.SampleRate * tailSeg.Channels);
            graceMs = Math.Max(graceMs, bufferedMs + 250);
        }
        var deadline = DateTime.UtcNow.AddMilliseconds(graceMs).Ticks;
        Interlocked.Exchange(ref _endReachedSessionId, sessionId);
        Interlocked.Exchange(ref _endReachedDeadlineTicksUtc, deadline);
        _positionTimer.Start();
    }

    private void OnError(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed) return;
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
        catch (Exception ex)
        {
            try { DebugLogger.Warn(DebugLogger.Category.Playback, "VLC.Error.Threw", ex.Message); }
            catch { }
        }
    }

    // 1 while a position-timer callback is inside the body. Timer.Stop() does NOT
    // wait for an in-flight Elapsed callback, so RebuildOutputModeLocked/Dispose
    // spin on this after stopping the timer — otherwise the old player can be
    // disposed while a late callback is still inside a native _player call
    // (use-after-free the CLR can't catch).
    private int _positionTickActive;
    private long _lastPositionTickUtcTicks;

    private void OnPositionTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_disposed) return;

        // The timer runs only while audio is playing — it doubles as the
        // keep-alive's "still in use" heartbeat (cheap: one volatile write).
        _keepAlive?.NotifyActivity();

        if (Interlocked.CompareExchange(ref _positionTickActive, 1, 0) != 0) return;

        // Rare-dropout diagnostic (2026-07-23 fingerprint: VLC "PCR too late" +
        // "buffer too late" with the 1000ms cushion exhausted): a tick-to-tick
        // gap far past the 100ms cadence means THIS process/system stalled too.
        // Next occurrence: gap line + VLC lines = system-wide freeze; VLC lines
        // alone = native input (disk) stall. Gaps right after a pause/seek are
        // expected — ignore those when reading the log. One line per event.
        var tickNowTicks = DateTime.UtcNow.Ticks;
        var tickPrevTicks = Interlocked.Exchange(ref _lastPositionTickUtcTicks, tickNowTicks);
        if (tickPrevTicks != 0 && !_isPaused && _currentMedia != null)
        {
            var gapMs = (tickNowTicks - tickPrevTicks) / TimeSpan.TicksPerMillisecond;
            // Was "> 750 and < 10_000", which straddled the evidence on both sides: a
            // dropout costing ~1s of clock can stall well under 750ms, and the upper
            // bound silently discarded the long freezes that most need reporting.
            // 250ms is still 2.5x the 100ms cadence, so ordinary scheduling jitter
            // stays quiet.
            if (gapMs > 250)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "PositionTimer.Stall", $"gapMs={gapMs}");
                // Also mirrored into the dev-mode session log: this marker next to
                // VLC's "buffer too late … dropped" pair = app/system-wide freeze;
                // the VLC pair alone = input/disk-side stall (see rare-dropout hunt).
                DebugLog.Write("Playback", $"position-timer stall: gapMs={gapMs}");
            }
        }

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
                // Engine: while the audible tail still renders, report the honest
                // sink position instead (see the deadline note in OnEndReachedCore);
                // the full-duration snap still happens once the tail drains.
                if (EngineActiveTailSegment() is { } graceSeg)
                {
                    PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(graceSeg.PositionMs));
                }
                else
                {
                    var len = _player.Length;
                    if (len <= 0)
                        len = Interlocked.Read(ref _lastKnownLengthMs);
                    if (len > 0)
                        PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(len));
                }

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
            //
            // Engine end-watchdog: an EndReached can be missed or mis-attributed
            // (observed in the field: IgnoredInactive with no staging active →
            // playback wedged at end-of-track with no end bookkeeping pending).
            // If VLC says the current input Ended and the audible tail has
            // drained, arm the end grace ourselves so the track can never wedge.
            if (_gaplessEngine && !_isPaused && _currentMedia != null &&
                Interlocked.Read(ref _endReachedDeadlineTicksUtc) == 0)
            {
                try
                {
                    if (_player.State == VLCState.Ended)
                    {
                        var wdSeg = _gaplessSink?.Provider.ActiveSegment;
                        if (wdSeg != null && wdSeg.Source is int wdSlot && wdSlot == EngineSlotOf(_player))
                            wdSeg.MarkEndOfStream(); // idempotent: input is done, no more samples
                        var drained = wdSeg == null || wdSeg.IsFinished || wdSeg.BufferedSamples == 0 ||
                                      (wdSeg.Source is int s && s != EngineSlotOf(_player));
                        if (drained)
                        {
                            DebugLogger.Warn(DebugLogger.Category.Playback, "GaplessEngine.EndWatchdog",
                                "input Ended with no end bookkeeping pending — arming end grace");
                            Interlocked.Exchange(ref _endReachedSessionId, sessionId);
                            Interlocked.Exchange(ref _endReachedDeadlineTicksUtc,
                                DateTime.UtcNow.AddMilliseconds(250).Ticks);
                        }
                    }
                }
                catch { /* transitional player state */ }
            }

            // Engine: _player.Time leads the AUDIBLE position by the ring depth
            // (amem reports no latency to VLC), so position comes from the sink's
            // active segment. While the previous track's tail is still rendering
            // (segment belongs to the swapped-out player), the incoming track
            // hasn't audibly started — report 0 for it.
            long time;
            if (_gaplessEngine && _gaplessSink?.Provider.ActiveSegment is { } audibleSeg)
                time = audibleSeg.Source is int audibleSlot && audibleSlot == EngineSlotOf(_player)
                    ? audibleSeg.PositionMs
                    : 0;
            else
                time = _player.Time;
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
        finally
        {
            Volatile.Write(ref _positionTickActive, 0);
        }
    }

    // Bounded spin-waits used before disposing a player: drain a late in-flight
    // position-timer callback / seek-worker iteration so neither is still inside
    // a native _player call when the handle is freed. Bounded so a wedged native
    // call can't hang rebuild/shutdown.
    private void DrainPositionTimerCallback(int maxMs = 500)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _positionTickActive) != 0 && sw.ElapsedMilliseconds < maxMs)
            Thread.Sleep(1);
    }

    private void DrainSeekWorker(int maxMs = 1000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _seekWorkerActive) != 0 && sw.ElapsedMilliseconds < maxMs)
            Thread.Sleep(1);
    }

    // RCU-style reclamation for a swapped-out MediaPlayer: readers hold the old
    // reference for microseconds, so a generous delay makes the free safe without
    // locking every _player read. Used by RebuildOutputModeLocked (not shutdown —
    // Dispose() reclaims immediately after draining, when no UI reader remains).
    private static void DisposePlayerDeferred(MediaPlayer player)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(
            _ => { try { player.Dispose(); } catch { } },
            TaskScheduler.Default);
    }

    private void EnsureSeekWorker()
    {
        if (Interlocked.CompareExchange(ref _seekWorkerActive, 1, 0) != 0)
            return;

        // Run on a dedicated, above-normal-priority thread rather than the shared
        // ThreadPool. The worker dips output volume to 0, writes _player.Time, then
        // restores — if it is descheduled mid-dip the audio stays silent. A library
        // scan saturates the pool with parallel metadata reads, so a pooled worker
        // gets starved between dip and restore (audible as the audio cutting out
        // while scrubbing during a scan). A dedicated prioritised thread keeps that
        // dip→restore window tight under load.
        var worker = new Thread(() =>
        {
            // Seek() stops the position timer before enqueuing; normally the worker
            // restarts it after applying the seek. If every dequeued seek was skipped
            // (transient Length==0 mid-transition), nothing restarted it and position/
            // lyrics updates stayed frozen — track that and recover on exit.
            var sawUnappliedSeek = false;
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
                    {
                        if (sawUnappliedSeek && !_disposed && !_isPaused && _currentMedia != null)
                            _positionTimer.Start();
                        break;
                    }

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
                    {
                        sawUnappliedSeek = true;
                        continue;
                    }

                    targetMs = Math.Clamp(targetMs, 0, len);
                    sawUnappliedSeek = false;

                    var nowTicks = DateTime.UtcNow.Ticks;
                    DebugLogger.Info(DebugLogger.Category.Playback, "Seek.Apply", $"targetMs={targetMs}, state={_player.State}, isPlaying={_player.IsPlaying}");

                    // An in-place Time write flushes the audio buffer and playback
                    // resumes mid-waveform — VLC drops a "buffer too late" on every
                    // seek, audible as a click. Silence the output across the seek to
                    // mask it. HOW we dip depends on the volume path, because writing
                    // the wrong control strands the volume:
                    //   • OS-session (Windows mmdevice): _player.Volume IS the shared
                    //     session, so restoring it to 100 would leave the session at
                    //     full volume. Dip the session LEVEL and restore it to the
                    //     user's current level instead (mmdevice ramps it click-free).
                    //   • Native integer volume (macOS/Linux): dip/restore _player.Volume.
                    //   • WASAPI callback sink (exclusive mode): the sink owns gain — seek.
                    // Paused/muted is already silent, so just seek.
                    if (_isPaused || _player.Mute)
                    {
                        _player.Time = targetMs;
                    }
                    else if (ActiveCallbackSink != null)
                    {
                        // Exclusive Mode / WASAPI gain path: the sink owns gain and
                        // _player.Volume is pinned at 100. Touching the OS session here
                        // (which the branch below would do, since _sessionVolume is
                        // non-null by default on Windows) would duck the whole process
                        // session on top of the sink's own level. Just seek.
                        _player.Time = targetMs;
                    }
                    else if (_sessionVolume is { } sv)
                    {
                        var savedMilli = Volatile.Read(ref _rampCurrentMilli);
                        if (savedMilli < 0)
                            savedMilli = CurvedVolumeToLevelMilli(
                                ApplyReplayGainScalar(ApplyVolumeCurve(Math.Clamp(_userVolume + _volumeAdjust, 0, 100))));
                        // Duck to 25% (~-12 dB) instead of muting. A full dip to 0
                        // measured as ~63-120 ms of dead output at the endpoint (the
                        // OS ramps every session write, stretching the 20 ms hold),
                        // audible as a split-second cut on every timeline/lyrics
                        // click. -12 dB still masks the buffer-flush click, but the
                        // output never goes silent.
                        sv.SetLevel(savedMilli / 4 / 1000.0);
                        _player.Time = targetMs;
                        Thread.Sleep(SeekFadeMs);
                        if (!sv.SetLevel(savedMilli / 1000.0))
                        {
                            // A failed restore strands the session ducked — and
                            // Windows persists per-app session volume across app
                            // restarts. Re-resolve the session and retry once.
                            sv.Invalidate();
                            sv.SetLevel(savedMilli / 1000.0);
                        }
                        Volatile.Write(ref _rampCurrentMilli, savedMilli);
                    }
                    else
                    {
                        // Native integer volume (macOS/Linux). The callback-sink and
                        // OS-session cases are both handled above, so this is the only
                        // remaining path. The seek still applies immediately (nothing
                        // slow runs BEFORE it), so the worker's seek-vs-track-change
                        // timing is unchanged; the fade-in bails if a Play() swaps the
                        // track so it never fights PlayInternal's volume set.
                        var mediaAtSeek = _currentMedia;
                        // The user's intended level, NOT the live _player.Volume. A
                        // scrub applies one fade per coalesced seek, so reading the
                        // player back made every run the next run's ceiling: a seek
                        // landing inside the previous fade (or inside PlayInternal's
                        // 40ms track-start fade) captured a partial value — 0 at the
                        // worst moment — and restored to that permanently. Same source
                        // PlayInternal uses for targetVolume, so a seek can no longer
                        // drift the track away from the slider.
                        var restoreVol = GetTargetVlcVolume();
                        SetPlayerVolumeGuarded(_player, 0);
                        _player.Time = targetMs;
                        FadePlayerVolumeFadeIn(restoreVol, SeekFadeMs, mediaAtSeek);
                    }
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
        })
        {
            IsBackground = true,
            Name = "VlcSeekWorker",
            Priority = ThreadPriority.AboveNormal
        };
        worker.Start();
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
        if (_gaplessEngine)
        {
            _engineStagedPath = null;
            // Unblock the staging writer BEFORE Stop() joins its decoder thread
            // (a Write blocked on a full ring would deadlock the stop). The
            // abandoned segment is skipped by the sink's splice loop.
            try { Volatile.Read(ref _engineSegments[EngineSlotOf(_standbyPlayer)])?.Abandon(); } catch { }
        }
        try { _standbyPlayer.Stop(); } catch { }
        SetPlayerVolumeGuarded(_standbyPlayer, 0);
        try { _standbyPlayer.UnsetEqualizer(); } catch { }
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
        lock (_seekGate) { _latestSeekMs = -1; }

        // Engine first: Clear() unblocks any writer stuck in a full ring so the
        // player Stops/Disposes below can join their decoder threads.
        if (_gaplessEngine)
        {
            EngineClearAll();
            try { _gaplessSink?.Dispose(); } catch { }
            _gaplessSink = null;
        }

        // Unsubscribe BEFORE disposing the timer. OnEndReached ends with
        // _positionTimer.Start(), so an event landing between the Dispose and the
        // unsubscribe threw ObjectDisposedException on VLC's native event thread and
        // killed the process. (The handlers are also wrapped now, but ordering these
        // correctly means the window doesn't exist in the first place.)
        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnError;
        _standbyPlayer.EndReached -= OnEndReached;
        _standbyPlayer.EncounteredError -= OnError;

        _positionTimer.Stop();
        _positionTimer.Dispose();
        // Timer.Stop/Dispose don't wait for an in-flight callback; drain it (and
        // the seek worker) so the _player.Dispose() below can't free a handle a
        // late callback is still using.
        DrainPositionTimerCallback();
        DrainSeekWorker();

        CancelSkipCts();
        _skipCts.Dispose();
        lock (_volumeWriteLock)
        {
            try { _volumeTrailingCts?.Cancel(); } catch { }
            _volumeTrailingCts?.Dispose();
            _volumeTrailingCts = null;
        }

        // Wait (briefly) for any in-flight PlayInternal/crossfade worker to drain so
        // the native players/libvlc below aren't disposed under its feet. _disposed
        // is already set and _skipCts cancelled, so fades bail within a step; the
        // timeout keeps shutdown bounded if a worker is wedged inside libvlc.
        var lockHeld = false;
        try { lockHeld = _playbackLock.Wait(TimeSpan.FromSeconds(3)); }
        catch (ObjectDisposedException) { }

        try { _player.Stop(); } catch { }
        try { _standbyPlayer.Stop(); } catch { }

        lock (_equalizerLock)
        {
            _equalizer?.Dispose();
            _equalizer = null;
        }
        DebugLog.VlcBridgeChanged -= OnVlcBridgeChanged;
        lock (_devBridgeLock)
        {
            if (_devBridgeAttached)
            {
                try { _libVlc.Log -= OnVlcLogForBridge; } catch { }
                _devBridgeAttached = false;
            }
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
        if (lockHeld)
        {
            try { _playbackLock.Release(); } catch { }
        }
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

    private void OnVlcBridgeChanged()
    {
        bool justAttached = false;
        lock (_devBridgeLock)
        {
            var want = DebugLog.VlcBridgeEnabled && !_disposed;
            if (want == _devBridgeAttached) return;
            try
            {
                if (want) _libVlc.Log += OnVlcLogForBridge;
                else _libVlc.Log -= OnVlcLogForBridge;
                _devBridgeAttached = want;
                justAttached = want;
            }
            catch
            {
                // Bridging is best-effort instrumentation — never break playback.
                _devBridgeAttached = false;
            }
        }
        if (justAttached)
            DebugLog.Write("VLC", "audio-engine log bridge on — VLC warnings/errors will appear here");
    }

    private void OnVlcLogForBridge(object? sender, LogEventArgs e)
    {
        var msg = $"{e.Level} {e.Module}: {e.Message}";
        var now = Stopwatch.GetTimestamp();

        // Runs on the VLC thread that emitted the line, so everything under this lock
        // must stay allocation-light and I/O-free — blocking here blocks a demux or
        // aout thread, which is the very stall we are trying to catch.
        string[]? context = null;
        lock (_devBridgeLock)
        {
            _devBridgeRing[_devBridgeRingNext] = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            _devBridgeRingNext = (_devBridgeRingNext + 1) % DevBridgeRingSize;

            if (e.Level == LogLevel.Error &&
                (_devBridgeLastContextTicks == 0 ||
                 now - _devBridgeLastContextTicks > Stopwatch.Frequency * DevBridgeContextCooldownSec))
            {
                _devBridgeLastContextTicks = now;
                context = SnapshotDevBridgeRingLocked();
            }

            if (e.Level is not (LogLevel.Warning or LogLevel.Error)) return;

            // Collapse identical repeats within 2s — a stutter spiral can emit the
            // same "playback too late" line many times per second, which would
            // flush the bounded session log. An Error that carries context is never
            // collapsed: dropping it would strand the ring dump with no cause line.
            if (context == null && msg == _devBridgeLastMsg &&
                now - _devBridgeLastTicks < Stopwatch.Frequency * 2) return;
            _devBridgeLastMsg = msg;
            _devBridgeLastTicks = now;
        }

        DebugLog.Write("VLC", msg);

        // Cooldown-limited, so a spiral dumps the ring once rather than per error.
        if (context != null)
        {
            DebugLog.Write("VLC", $"── last {context.Length} engine lines before the error ──");
            foreach (var line in context)
                DebugLog.Write("VLC", "  " + line);
            DebugLog.Write("VLC", "── end context ──");
        }
    }

    /// <summary>Ring contents oldest-first. Caller holds <c>_devBridgeLock</c>.</summary>
    private string[] SnapshotDevBridgeRingLocked()
    {
        var ordered = new List<string>(DevBridgeRingSize);
        for (var i = 0; i < DevBridgeRingSize; i++)
        {
            var line = _devBridgeRing[(_devBridgeRingNext + i) % DevBridgeRingSize];
            if (line != null) ordered.Add(line);
        }
        return ordered.ToArray();
    }

    private void WriteVlcDiag(string line)
    {
        var w = _vlcDiagWriter;
        if (w == null) return;
        // LibVLC quotes the full MRL in many of its lines; stream URLs embed auth
        // tokens, so scrub query strings before anything lands in the diag file.
        line = LogRedaction.Scrub(line);
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

        // Standard VLC.app install (covers `brew install --cask vlc` and manual
        // installs) stays first: a user-installed VLC is newer than our bundle.
        // Second choice is the libvlc payload the CI .app packaging step bundles
        // at Contents/MacOS/libvlc (dylibs + plugins/) — the VideoLAN.LibVLC.Mac
        // NuGet was dropped because its 3.0.21 pin never existed on nuget.org
        // and restore floated to an abandoned 2019 payload (AUDIT H7/H8).
        // The bundle mirrors VLC.app's lib/ + plugins/ sibling layout because the
        // plugins' install names reference libvlccore via @loader_path/../lib/.
        string[] candidates =
        {
            "/Applications/VLC.app/Contents/MacOS/lib",
            Path.Combine(AppContext.BaseDirectory, "libvlc", "lib"),
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

    /// <summary>
    /// Whether to pass --demux=avformat (the VBR-MP3/M4A O(1)-seek fix). True
    /// everywhere the avformat plugin is guaranteed to exist: Windows/macOS ship
    /// VideoLAN's full plugin payload, and the Linux AppImage bundles it and
    /// says so via NOCTIS_BUNDLED_VLC=1. Plain Linux system libvlc must NOT
    /// force it: distros split the ffmpeg plugins into optional packages (Arch:
    /// vlc-plugin-ffmpeg), and forcing an uninstalled demux module fails every
    /// media open with "VLC is unable to open the MRL" (issue #26).
    /// Internal for tests (InternalsVisibleTo Noctis.Tests).
    /// </summary>
    internal static bool ShouldForceAvformatDemux(bool isLinux, string? bundledVlcEnv)
        => !isLinux || bundledVlcEnv == "1";

    private static string BuildLibVlcMissingMessage()
    {
        if (OperatingSystem.IsLinux())
        {
            return "libvlc is required but was not found. Install it with your package manager:\n" +
                   "  Debian/Ubuntu:  sudo apt install vlc\n" +
                   "  Fedora:         sudo dnf install vlc\n" +
                   "  Arch:           sudo pacman -S vlc\n" +
                   "(On Arch, VLC's plugins are split into separate packages — if playback " +
                   "errors persist after installing, add vlc-plugins-all.)";
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
