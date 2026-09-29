using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.ExoPlayer.Upstream;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;
using AUri = Android.Net.Uri;
using JFile = Java.IO.File;
using JInteger = Java.Lang.Integer;
// Android.Media also defines AudioAttributes and MediaMetadata; alias the Media3 ones we
// mean (same pattern as AUri/JFile above) rather than fully-qualifying every use.
using AudioAttributes = AndroidX.Media3.Common.AudioAttributes;
using MediaMetadata = AndroidX.Media3.Common.MediaMetadata;

namespace Noctis.Android.Services;

/// <summary>
/// IAudioPlayer over Media3 ExoPlayer. Main-thread only (ExoPlayer's application looper
/// is the Android main thread, which is also Avalonia's UI thread here), so no locking
/// and no marshalling: listener callbacks and the position timer already run there.
/// Gapless: the next file is appended to ExoPlayer's item list by PrepareNext; the
/// automatic transition raises TrackEnded, and the ViewModel's Play(next) is answered
/// without a restart because the path matches the item already playing.
/// </summary>
public sealed class Media3AudioPlayer : IAudioPlayer
{
    private const int PositionPollMs = 250;

    private const int StreamConnectTimeoutMs = 5000;
    private const int StreamReadTimeoutMs = 15000;
    /// <summary>Retries of a failed load before the error surfaces (the stock policy allows 3).</summary>
    private const int StreamLoadRetries = 1;

    private readonly Context _context;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IExoPlayer _player;
    // The signed-in desktop and its device key for stream requests (SetStreamAuth); null when
    // signed out. Read on ExoPlayer's loader threads by StreamAuthResolver.
    private volatile StreamAuth? _streamAuth;
    private readonly Listener _listener;
    private readonly SessionForwardingPlayer _sessionPlayer;
    // The position poll runs on the main looper's own Handler, not a DispatcherTimer. At
    // DispatcherPriority.Background Avalonia pumps the timer together with frames, so it
    // stopped whenever the app drew nothing, screen off included (device run 2026-09-22:
    // engine at 110 s, UI and the 5 s resume checkpoint frozen at 00:32 until a tap). A
    // Handler message is delivered whether or not anything renders, on the same thread as
    // before (the main looper is Avalonia's UI thread here), so no marshalling changes.
    private readonly Handler _positionHandler = new(Looper.MainLooper!);
    private int _positionPumpGeneration;

    private Dictionary<string, Track>? _byPath;
    private bool _gapless = true;
    private bool _autoTransitionPending;
    private bool _disposed;
    private int _volume = 100;
    private int _volumeAdjust;
    private bool _muted;

    public event EventHandler? TrackEnded;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<string>? PlaybackError;
    public event EventHandler<TimeSpan>? DurationResolved;
    public event EventHandler<string>? OutputModeChanged;

    /// <summary>
    /// Raised when the MediaSession's own Next transport command arrives (notification,
    /// lock screen, Bluetooth) — see <see cref="SessionForwardingPlayer"/>. Android-specific:
    /// deliberately not on <see cref="IAudioPlayer"/> since desktop has no out-of-process
    /// transport to route. The Task 9 ViewModel wiring subscribes to this to advance the
    /// queue the same way the in-app Next button does.
    /// </summary>
    public event EventHandler? SessionNextRequested;

    /// <summary>Raised for the session's own Previous transport command. See <see cref="SessionNextRequested"/>.</summary>
    public event EventHandler? SessionPreviousRequested;

    /// <summary>Whether the app queue has a next/previous track, for the media session's
    /// transport buttons. ExoPlayer's own item list holds at most the current track plus a
    /// prepared gapless successor, so it is not the right source: under Repeat One it never
    /// has a next item, yet the queue always does. The Task 9 wiring points these at the
    /// ViewModel's queue.</summary>
    public Func<bool>? HasNextInQueue { get; set; }

    /// <summary>See <see cref="HasNextInQueue"/>.</summary>
    public Func<bool>? HasPreviousInQueue { get; set; }

    /// <summary>
    /// Where a desktop song ("noctis-remote://…") plays from: its downloaded file, else its
    /// https stream URL (INoctisAccountService.ResolvePlaybackUri); null for every other path.
    /// The MediaItem's id stays the library path, so the gapless handoff and the metadata
    /// lookup still match by it, and no URL reaches the session's MediaControllers.
    /// </summary>
    public Func<string, string?>? ResolveRemote { get; set; }

    public Media3AudioPlayer(Context context, ILibraryService library, IPersistenceService persistence)
    {
        _context = context;
        _library = library;
        _persistence = persistence;

        var attributes = new AudioAttributes.Builder()
            .SetUsage(C.UsageMedia)
            .SetContentType(C.AudioContentTypeMusic)
            .Build();
        // The stock source stack with one addition for http: streams from the owner's desktop
        // authenticate with its device key in a header (SetStreamAuth), never in the URL, so
        // the key is in no MediaItem URI and no logged address. The resolver adds the header
        // per request, and only to the signed-in desktop's stream endpoint (StreamAuthScope) —
        // not as a factory-wide default request property, which would hand the key to any URL
        // the player were ever given. Files and content:// URIs never reach it.
        // Timeouts: the stock 8 s connect + three backed-off retries left a desktop song buffering
        // for over a minute (66 s measured on the emulator) when the desktop was off, before the
        // queue could move on. A LAN desktop that is up answers in milliseconds, so fail fast.
        var http = new DefaultHttpDataSource.Factory()
            .SetConnectTimeoutMs(StreamConnectTimeoutMs)
            .SetReadTimeoutMs(StreamReadTimeoutMs);
        var httpSources = new ResolvingDataSource.Factory(http, new StreamAuthResolver(this));
        _player = new ExoPlayerBuilder(context)
            .SetMediaSourceFactory(new DefaultMediaSourceFactory(new DefaultDataSource.Factory(context, httpSources))
                .SetLoadErrorHandlingPolicy(new DefaultLoadErrorHandlingPolicy(StreamLoadRetries)))
            .SetAudioAttributes(attributes, true)     // true = Media3 handles audio focus (pause on loss, duck on transient)
            .SetHandleAudioBecomingNoisy(true)        // headphones unplugged → pause
            // CPU and Wi-Fi awake while playing with the screen off: a desktop stream stalls if
            // the radio powers down (WakeModeLocal held only the CPU). Files are unaffected.
            .SetWakeMode(C.WakeModeNetwork)
            .Build();
        _listener = new Listener(this);
        _player.AddListener(_listener);
        PlaybackEngine.Player = _player;

        // Wraps _player for the MediaSession only (PlaybackEngine.SessionPlayer): intercepts
        // the session's own Next/Previous so they route through the queue instead of seeking
        // ExoPlayer's item list directly. See SessionForwardingPlayer's doc comment.
        _sessionPlayer = new SessionForwardingPlayer(this, _player);
        PlaybackEngine.SessionPlayer = _sessionPlayer;

        OutputLatency = EstimateOutputLatency(context);
        _library.LibraryUpdated += OnLibraryUpdated;
    }

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan OutputLatency { get; }
    public long CurrentSessionId { get; private set; }
    public string? CurrentMediaPath { get; private set; }

    public int Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 100); ApplyVolume(); } }
    public int VolumeAdjust { get => _volumeAdjust; set { _volumeAdjust = Math.Clamp(value, -100, 100); ApplyVolume(); } }
    public bool IsMuted { get => _muted; set { _muted = value; ApplyVolume(); } }
    public long PendingSeekMs { get; set; } = -1;

    public bool ExclusiveModeActive => false;
    public bool EqualizerActive => false;
    public string OutputDescription => "Android AudioTrack (Media3)";
    public double ReplayGainAppliedDb => 0;

    public void Play(string filePath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath)) return;
        CurrentSessionId++;

        // Gapless handoff already happened inside ExoPlayer: the ViewModel is telling us
        // what we are already playing. Do not restart it.
        if (_autoTransitionPending && _player.CurrentMediaItem?.MediaId == filePath)
        {
            _autoTransitionPending = false;
            CurrentMediaPath = filePath;
            State = PlaybackState.Playing;
            RaiseDurationIfKnown();
            EnsureServiceStarted();
            StartPositionPump();
            return;
        }

        _autoTransitionPending = false;
        _player.SetMediaItem(BuildItem(filePath));
        _player.Prepare();
        if (PendingSeekMs >= 0)
        {
            _player.SeekTo(PendingSeekMs);
            PendingSeekMs = -1;
        }
        ApplyVolume();
        _player.Play();
        CurrentMediaPath = filePath;
        Position = TimeSpan.Zero;
        State = PlaybackState.Playing;
        EnsureServiceStarted();
        StartPositionPump();
    }

    public void Pause()
    {
        if (_disposed || State != PlaybackState.Playing) return;
        _player.Pause();
        State = PlaybackState.Paused;
        // Only our own Pause() stops the poll: an externally-caused pause (audio focus,
        // headphone unplug, lock-screen) never calls this, so the timer keeps ticking and
        // OnPlayerPosition's "the tick is where the UI catches up" resync still fires.
        StopPositionPump();
    }

    public void Resume()
    {
        if (_disposed || State != PlaybackState.Paused) return;
        _player.Play();
        State = PlaybackState.Playing;
        StartPositionPump();
    }

    public void Stop()
    {
        if (_disposed) return;
        StopPositionPump();
        _player.Stop();
        _player.ClearMediaItems();
        _autoTransitionPending = false;
        CurrentMediaPath = null;
        Position = TimeSpan.Zero;
        State = PlaybackState.Stopped;
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || State == PlaybackState.Stopped) return;
        _player.SeekTo((long)position.TotalMilliseconds);
        Position = position;
        PositionChanged?.Invoke(this, position);
    }

    public void SetGapless(bool enabled)
    {
        _gapless = enabled;
        if (!enabled) CancelPreparedNext();
    }

    public void PrepareNext(string filePath, long startPositionMs = -1)
    {
        if (_disposed || !_gapless || _player.MediaItemCount == 0) return;
        var current = _player.CurrentMediaItemIndex;
        if (_player.MediaItemCount > current + 1 && _player.GetMediaItemAt(current + 1)?.MediaId == filePath)
            return; // already queued
        TrimAfterCurrent();
        _player.AddMediaItem(BuildItem(filePath));
    }

    public void CancelPreparedNext()
    {
        if (_disposed) return;
        TrimAfterCurrent();
    }

    private void TrimAfterCurrent()
    {
        var current = _player.CurrentMediaItemIndex;
        while (_player.MediaItemCount > current + 1)
            _player.RemoveMediaItem(_player.MediaItemCount - 1);
    }

    /// <summary>
    /// The signed-in desktop ("https://host:port") and its device key, sent as the
    /// X-Noctis-Key header with that desktop's stream requests; nulls once signed out. Set only
    /// while signed in. Any thread: the next request reads it.
    /// </summary>
    public void SetStreamAuth(string? serverUrl, string? deviceKey) =>
        _streamAuth = string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(deviceKey) ? null : new StreamAuth(serverUrl, deviceKey);

    private sealed record StreamAuth(string ServerUrl, string DeviceKey);

    public void SetPlaybackRate(double rate)
    {
        if (_disposed) return;
        _player.SetPlaybackSpeed((float)Math.Clamp(rate, 0.5, 2.0));
    }

    public void CommitVolume() { }
    public void SetNormalization(bool enabled) { }
    public void SetExclusiveMode(bool enabled) { }
    public void ApplyReplayGain(string mode, double preampDb) { }
    public void SetCrossfade(bool enabled, int durationSeconds, AutoMixFadeCurve fadeCurve = AutoMixFadeCurve.SmoothEase, bool fadeOut = true, bool overlap = false) { }
    public void SetPitchSemitones(double semitones) { }
    public void SetUpmixMode(string mode) { }
    public void SetAdvancedEqualizer(bool enabled, float[] bands, float preampDb) { }

    // ── ExoPlayer callbacks (main thread) ──

    private void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == BasePlayer.InterfaceConsts.StateReady)
        {
            RaiseDurationIfKnown();
        }
        else if (playbackState == BasePlayer.InterfaceConsts.StateEnded)
        {
            // End of the LAST item (an auto-advance to a queued item raises
            // OnMediaItemTransition instead, never Ended).
            StopPositionPump();
            State = PlaybackState.Stopped;
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMediaItemTransition(MediaItem? item, int reason)
    {
        if (reason != BasePlayer.InterfaceConsts.MediaItemTransitionReasonAuto || item == null) return;
        // ExoPlayer moved to the item PrepareNext queued: tell the ViewModel the previous
        // track ended; its Play(next) will match this item and leave it running.
        _autoTransitionPending = true;
        CurrentMediaPath = item.MediaId;
        Position = TimeSpan.Zero;
        TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerError(PlaybackException error)
    {
        StopPositionPump();
        State = PlaybackState.Stopped;
        // Redacted: the text reaches the log and the Now Playing error line.
        PlaybackError?.Invoke(this, LogRedactor.Redact($"{error.ErrorCodeName}: {error.Message}"));
    }

    private void OnIsPlayingChanged(bool isPlaying)
    {
        // Audio focus loss, headphone unplug and lock-screen Pause all land here without a
        // Pause() call from us. Mirror it so the ViewModel's next toggle does the right thing.
        // No PlayWhenReady check: Media3 only clears it for a PERMANENT focus loss. A
        // transient loss (e.g. an incoming call) is signalled as a playback-suppression
        // reason with PlayWhenReady left true — requiring it false here would miss that case
        // and leave State stuck on Playing over a frozen clock. StateReady is still required:
        // it is what stops our own Stop() (which drives the state to Idle) from re-entering
        // here and being misread as an external pause.
        if (!isPlaying && State == PlaybackState.Playing && _player.PlaybackState == BasePlayer.InterfaceConsts.StateReady)
        {
            // Deliberately do NOT stop the poll timer here (unlike our own Pause()): it must
            // keep ticking through an external pause, because PollPosition's tick is the only
            // thing that drives OnPlayerPosition, which is how the ViewModel notices this
            // state change happened at all.
            State = PlaybackState.Paused;
        }
        else if (isPlaying && State == PlaybackState.Paused)
        {
            // Mirror image of our own Resume(): a session Play (notification, lock screen,
            // Bluetooth) reaches ExoPlayer directly — SessionForwardingPlayer only intercepts
            // seek-to-next/previous, not play/pause — so nothing else here restarts the timer.
            // Without this the UI freezes (no more PositionChanged ticks) and IsPlaying/State
            // silently disagree until the next unrelated tick, if any.
            State = PlaybackState.Playing;
            StartPositionPump();
        }
    }

    /// <summary>
    /// Starts (or restarts) the 250 ms position poll. Idempotent in effect: a restart orphans
    /// the running chain, whose next tick sees a stale generation and stops, so there is never
    /// more than one live chain and no RemoveCallbacks bookkeeping.
    /// </summary>
    private void StartPositionPump()
    {
        var generation = ++_positionPumpGeneration;
        _positionHandler.PostDelayed(() => PositionTick(generation), PositionPollMs);
    }

    private void StopPositionPump() => _positionPumpGeneration++;

    private void PositionTick(int generation)
    {
        if (_disposed || generation != _positionPumpGeneration) return;
        PollPosition();
        _positionHandler.PostDelayed(() => PositionTick(generation), PositionPollMs);
    }

    private void PollPosition()
    {
        if (_disposed || CurrentMediaPath == null) return;
        var ms = _player.CurrentPosition;
        if (ms < 0) return;
        Position = TimeSpan.FromMilliseconds(ms);
        PositionChanged?.Invoke(this, Position);
    }

    private void RaiseDurationIfKnown()
    {
        var ms = _player.Duration;
        if (ms == C.TimeUnset || ms <= 0) return;
        Duration = TimeSpan.FromMilliseconds(ms);
        DurationResolved?.Invoke(this, Duration);
    }

    private void ApplyVolume()
    {
        if (_disposed) return;
        var gain = _muted ? 0f : (_volume / 100f) * (1f + _volumeAdjust / 100f);
        _player.Volume = Math.Clamp(gain, 0f, 1f);
    }

    private void EnsureServiceStarted()
    {
        // No one-way latch: OnTaskRemoved stops the service whenever the app is swiped away
        // while not playing (the normal case) while this player instance lives on across a
        // reopen, so a latch here would leave the next Play() with no MediaSession, no
        // notification and no foreground-service protection. StartService on an
        // already-running service is cheap. API 26+ throws if called while the app has no
        // visible activity in the foreground (background start restriction); log and carry
        // on rather than crash playback over a missing notification.
        //
        // StartService, NOT StartForegroundService, now that the service really does go
        // foreground. Media3 owns that promotion: MediaNotificationManager.startForeground()
        // calls ContextCompat.startForegroundService() and Util.setForegroundServiceNotification()
        // back to back itself (verified against the 1.11.0 bytecode), so the 5-second
        // startForegroundService → startForeground contract is opened and honoured in one
        // synchronous step, with its own start intent that MediaSessionService.onStartCommand
        // recognises. If we opened that contract here instead, we would owe the system a
        // startForeground() that only arrives after Media3's internal MediaController has
        // connected and decided the player should be in the foreground — asynchronous, and
        // never at all for a paused player — so a background start would trade a missing
        // notification for a ForegroundServiceDidNotStartInTime kill. All this call has to do
        // is get the service instantiated so OnCreate can AddSession; it runs right after
        // _player.Play() from a visible activity, which is exactly when a plain start is legal.
        try
        {
            _context.StartService(new Intent(_context, typeof(NoctisPlaybackService)));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Audio", $"StartService failed: {ex.Message}");
        }
    }

    private MediaItem BuildItem(string path)
    {
        var uri = PlaybackUri(path);

        var meta = new MediaMetadata.Builder();
        var track = Lookup(path);
        if (track != null)
        {
            meta.SetTitle(track.Title).SetArtist(track.Artist).SetAlbumTitle(track.Album);
            // The bytes, not a file:// artworkUri. SystemUI draws the notification and the
            // lock screen in its own process and cannot read a path inside our private files
            // dir, so a URI there renders as a blank cover. Cached covers are small and this
            // is a private-storage read, so it stays cheap enough for the track-change path;
            // a missing or unreadable file must never take the rest of the metadata down
            // with it, hence the guard and the catch.
            // A track with its own embedded cover (TrackArtwork) shows that, not its album's.
            var art = !string.IsNullOrEmpty(track.AlbumArtworkPath) && File.Exists(track.AlbumArtworkPath)
                ? track.AlbumArtworkPath
                : _persistence.GetArtworkPath(track.AlbumId);
            if (File.Exists(art))
            {
                try
                {
                    meta.SetArtworkData(File.ReadAllBytes(art),
                        JInteger.ValueOf(MediaMetadata.PictureTypeFrontCover));
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Audio", $"Artwork read failed for '{track.Title}': {ex.Message}");
                }
            }
        }
        else
        {
            meta.SetTitle(FallbackTitle(path));
        }

        return new MediaItem.Builder()
            .SetUri(uri)
            .SetMediaId(path)
            .SetMediaMetadata(meta.Build())
            .Build();
    }

    /// <summary>What ExoPlayer reads for <paramref name="path"/>: a desktop song's download or
    /// stream (<see cref="ResolveRemote"/>), a SAF content:// URI, or a file.</summary>
    private AUri PlaybackUri(string path)
    {
        string? resolved = null;
        try
        {
            resolved = ResolveRemote?.Invoke(path);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Audio", $"Resolving a desktop song failed: {ex.Message}");
        }
        if (resolved != null)
        {
            return resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase) || resolved.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? AUri.Parse(resolved)!
                : AUri.FromFile(new JFile(resolved))!;
        }
        // An unresolved desktop song (signed out) falls through to a file that is not there:
        // ExoPlayer reports a source error, which the ViewModel handles like any unreadable file.
        return path.StartsWith("content://", StringComparison.Ordinal)
            ? AUri.Parse(path)!
            : AUri.FromFile(new JFile(path))!;
    }

    /// <summary>
    /// Title for a path with no library match. GetFileNameWithoutExtension is meaningless for
    /// a SAF/MediaStore content:// URI like "content://…/document/…1234" (no file name, just
    /// an opaque id in the path) — decode the URI's last path segment instead, or fall back to
    /// a plain label when even that is empty. Any other scheme (a desktop song's
    /// noctis-remote://, a URL) or a query is never shown: it is no title, and the notification
    /// and every MediaController would see it.
    /// </summary>
    private static string FallbackTitle(string path)
    {
        const string unknown = "Unknown track";
        if (path.StartsWith("content://", StringComparison.Ordinal))
        {
            var last = AUri.Parse(path)?.LastPathSegment;
            var decoded = string.IsNullOrEmpty(last) ? null : AUri.Decode(last);
            return string.IsNullOrWhiteSpace(decoded) ? unknown : decoded;
        }
        if (path.Contains("://", StringComparison.Ordinal) || path.Contains('?')) return unknown;
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? unknown : name;
    }

    private Track? Lookup(string path)
    {
        if (_byPath == null)
        {
            try
            {
                // Tracks is the live backing list; a background scan can mutate it mid-enumeration
                // on this same UI thread's track-change path. Snapshot before grouping and, if the
                // snapshot itself races a mutation, fall back to no metadata for this item rather
                // than crashing the transition — _byPath stays null so the next call retries.
                _byPath = _library.Tracks.ToArray().GroupBy(t => t.FilePath).ToDictionary(g => g.Key, g => g.First());
            }
            catch (InvalidOperationException ex)
            {
                DebugLog.Write("Audio", $"Track lookup snapshot raced a library update: {ex.Message}");
                return null;
            }
        }
        return _byPath.GetValueOrDefault(path);
    }

    private void OnLibraryUpdated(object? sender, EventArgs e) => _byPath = null;

    /// <summary>
    /// ExoPlayer's position already tracks the AudioTrack head, so what remains is the
    /// output buffer the mixer holds: two buffers of the device's native size is the
    /// usual figure. Zero when the properties are unavailable.
    /// </summary>
    private static TimeSpan EstimateOutputLatency(Context context)
    {
        try
        {
            var audio = (AudioManager?)context.GetSystemService(Context.AudioService);
            var frames = int.TryParse(audio?.GetProperty(AudioManager.PropertyOutputFramesPerBuffer), out var f) ? f : 0;
            var rate = int.TryParse(audio?.GetProperty(AudioManager.PropertyOutputSampleRate), out var r) ? r : 0;
            if (frames > 0 && rate > 0)
                return TimeSpan.FromSeconds(2.0 * frames / rate);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Audio", $"Output latency probe failed: {ex.Message}");
        }
        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPositionPump();
        _library.LibraryUpdated -= OnLibraryUpdated;
        _player.RemoveListener(_listener);
        // NoctisPlaybackService may still hold a MediaSession wrapping _sessionPlayer (which
        // wraps _player): stop it before releasing the player so its OnDestroy releases the
        // session while the player it wraps is still alive, instead of the session touching a
        // released Java object later. StopService is a request, not a synchronous wait for
        // OnDestroy, but ordering it first is the best this process can do.
        try
        {
            _context.StopService(new Intent(_context, typeof(NoctisPlaybackService)));
        }
        catch (Exception ex)
        {
            DebugLog.Write("Audio", $"StopService failed: {ex.Message}");
        }
        _player.Release();
        if (ReferenceEquals(PlaybackEngine.Player, _player)) PlaybackEngine.Player = null;
        if (ReferenceEquals(PlaybackEngine.SessionPlayer, _sessionPlayer)) PlaybackEngine.SessionPlayer = null;
        // Deterministic JNI global-ref release rather than waiting on finalization.
        _listener.Dispose();
        _sessionPlayer.Dispose();
    }

    private void RaiseSessionNextRequested() => SessionNextRequested?.Invoke(this, EventArgs.Empty);
    private void RaiseSessionPreviousRequested() => SessionPreviousRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Adds the device key header to the signed-in desktop's stream requests; every
    /// other request passes through untouched. Runs on ExoPlayer's loader threads.</summary>
    private sealed class StreamAuthResolver : Java.Lang.Object, ResolvingDataSource.IResolver
    {
        private readonly Media3AudioPlayer _owner;
        public StreamAuthResolver(Media3AudioPlayer owner) => _owner = owner;

        public DataSpec? ResolveDataSpec(DataSpec? dataSpec)
        {
            var auth = _owner._streamAuth;
            if (dataSpec == null || auth == null || !StreamAuthScope.Allows(dataSpec.Uri?.ToString(), auth.ServerUrl)) return dataSpec;
            return dataSpec.WithAdditionalHeaders(new Dictionary<string, string> { [StreamAuthScope.HeaderName] = auth.DeviceKey });
        }

        public AUri? ResolveReportedUri(AUri? uri) => uri;
    }

    /// <summary>Java-side listener; every IPlayerListener method has a default body, so only these four are overridden.</summary>
    private sealed class Listener : Java.Lang.Object, IPlayerListener
    {
        private readonly Media3AudioPlayer _owner;
        public Listener(Media3AudioPlayer owner) => _owner = owner;
        public void OnPlaybackStateChanged(int playbackState) => _owner.OnPlaybackStateChanged(playbackState);
        public void OnMediaItemTransition(MediaItem? mediaItem, int reason) => _owner.OnMediaItemTransition(mediaItem, reason);
        public void OnPlayerError(PlaybackException error) => _owner.OnPlayerError(error);
        public void OnIsPlayingChanged(bool isPlaying) => _owner.OnIsPlayingChanged(isPlaying);
    }

    /// <summary>
    /// Wraps _player for the MediaSession only. MediaSession.Builder(context, player) hands
    /// session-originated transport commands — the notification, lock screen and Bluetooth
    /// Next/Previous — straight to the wrapped player's SeekToNextMediaItem/
    /// SeekToPreviousMediaItem. That jumps ExoPlayer's own media-item list with a SEEK
    /// transition reason, which OnMediaItemTransition ignores (only AUTO drives TrackEnded),
    /// so CurrentMediaPath, the ViewModel's CurrentTrack and the queue cursor all stay on the
    /// old track while a different one plays; when it then ends, TrackEnded advances the
    /// untouched cursor onto the same old track and plays it a second time from zero.
    /// Overriding these four methods to raise events instead keeps every transport path —
    /// in-app buttons and session controls alike — flowing through PlaybackQueue. Task 9's
    /// ViewModel wiring subscribes to SessionNextRequested/SessionPreviousRequested.
    ///
    /// Media3 derives the notification's Next/Previous buttons (and the media-button dispatch
    /// gate) from THIS player's AvailableCommands/IsCommandAvailable/HasNextMediaItem/
    /// HasPreviousMediaItem, which ForwardingPlayer forwards straight to the wrapped ExoPlayer
    /// by default. But ExoPlayer's own item list holds at most the current track plus one
    /// prepared gapless successor — "has next" there means "a gapless item happens to be
    /// queued right now", not "the app queue has a next track". Left unoverridden, the button
    /// vanishes whenever gapless is off, on the last prepared item, and permanently under
    /// Repeat One (PrepareUpcoming never queues a successor in that mode). Overriding all four
    /// to consult _owner.HasNextInQueue/HasPreviousInQueue instead keeps the notification in
    /// sync with what Next/Previous will actually do. Falls back to the base (ExoPlayer-driven)
    /// answer when a predicate is unset, so nothing regresses before Task 9 wires them.
    /// </summary>
    private sealed class SessionForwardingPlayer : ForwardingPlayer
    {
        private readonly Media3AudioPlayer _owner;
        public SessionForwardingPlayer(Media3AudioPlayer owner, IPlayer player) : base(player) => _owner = owner;

        public override void SeekToNext() => _owner.RaiseSessionNextRequested();
        public override void SeekToNextMediaItem() => _owner.RaiseSessionNextRequested();
        public override void SeekToPrevious() => _owner.RaiseSessionPreviousRequested();
        public override void SeekToPreviousMediaItem() => _owner.RaiseSessionPreviousRequested();

        public override bool HasNextMediaItem => _owner.HasNextInQueue?.Invoke() ?? base.HasNextMediaItem;
        public override bool HasPreviousMediaItem => _owner.HasPreviousInQueue?.Invoke() ?? base.HasPreviousMediaItem;

        public override bool IsCommandAvailable(int command)
        {
            if (IsNextCommand(command) && _owner.HasNextInQueue is { } hasNext) return hasNext();
            if (IsPreviousCommand(command) && _owner.HasPreviousInQueue is { } hasPrevious) return hasPrevious();
            return base.IsCommandAvailable(command);
        }

        public override PlayerCommands AvailableCommands
        {
            get
            {
                var commands = base.AvailableCommands;
                if (_owner.HasNextInQueue == null && _owner.HasPreviousInQueue == null) return commands;
                var builder = commands.BuildUpon();
                ApplyOverride(builder, BasePlayer.InterfaceConsts.CommandSeekToNext, _owner.HasNextInQueue);
                ApplyOverride(builder, BasePlayer.InterfaceConsts.CommandSeekToNextMediaItem, _owner.HasNextInQueue);
                ApplyOverride(builder, BasePlayer.InterfaceConsts.CommandSeekToPrevious, _owner.HasPreviousInQueue);
                ApplyOverride(builder, BasePlayer.InterfaceConsts.CommandSeekToPreviousMediaItem, _owner.HasPreviousInQueue);
                return builder.Build();
            }
        }

        private static void ApplyOverride(PlayerCommands.Builder builder, int command, Func<bool>? predicate)
        {
            if (predicate == null) return;
            if (predicate()) builder.Add(command); else builder.Remove(command);
        }

        private static bool IsNextCommand(int command) =>
            command == BasePlayer.InterfaceConsts.CommandSeekToNext || command == BasePlayer.InterfaceConsts.CommandSeekToNextMediaItem;

        private static bool IsPreviousCommand(int command) =>
            command == BasePlayer.InterfaceConsts.CommandSeekToPrevious || command == BasePlayer.InterfaceConsts.CommandSeekToPreviousMediaItem;
    }
}
