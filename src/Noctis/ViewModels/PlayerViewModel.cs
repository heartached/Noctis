using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Controls audio playback and exposes state for the persistent bottom playback bar.
/// Owns the relationship between the queue and the audio player service.
/// </summary>
public partial class PlayerViewModel : ViewModelBase
{
    private readonly IAudioPlayer _audioPlayer;

    /// <summary>Lead of <see cref="Position"/> over the speaker (output buffer depth);
    /// the lyrics clock subtracts it. See <see cref="IAudioPlayer.OutputLatency"/>.</summary>
    public TimeSpan OutputLatency => _audioPlayer.OutputLatency;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IAnimatedCoverService _animatedCovers;
    // Re-reads queued non-library files on restore (GitHub #86). Null: they are dropped.
    private readonly IMetadataService? _metadata;

    // ── Recently-played memory (feeds shuffle recency deprioritization) ──
    private readonly LinkedList<Guid> _recentlyPlayedOrder = new();
    private readonly HashSet<Guid> _recentlyPlayed = new();
    private const int RecentlyPlayedCapacity = 50;

    // Insertion-order tracking, not true LRU: re-playing an already-tracked id intentionally
    // does not refresh its position. Acceptable for a shuffle deprioritization heuristic.
    private void MarkRecentlyPlayed(Track t)
    {
        if (_recentlyPlayed.Add(t.Id)) _recentlyPlayedOrder.AddLast(t.Id);
        while (_recentlyPlayedOrder.Count > RecentlyPlayedCapacity)
        {
            var oldest = _recentlyPlayedOrder.First!.Value;
            _recentlyPlayedOrder.RemoveFirst();
            _recentlyPlayed.Remove(oldest);
        }
    }

    // ── Observable properties bound to the playback bar ──

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseTooltip))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    [NotifyPropertyChangedFor(nameof(LyricsBackgroundHoldsWhilePaused))]
    private PlaybackState _state = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private double _positionFraction; // 0.0 – 1.0 for slider
    [ObservableProperty] private int _volume = 75;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private Bitmap? _albumArt;

    /// <summary>
    /// The cover is a shared <see cref="ArtworkCache"/> bitmap. Registering this
    /// holder is what keeps the cache from disposing it under the lyrics backdrops
    /// while it is the current track, and releasing the old one lets an evicted
    /// cover actually go away on the next track change.
    /// </summary>
    partial void OnAlbumArtChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        ArtworkCache.Acquire(newValue);
        ArtworkCache.Release(oldValue);
    }
    [ObservableProperty] private string? _currentAnimatedCoverPath;

    // ── Music video (Discord, aaron 09-15): a clip next to the song replaces the cover
    // on the lyrics page and follows playback. Video only — the song's own audio plays.
    [ObservableProperty] private string? _currentMusicVideoPath;
    [ObservableProperty] private bool _musicVideosEnabled;
    /// <summary>0 = flat, otherwise the rounded corner radius of the video frame.</summary>
    [ObservableProperty] private double _musicVideoCornerRadius = 18;
    public bool HasMusicVideo => !string.IsNullOrEmpty(CurrentMusicVideoPath);
    /// <summary>Whether a clip exists for the current song regardless of the toggle: the
    /// player menu's "Music video" item shows only then (Discord, aaron 2026-09-21: it
    /// showed for songs with no video), and stays while the feature is off so it can be
    /// switched back on.</summary>
    [ObservableProperty] private bool _currentTrackHasMusicVideoFile;
    partial void OnCurrentMusicVideoPathChanged(string? value) => OnPropertyChanged(nameof(HasMusicVideo));
    partial void OnMusicVideosEnabledChanged(bool value) => ResolveMusicVideo();

    private void ResolveMusicVideo()
    {
        var track = CurrentTrack;
        var found = track != null && !track.IsRemoteStream
            ? Helpers.MusicVideoLocator.Find(track.FilePath)
            : null;
        CurrentTrackHasMusicVideoFile = found != null;
        CurrentMusicVideoPath = MusicVideosEnabled ? found : null;
    }
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _remainingTimeText = "0:00";
    [ObservableProperty] private bool _isShuffleEnabled;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.Off;
    [ObservableProperty] private bool _isQueuePopupOpen;
    [ObservableProperty] private bool _autoMixEnabled;
    [ObservableProperty] private AutoMixTransitionMode _autoMixTransitionMode = AutoMixTransitionMode.Off;
    /// <summary>Gapless playback (Settings > Audio): natural track changes hand off to a
    /// pre-decoded standby player instead of the audible stop/parse/start path.</summary>
    [ObservableProperty] private bool _gaplessEnabled = true;
    /// <summary>Autoplay (Settings > Playback): when the queue is exhausted by a natural
    /// track end, keep playing similar tracks from the library (same genre, then same
    /// primary artist). Driven by Settings; read at each queue exhaustion, so flipping
    /// it mid-session arms or disarms the next one. Off by default.</summary>
    [ObservableProperty] private bool _autoplayEnabled;

    // ── Signal path / quality badge (Roon-style) ──
    /// <summary>Overall chain quality: "Bit-perfect", "Hi-Res Lossless", "Lossless", "Enhanced", "Lossy".</summary>
    [ObservableProperty] private string _signalPathQuality = "";
    /// <summary>Badge dot colour for the current quality tier (hex string).</summary>
    [ObservableProperty] private string _signalPathColor = "#9CA3AF";
    /// <summary>The audio chain, stage by stage, for the expanded badge flyout.</summary>
    [ObservableProperty] private IReadOnlyList<SignalPathStage> _signalPathStages = Array.Empty<SignalPathStage>();
    [ObservableProperty] private AutoMixStrength _autoMixStrength = AutoMixStrength.Balanced;
    [ObservableProperty] private bool _autoMixRemoveSilence = true;
    [ObservableProperty] private bool _autoMixAvoidAlbums = true;
    [ObservableProperty] private bool _autoMixBeatMatch = true;
    [ObservableProperty] private bool _trackTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _artistMarqueeEnabled = true;

    // ── Player island extras (Settings → Appearance → Player Island Buttons) ──
    // Podcast/audiobook transport (Discord, Luwi, 08-26): skip back/forward,
    // playback speed and a sleep-timer button, each opt-in. Mirrored from
    // SettingsViewModel like the marquee flags — the bar's DataContext is this VM.
    [ObservableProperty] private bool _islandShowSkipButtons;
    [ObservableProperty] private bool _islandShowPlaybackSpeed;
    [ObservableProperty] private bool _islandShowSleepTimer;
    /// <summary>GitHub #59: shuffle on the island, after Repeat.</summary>
    [ObservableProperty] private bool _islandShowShuffle;
    /// <summary>GitHub #94: EQ on/off on the island, after Shuffle.</summary>
    [ObservableProperty] private bool _islandShowEqualizer;
    /// <summary>Repeat after Next, and the favorite heart on the right: opt-in since the
    /// track-box layout, so the stock bar is transport + box + lyrics/queue/volume.</summary>
    [ObservableProperty] private bool _islandShowRepeat;
    [ObservableProperty] private bool _islandShowFavorite;
    /// <summary>GitHub #80: the mini player button in the right cluster (Settings → Player).</summary>
    [ObservableProperty] private bool _islandShowMiniPlayer = true;
    /// <summary>Elapsed / remaining time inside the island's track box (Settings → Player).</summary>
    [ObservableProperty] private bool _islandShowTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandSkipLabel))]
    [NotifyPropertyChangedFor(nameof(SkipBackTooltip))]
    [NotifyPropertyChangedFor(nameof(SkipForwardTooltip))]
    private int _islandSkipSeconds = 15;

    public string IslandSkipLabel => IslandSkipSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string SkipBackTooltip => $"Back {IslandSkipSeconds} seconds";
    public string SkipForwardTooltip => $"Forward {IslandSkipSeconds} seconds";

    /// <summary>Playback speed as a percent (75 … 200); 100 = normal. Session-only on
    /// purpose: a forgotten 1.5× would make every album sound wrong on the next launch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackRate))]
    [NotifyPropertyChangedFor(nameof(PlaybackRateText))]
    [NotifyPropertyChangedFor(nameof(IsPlaybackRateChanged))]
    [NotifyPropertyChangedFor(nameof(IsSpeedOrPitchChanged))]
    private int _playbackRatePercent = 100;

    public double PlaybackRate => PlaybackRatePercent / 100.0;
    public string PlaybackRateText => (PlaybackRatePercent / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
    public bool IsPlaybackRateChanged => PlaybackRatePercent != 100;

    partial void OnPlaybackRatePercentChanged(int value)
    {
        _audioPlayer.SetPlaybackRate(value / 100.0);
        DebugLogger.Info(DebugLogger.Category.Playback, "PlaybackRate", $"percent={value}");
    }

    /// <summary>Pitch shift in semitones (−12 … +12), independent of speed. Session-only like the speed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchText))]
    [NotifyPropertyChangedFor(nameof(IsPitchChanged))]
    [NotifyPropertyChangedFor(nameof(IsSpeedOrPitchChanged))]
    private int _pitchSemitones;

    public string PitchText => PitchSemitones == 0 ? "±0" : (PitchSemitones > 0 ? "+" : "") + PitchSemitones.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public bool IsPitchChanged => PitchSemitones != 0;
    /// <summary>Lights the island speed button when either control is off its default.</summary>
    public bool IsSpeedOrPitchChanged => IsPlaybackRateChanged || IsPitchChanged;

    partial void OnPitchSemitonesChanged(int value)
    {
        _audioPlayer.SetPitchSemitones(value);
        DebugLogger.Info(DebugLogger.Category.Playback, "Pitch", $"semitones={value}");
    }

    /// <summary>Island pitch menu; parameter is a semitone offset ("-3" … "3") or "0" to reset.</summary>
    [RelayCommand]
    private void SetPitch(string? semitones)
    {
        if (int.TryParse(semitones, out var st) && st is >= -12 and <= 12)
            PitchSemitones = st;
    }
    /// <summary>Opacity of the playback bar's glass fill (0–1). Driven by Settings; default
    /// 0.4 matches the original #66 alpha. Background only — controls/text stay opaque.</summary>
    [ObservableProperty] private double _islandBackgroundOpacity = 0.4;
    /// <summary>Opacity of the white track box (song-info card) inside the bar (0–1).
    /// Driven by Settings; 0 removes the card, leaving art/text straight on the pill.</summary>
    [ObservableProperty] private double _islandTrackBoxOpacity = 0.07;
    /// <summary>User-resized width of the persistent playback bar island. Hydrated from
    /// AppSettings.PlaybackBarWidth at startup and updated by the bar's edge-drag; the
    /// 626 default mirrors both the settings default and the XAML base width.</summary>
    [ObservableProperty] private double _playbackBarIslandWidth = 536;
    /// <summary>Whether the lyrics page's flowing-light blobs are shown in artwork
    /// background mode (issue #22). Driven by Settings like the marquee flags.</summary>
    [ObservableProperty] private bool _lyricsFlowingLightEnabled;
    /// <summary>See <see cref="AppSettings.LyricsFlowingStyle"/>; the lyrics page picks the layer from it.</summary>
    [ObservableProperty] private string _lyricsFlowingStyle = FlowingStyles.Drift;
    [ObservableProperty] private double _lyricsKawarpWarp = 1.0;
    [ObservableProperty] private int _lyricsKawarpBlur = 6;
    /// <summary>Live spectrum visualizer behind the lyrics page. Driven by Settings.</summary>
    [ObservableProperty] private bool _lyricsVisualizerEnabled;
    /// <summary>Visualizer look (a VisualizerStyle name). Driven by Settings.</summary>
    [ObservableProperty] private string _lyricsVisualizerStyle = VisualizerStyles.DefaultSetting;
    /// <summary>Visualizer paints with the artwork's colour (else white/accent). Driven by Settings.</summary>
    [ObservableProperty] private bool _lyricsVisualizerArtworkColor = true;
    /// <summary>The current cover's vibrant colour (ShareCardRenderer's path-cached hue-bucket
    /// vote, computed off the UI thread when the track changes); null without artwork.
    /// The visualizer surfaces tint their bars with it.</summary>
    [ObservableProperty] private Color? _artworkAccentColor;
    /// <summary>Looping video/GIF the lyrics page paints behind the lyrics (empty = none).
    /// Driven by Settings like the flags above; the page's VideoBackdrop binds it.</summary>
    [ObservableProperty] private string _lyricsBackgroundMediaPath = string.Empty;
    private string _lyricsBackgroundDefaultPath = string.Empty;
    private IReadOnlyDictionary<string, string> _lyricsBackgroundOverrides = new Dictionary<string, string>();
    /// <summary>Settings: freeze the background video while playback is paused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsBackgroundHoldsWhilePaused))]
    private bool _lyricsBackgroundPausesWithPlayback;
    /// <summary>What the lyrics page's VideoBackdrop binds as IsPaused.</summary>
    public bool LyricsBackgroundHoldsWhilePaused => LyricsBackgroundPausesWithPlayback && !IsPlaying;

    /// <summary>Settings hands over the default clip and the per-song/per-album overrides;
    /// <see cref="LyricsBackgroundMediaPath"/> is re-resolved for the current track.</summary>
    public void SetLyricsBackgroundSources(string defaultPath, IReadOnlyDictionary<string, string> overrides)
    {
        _lyricsBackgroundDefaultPath = defaultPath ?? string.Empty;
        _lyricsBackgroundOverrides = overrides ?? new Dictionary<string, string>();
        ResolveLyricsBackground();
    }

    /// <summary>The song's own clip, else its album's, else the default; a recorded clip whose
    /// file is gone is skipped so the page never binds a dead path.</summary>
    public string ResolveLyricsBackgroundFor(Track? track)
    {
        if (track != null)
        {
            if (TryOverride(Helpers.LyricsBackgroundOverrides.KeyForTrack(track), out var own)) return own;
            if (TryOverride(Helpers.LyricsBackgroundOverrides.KeyForAlbumId(track.AlbumId), out var album)) return album;
        }
        return _lyricsBackgroundDefaultPath;

        bool TryOverride(string key, out string path)
        {
            if (_lyricsBackgroundOverrides.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p) && File.Exists(p))
            {
                path = p;
                return true;
            }
            path = string.Empty;
            return false;
        }
    }

    private void ResolveLyricsBackground() => LyricsBackgroundMediaPath = ResolveLyricsBackgroundFor(CurrentTrack);
    /// <summary>Opt-in fullscreen lyrics focus — dims all but the active line and its
    /// closest neighbors while the lyrics page is fullscreen. Driven by Settings.</summary>
    [ObservableProperty] private bool _lyricsFullScreenFocusEnabled;
    /// <summary>Percent floor (0–60) under the dimmed lyric lines; above 0 every line
    /// stays faintly visible and clickable. Driven by Settings.</summary>
    [ObservableProperty] private int _lyricsMinLineOpacity;
    /// <summary>Whether TTML words split across several timed spans render unbroken
    /// (issue #32). Driven by Settings; the lyrics VM re-parses when it flips.</summary>
    [ObservableProperty] private bool _lyricsJoinSplitWords;
    /// <summary>Whether the lyrics show TTML translations / romanization / background vocals
    /// under each line (issue #78). Driven by Settings; display-only, no re-parse.</summary>
    [ObservableProperty] private bool _lyricsShowTranslations = true;
    [ObservableProperty] private bool _lyricsShowRomanization = true;
    [ObservableProperty] private bool _lyricsShowBackgroundVocals = true;

    // ── Lyrics page integration (flags + pass-through commands set up by MainWindowViewModel) ──

    [ObservableProperty] private bool _isLyricsPageActive;
    [ObservableProperty] private bool _isLyricsSyncedActive;
    [ObservableProperty] private bool _isLyricsPlainActive;
    [ObservableProperty] private bool _isLyricsSyncedAvailable;
    [ObservableProperty] private bool _canShareLyrics;

    /// <summary>
    /// Path to the current track's artwork file (or null if none).
    /// Used by surfaces that bind via <see cref="Controls.CachedImage"/> so the
    /// previous cover stays visible during async cache-miss decode — no flash on
    /// track switch. The Bitmap-based <see cref="AlbumArt"/> property is kept
    /// for legacy bindings (Lyrics) that haven't been migrated yet.
    /// </summary>
    [ObservableProperty] private string? _currentArtPath;

    /// <summary>True if there's any content loaded (current track or upcoming tracks in queue).</summary>
    public bool HasContent => CurrentTrack != null || UpNext.Count > 0;

    private Action? _selectLyricsSynced;
    private Action? _selectLyricsPlain;
    private Action? _openLyricsBackgroundColor;
    private Action? _removeLyrics;
    private Action? _shareLyrics;

    public string PlayPauseTooltip => State == PlaybackState.Playing ? "Pause" : "Play";

    /// <summary>True when playback is actively playing (not paused or stopped).</summary>
    public bool IsPlaying => State == PlaybackState.Playing;

    // ── Queue ──

    /// <summary>Upcoming tracks to play.</summary>
    public BulkObservableCollection<Track> UpNext { get; } = new();

    /// <summary>Previously played tracks (most recent first).</summary>
    public BulkObservableCollection<Track> History { get; } = new();

    /// <summary>Fires when a new track starts playing.</summary>
    public event EventHandler<Track>? TrackStarted;

    /// <summary>Fires when the user seeks to a new position.</summary>
    public event EventHandler<TimeSpan>? Seeked;

    private bool _isSeeking; // prevents feedback loop during drag

    /// <summary>True while the user is dragging the timeline. Position is then a target
    /// being steered, not a clock that is running — the seek is only committed on release
    /// — so anything extrapolating Position forward must stand down.</summary>
    public bool IsSeeking => _isSeeking;

    private DateTime _lastSeekTime = DateTime.MinValue; // prevents stale position updates after seek
    private TimeSpan _pendingSeekTarget = TimeSpan.Zero; // latest seek target while dragging
    private bool _hasPendingSeekTarget; // whether a drag seek target is waiting to be committed
    private TimeSpan _lastCommittedSeekTarget; // the position we last seeked to (for anchoring)
    private const int SeekSettleWindowMs = 300; // must be less than VLC's file-caching (1000ms, see VlcAudioPlayer)
    private const int SeekDebounceMs = 60; // coalesces rapid clicks so VLC receives fewer seeks
    private const int TrackStartStalePositionGuardMs = 9000;
    private const int NaturalEndFallbackDelayMs = 1400;
    private const double NaturalEndToleranceSeconds = 0.75;
    private System.Threading.Timer? _seekDebounceTimer; // debounce timer for rapid seek clicks
    private System.Threading.Timer? _naturalEndFallbackTimer; // backup for missed VLC TrackEnded
    private volatile bool _positionUpdateQueued; // coalesces rapid VLC position dispatches
    private TimeSpan _latestVlcPosition; // latest position from VLC timer (written from timer thread)
    private List<Track> _originalQueue = new(); // stored when shuffle is enabled

    // The full set of tracks in the current playback cycle, in order, uncapped.
    //
    // Repeat All used to rebuild the queue from History, but TrimHistory caps History at
    // 50 entries — so cycling a 120-track playlist restarted at track 71 and permanently
    // dropped tracks 1-70, with no UI indication. History is a *display* list with a
    // display-sized cap; the repeat cycle needs the real queue.
    private List<Track> _repeatCycleTracks = new();
    private Action<string>? _navigateAction; // injected navigation action
    private Action<Track>? _viewAlbumAction; // injected from MainWindowViewModel
    private Action<Track>? _searchLyricsAction; // injected for search lyrics navigation
    private SidebarViewModel? _sidebar; // injected for playlist access
    private bool _suppressHasContentNotify; // prevents layout thrashing during batch queue updates
    private bool _isAdvancingQueue; // re-entrancy guard for AdvanceQueue
    private bool _autoMixAdvanceQueued; // prevents repeated early-advance triggers
    private long _pendingAutoMixNextStartMs = -1;
    private DateTime _autoMixCommitGuardUntilUtc = DateTime.MinValue;
    private DateTime _autoMixTransitionArmedUntilUtc = DateTime.MinValue;
    private string _lastAutoMixLogKey = string.Empty;
    private Guid _autoMixPreparedTrackId = Guid.Empty;
    private long _queueVersion;
    private AutoMixPreparedTransitionSnapshot? _autoMixPreparedSnapshot;
    private SettingsViewModel? _settings;
    private IPlayHistoryService? _playHistory; // injected for play/skip event logging

    // ── Track Radio ──
    private readonly IRadioService _radioService = new RadioService();
    [ObservableProperty] private bool _isRadioActive;
    private Track? _radioSeed;
    private bool _radioRefillInFlight;
    private const int RadioRefillThreshold = 5;
    private const int RadioBatchSize = 25;

    // ── Autoplay (tag-based queue continuation) ──
    private readonly IAutoplayService _autoplayService = new AutoplayService();
    // Everything autoplay picked this session, so it doesn't repeat itself until the
    // candidate pool is exhausted (then it's cleared and reuse is allowed).
    private readonly HashSet<Guid> _autoplayPickedIds = new();
    // Small visible batch (Apple Music-style): the first pick plays, the rest land in
    // Up Next so the queue popup shows where playback is heading.
    private const int AutoplayBatchSize = 5;

    public PlayerViewModel(IAudioPlayer audioPlayer, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers,
        IMetadataService? metadata = null)
    {
        _audioPlayer = audioPlayer;
        _library = library;
        _persistence = persistence;
        _animatedCovers = animatedCovers;
        _metadata = metadata;

        // Subscribe to audio player events
        _audioPlayer.PositionChanged += OnPositionChanged;
        _audioPlayer.TrackEnded += OnTrackEnded;
        _audioPlayer.PlaybackError += OnPlaybackError;
        _audioPlayer.DurationResolved += OnDurationResolved;
        // Output path can change after playback starts (exclusive engaged /
        // fell back) — keep the signal-path badge in sync.
        _audioPlayer.OutputModeChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshSignalPath);

        // Subscribe to library events
        _library.LibraryUpdated += OnLibraryUpdated;

        // Favorites are toggled on the library's Track instances (albums grid,
        // album detail, favorites page) while the queue may still hold pre-reload
        // instances of the same tracks — mirror the state by id so hearts stay
        // consistent everywhere.
        _library.FavoritesChanged += (_, _) => SyncQueueFavoritesFromLibrary();

        // Subscribe to queue changes to update HasContent (skipped during batch updates)
        UpNext.CollectionChanged += (_, _) => { if (!_suppressHasContentNotify) OnPropertyChanged(nameof(HasContent)); };
        History.CollectionChanged += (_, _) => { if (!_suppressHasContentNotify) OnPropertyChanged(nameof(HasContent)); };

        // Sync volume to audio player
        _audioPlayer.Volume = _volume;
    }

    // ── Commands ──────────────────────────────────────────────

    [RelayCommand]
    private void PlayPause()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "PlayPause", $"state={State}, track={CurrentTrack?.Title}");
        switch (State)
        {
            case PlaybackState.Playing:
                CancelAutoMixTransition("user paused");
                _audioPlayer.Pause();
                State = PlaybackState.Paused;
                // Pause is a natural resting point — capture the position so a
                // non-graceful exit restores "where you left off".
                SaveQueueStateInBackground();
                break;

            case PlaybackState.Paused:
                _audioPlayer.Resume();
                State = PlaybackState.Playing;
                break;

            case PlaybackState.Stopped:
                if (CurrentTrack != null)
                {
                    // Track loaded but stopped — replay it
                    PlayTrack(CurrentTrack);
                }
                else if (UpNext.Count > 0)
                {
                    // Nothing loaded but the queue was filled (GitHub #92: songs added to
                    // the queue opened on an empty player) — play it, don't replace it
                    // with a library shuffle.
                    AdvanceQueue(QueueAdvanceReason.UserSkip);
                }
                else if (_library.Tracks.Count > 0)
                {
                    // No track loaded — shuffle entire library.
                    var allTracks = Helpers.ShuffleHelper.WeightedShuffle(
                        _library.Tracks, recentlyPlayed: _recentlyPlayed, allowExplicit: _allowExplicitContent);
                    ReplaceQueueAndPlay(allTracks, 0);
                    // Set AFTER the call: ReplaceQueueAndPlay clears the flag (a new queue
                    // isn't shuffled by definition), so setting it first left the queue
                    // shuffled while every shuffle indicator — the mini player, the
                    // command palette, the MPRIS Shuffle property — reported Off.
                    IsShuffleEnabled = true;
                }
                break;
        }
    }

    /// <summary>
    /// Pause-only, for app shutdown. The shutdown saves run for up to a few seconds
    /// after the window is gone, and the audio engine kept rendering through them —
    /// the track audibly played on after the app "closed". Not PlayPause(): that
    /// toggles, so an already-paused player would resume mid-shutdown. Pause (never
    /// Stop) so CurrentTrack and Position survive into the queue snapshot.
    /// </summary>
    public void PauseForShutdown()
    {
        if (State != PlaybackState.Playing) return;
        CancelAutoMixTransition("shutdown");
        _audioPlayer.Pause();
        State = PlaybackState.Paused;
    }

    [RelayCommand]
    private void Next()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "Next", $"queueLen={UpNext.Count}");
        CancelAutoMixTransition("user skipped");

        // The repeat-all wrap lives inside AdvanceQueueCore, so returning here on an
        // empty UpNext made it unreachable from a user skip: with Repeat All on and the
        // last track playing, Next / Ctrl+Right / the tray item / SMTC-MPRIS next were
        // all silent no-ops, and only a natural track end wrapped.
        var canWrap = RepeatMode == RepeatMode.All &&
                      (_repeatCycleTracks.Count > 0 || History.Count > 0);
        if (UpNext.Count == 0 && !canWrap) return;

        // A user skip before the halfway point counts as a skip in the play log.
        if (CurrentTrack != null && PositionFraction < 0.5)
            _playHistory?.RecordSkip(CurrentTrack);

        AdvanceQueue(QueueAdvanceReason.UserSkip);
    }

    [RelayCommand]
    private void Previous()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "Previous", $"pos={Position.TotalSeconds:F1}s, historyLen={History.Count}");
        if (Position.TotalSeconds > 3)
        {
            // Restart current track
            CancelAutoMixTransition("user skipped");
            if (!DeferSeekWhileStopped(TimeSpan.Zero))
                _audioPlayer.Seek(TimeSpan.Zero);
            _lastSeekTime = DateTime.UtcNow;
            _lastCommittedSeekTarget = TimeSpan.Zero;
            Position = TimeSpan.Zero;
            PositionFraction = 0;
            PositionText = "0:00";
            RemainingTimeText = FormatTime(Duration);
            Seeked?.Invoke(this, TimeSpan.Zero);
        }
        else if (Math.Min(_queueHistoryDepth, History.Count) > 0)
        {
            // Undo the Next / natural advances made inside this queue before touching the
            // pre-start tracks: started at 3, Next to 4, Previous must return to 3 — it went
            // to 2 because the pre-start branch below was consulted first (Discord, aaron
            // 2026-09-23). Entries deeper than _queueHistoryDepth predate this queue.
            CancelAutoMixTransition("user skipped");
            GoBackInQueue(QueueAdvanceReason.Previous);
        }
        else if (_precedingInQueue.Count > 0)
        {
            // Step back into the part of the queue that was started past (GitHub #74).
            CancelAutoMixTransition("user skipped");
            MarkQueueChanged();
            if (CurrentTrack != null) UpNext.Insert(0, CurrentTrack);
            var prev = _precedingInQueue[^1];
            _precedingInQueue.RemoveAt(_precedingInQueue.Count - 1);
            PlayTrack(prev);
        }
        else if (History.Count > 0)
        {
            CancelAutoMixTransition("user skipped");
            GoBackInQueue(QueueAdvanceReason.Previous);
        }
    }

    /// <summary>Queue entries before the one the user started from, newest-first is the
    /// END of the list; consumed by <see cref="Previous"/> before <see cref="History"/>.</summary>
    private readonly List<Track> _precedingInQueue = new();

    /// <summary>How many leading <see cref="History"/> entries were pushed by advances inside
    /// the current queue (Next / natural end). <see cref="Previous"/> walks these back before
    /// it steps into <see cref="_precedingInQueue"/>; anything deeper played before the queue
    /// was started and is only reached once its first track is passed (GitHub #74).</summary>
    private int _queueHistoryDepth;

    /// <summary>Island speed menu; parameter is a percent ("75" … "200").</summary>
    [RelayCommand]
    private void SetPlaybackRate(string? percent)
    {
        if (int.TryParse(percent, out var p) && p is >= 50 and <= 200)
            PlaybackRatePercent = p;
    }

    [RelayCommand]
    private void SkipBack() => SeekBy(TimeSpan.FromSeconds(-IslandSkipSeconds));

    [RelayCommand]
    private void SkipForward() => SeekBy(TimeSpan.FromSeconds(IslandSkipSeconds));

    /// <summary>Relative seek, clamped to the track; rides the absolute seek so the
    /// AutoMix cancel, seek bookkeeping and Seeked event all stay in one place.</summary>
    internal void SeekBy(TimeSpan delta)
    {
        if (CurrentTrack == null || Duration <= TimeSpan.Zero) return;
        var target = Position + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > Duration) target = Duration;
        SeekToPosition(target.Ticks / (double)Duration.Ticks);
    }

    /// <summary>Absolute seek, clamped to the track; rides the fraction seek so its bookkeeping stays in one place.</summary>
    internal void SeekTo(TimeSpan target)
    {
        if (CurrentTrack == null || Duration <= TimeSpan.Zero) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > Duration) target = Duration;
        SeekToPosition(target.Ticks / (double)Duration.Ticks);
    }

    [RelayCommand]
    private void SeekToPosition(double fraction)
    {
        if (CurrentTrack == null || Duration == TimeSpan.Zero) return;

        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var target = TimeSpan.FromTicks((long)(Duration.Ticks * fraction));
        var remaining = Duration - target;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        Position = target;
        PositionText = FormatTime(target);
        PositionFraction = fraction;
        RemainingTimeText = FormatTime(remaining);
        CancelAutoMixTransition("user seeked");
        DebugLogger.Info(DebugLogger.Category.Playback, "SeekToPosition",
            $"targetMs={target.TotalMilliseconds:F0}, state={State}, track={CurrentTrack.Id}");
        if (!DeferSeekWhileStopped(target))
            _audioPlayer.Seek(target);
        _lastSeekTime = DateTime.UtcNow;
        _lastCommittedSeekTarget = target;
        Seeked?.Invoke(this, target);
    }

    /// <summary>
    /// A seek on a Stopped track (restored session, or halted by stop-after-current) has
    /// no playing media to move: the player dropped it (nothing loaded yet, so Play then
    /// jumped back to the stale restored position) or restarted the ended media behind a
    /// Stopped UI. Keep it as the one-shot resume target PlayTrack applies on Play instead.
    /// </summary>
    private bool DeferSeekWhileStopped(TimeSpan target)
    {
        if (State != PlaybackState.Stopped || CurrentTrack == null) return false;
        _resumePositionMs = target > TimeSpan.Zero ? (long)target.TotalMilliseconds : -1;
        _resumeTrackId = CurrentTrack.Id;
        DebugLogger.Info(DebugLogger.Category.Playback, "Seek.Deferred",
            $"reason=stopped, targetMs={target.TotalMilliseconds:F0}, track={CurrentTrack.Id}");
        return true;
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _audioPlayer.IsMuted = IsMuted;
        DebugLogger.Info(DebugLogger.Category.Playback, "Mute", $"muted={IsMuted}, source=toggle");
    }

    // ── Sleep timer ──────────────────────────────────────────

    private DispatcherTimer? _sleepTimer;
    private DateTime _sleepTimerEndsAtUtc;

    /// <summary>True while a timed sleep timer is counting down.</summary>
    [ObservableProperty] private bool _isSleepTimerActive;

    /// <summary>Remaining time label, e.g. "29:54". Empty when inactive.</summary>
    [ObservableProperty] private string _sleepTimerRemainingText = string.Empty;

    /// <summary>Active timed-sleep duration in minutes (15/30/45/60); 0 when no timed
    /// sleep timer is running. Drives the checkmark on the selected menu option.</summary>
    [ObservableProperty] private int _sleepTimerMinutes;

    /// <summary>When true, playback stops after the current track finishes (also the
    /// sleep timer's "end of track" mode). One-shot: clears itself once it fires.</summary>
    [ObservableProperty] private bool _stopAfterCurrentTrack;

    /// <summary>Starts a timed sleep timer; parameter is minutes ("15"/"30"/"45"/"60").</summary>
    [RelayCommand]
    private void StartSleepTimer(string? minutes)
    {
        if (!int.TryParse(minutes, out var mins) || mins <= 0) return;

        _sleepTimerEndsAtUtc = DateTime.UtcNow.AddMinutes(mins);
        _sleepTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick -= OnSleepTimerTick;
        _sleepTimer.Tick += OnSleepTimerTick;
        _sleepTimer.Start();
        IsSleepTimerActive = true;
        SleepTimerMinutes = mins;
        UpdateSleepTimerRemaining();
        DebugLogger.Info(DebugLogger.Category.Playback, "SleepTimer.Start", $"minutes={mins}");
    }

    [RelayCommand]
    private void CancelSleepTimer()
    {
        _sleepTimer?.Stop();
        IsSleepTimerActive = false;
        SleepTimerMinutes = 0;
        SleepTimerRemainingText = string.Empty;
        DebugLogger.Info(DebugLogger.Category.Playback, "SleepTimer.Cancel", null);
    }

    /// <summary>Toggles "stop after current track" (the sleep timer's end-of-track mode).</summary>
    [RelayCommand]
    private void ToggleStopAfterCurrent()
    {
        StopAfterCurrentTrack = !StopAfterCurrentTrack;
        DebugLogger.Info(DebugLogger.Category.Playback, "SleepTimer.StopAfterCurrent", $"enabled={StopAfterCurrentTrack}");
    }

    private void OnSleepTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _sleepTimerEndsAtUtc)
        {
            CancelSleepTimer();
            if (State == PlaybackState.Playing)
                PlayPause();
            DebugLogger.Info(DebugLogger.Category.Playback, "SleepTimer.Fired", "paused playback");
            return;
        }
        UpdateSleepTimerRemaining();
    }

    private void UpdateSleepTimerRemaining()
    {
        var remaining = _sleepTimerEndsAtUtc - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        SleepTimerRemainingText = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes}:{remaining.Seconds:00}";
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        CancelAutoMixTransition("shuffle changed");
        IsShuffleEnabled = !IsShuffleEnabled;
        MarkQueueChanged();
        DebugLogger.Info(DebugLogger.Category.Queue, "ToggleShuffle", $"enabled={IsShuffleEnabled}, queueLen={UpNext.Count}");

        // Suppress HasContent notifications during batch queue update to prevent
        // rapid layout invalidation that causes visual shifts in the lyrics view.
        _suppressHasContentNotify = true;
        try
        {
            if (IsShuffleEnabled)
            {
                // Save original queue order
                _originalQueue = UpNext.ToList();
                // Shuffle the queue, respecting SkipWhenShuffling and
                // down-weighting "not liked" + recently-played tracks.
                // Explicit tracks are NOT filtered here: with the filter off they are parked
                // out of the new order by PruneBlockedExplicit below, so turning the filter
                // back on can return them (WeightedShuffle dropping them would lose them).
                var shuffled = Helpers.ShuffleHelper.WeightedShuffle(
                    UpNext.Where(t => !t.SkipWhenShuffling), recentlyPlayed: _recentlyPlayed);
                UpNext.ReplaceAll(shuffled);
                PruneBlockedExplicit(wholeQueue: true);
            }
            else if (_originalQueue.Count > 0)
            {
                // Restore original queue order — but only tracks still pending.
                // Tracks that played (or were removed) while shuffled are gone
                // from UpNext; re-injecting the full snapshot would replay them.
                var pending = new Dictionary<Guid, int>();
                foreach (var t in UpNext)
                    pending[t.Id] = pending.TryGetValue(t.Id, out var n) ? n + 1 : 1;
                var restored = new List<Track>(UpNext.Count);
                foreach (var t in _originalQueue)
                {
                    // Tracks the shuffle FILTERED OUT (rather than consumed) must always
                    // come back: they were never played, they're just absent from UpNext.
                    // SkipWhenShuffling was handled; snoozed tracks were not — WeightedShuffle
                    // drops those too, so they satisfied neither branch and a shuffle
                    // on/off round trip silently deleted them from the queue. Blocked
                    // explicit tracks are NOT restored here: they sit in _parkedExplicit
                    // and only the Explicit Content switch brings them back.
                    if (IsBlockedExplicit(t))
                        continue;
                    if (t.SkipWhenShuffling || t.IsSnoozed)
                    {
                        restored.Add(t);
                        continue;
                    }
                    if (pending.TryGetValue(t.Id, out var n) && n > 0)
                    {
                        pending[t.Id] = n - 1;
                        restored.Add(t);
                    }
                }
                UpNext.ReplaceAll(restored);
                _originalQueue.Clear();
            }
        }
        finally
        {
            _suppressHasContentNotify = false;
            OnPropertyChanged(nameof(HasContent));
        }
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "CycleRepeat", $"from={RepeatMode}");
        CancelAutoMixTransition("repeat changed");
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
    }

    /// <summary>Sets the SettingsViewModel for per-track EQ and audio overrides.</summary>
    public void SetSettingsViewModel(SettingsViewModel settings)
    {
        _settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.EqualizerEnabled))
                OnPropertyChanged(nameof(IsEqualizerEnabled));
        };
        OnPropertyChanged(nameof(IsEqualizerEnabled));
    }

    /// <summary>GitHub #94: the EQ master switch (Settings → Audio), for the island's EQ button.</summary>
    public bool IsEqualizerEnabled => _settings?.EqualizerEnabled ?? false;

    [RelayCommand]
    private void ToggleEqualizer()
    {
        if (_settings != null) _settings.EqualizerEnabled = !_settings.EqualizerEnabled;
    }

    /// <summary>Commits a finished playback-bar resize (drag release or grip
    /// double-click reset): updates the live width and persists it through the
    /// settings pipeline. No-ops the persistence when no settings VM is wired
    /// (headless tests), leaving the property itself fully usable.</summary>
    public void CommitPlaybackBarWidth(double width)
    {
        PlaybackBarIslandWidth = width;
        _settings?.SetPlaybackBarWidth(width);
    }

    /// <summary>Sets the play history log used for play/skip event recording.</summary>
    public void SetPlayHistory(IPlayHistoryService playHistory) => _playHistory = playHistory;

    /// <summary>Sets the navigation action for the lyrics view.</summary>
    public void SetNavigateAction(Action<string> navigateAction)
    {
        _navigateAction = navigateAction;
    }

    [RelayCommand]
    private void ShowLyrics()
    {
        _navigateAction?.Invoke("lyrics");
    }

    [RelayCommand]
    private void ShowQueue()
    {
        IsQueuePopupOpen = !IsQueuePopupOpen;
    }

    /// <summary>Sets the view album action (injected from MainWindowViewModel).</summary>
    public void SetViewAlbumAction(Action<Track> viewAlbumAction)
    {
        _viewAlbumAction = viewAlbumAction;
    }

    /// <summary>Sets the action to search lyrics for a track.</summary>
    public void SetSearchLyricsAction(Action<Track> action) => _searchLyricsAction = action;

    [RelayCommand]
    private void SearchCurrentTrackLyrics()
    {
        if (CurrentTrack != null)
            _searchLyricsAction?.Invoke(CurrentTrack);
    }

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void ViewArtist(Track? track)
    {
        var artist = track?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    /// <summary>One name out of a multi-artist credit — the island resolves the name under
    /// the pointer (see <see cref="Helpers.ArtistCreditSpans"/>); <see cref="ViewArtist"/> with
    /// the whole credit opens the primary artist only.</summary>
    [RelayCommand]
    private void ViewArtistNamed(string? artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
            _viewArtistAction?.Invoke(artistName);
    }

    [RelayCommand]
    private void SetLyricsSynced() => _selectLyricsSynced?.Invoke();

    [RelayCommand]
    private void SetLyricsPlain() => _selectLyricsPlain?.Invoke();

    [RelayCommand]
    private void OpenLyricsBackgroundColor() => _openLyricsBackgroundColor?.Invoke();

    [RelayCommand]
    private void RemoveCurrentTrackLyrics() => _removeLyrics?.Invoke();

    [RelayCommand]
    private void ShareCurrentTrackLyrics() => _shareLyrics?.Invoke();

    /// <summary>
    /// Called by MainWindowViewModel when the lyrics view becomes the current view.
    /// Wires the three pass-through commands and seeds the active-state flags.
    /// </summary>
    public void SetLyricsPageActions(
        Action selectSynced,
        Action selectPlain,
        Action openBackgroundColor,
        Action removeLyrics,
        Action shareLyrics,
        bool isSyncedActive,
        bool isPlainActive,
        bool isSyncedAvailable,
        bool canShare)
    {
        _selectLyricsSynced = selectSynced;
        _selectLyricsPlain = selectPlain;
        _openLyricsBackgroundColor = openBackgroundColor;
        _removeLyrics = removeLyrics;
        _shareLyrics = shareLyrics;
        IsLyricsSyncedActive = isSyncedActive;
        IsLyricsPlainActive = isPlainActive;
        IsLyricsSyncedAvailable = isSyncedAvailable;
        CanShareLyrics = canShare;
        IsLyricsPageActive = true;
    }

    /// <summary>
    /// Called by MainWindowViewModel when navigating away from the lyrics view.
    /// </summary>
    public void ClearLyricsPageActions()
    {
        _selectLyricsSynced = null;
        _selectLyricsPlain = null;
        _openLyricsBackgroundColor = null;
        _removeLyrics = null;
        _shareLyrics = null;
        IsLyricsPageActive = false;
        IsLyricsSyncedActive = false;
        IsLyricsPlainActive = false;
        IsLyricsSyncedAvailable = false;
        CanShareLyrics = false;
    }

    /// <summary>
    /// Called by MainWindowViewModel whenever the lyrics view's Synced/Plain selection
    /// or synced-availability changes, to keep the menu's checkmarks accurate.
    /// </summary>
    public void UpdateLyricsPageState(bool isSyncedActive, bool isPlainActive, bool isSyncedAvailable, bool canShare)
    {
        IsLyricsSyncedActive = isSyncedActive;
        IsLyricsPlainActive = isPlainActive;
        IsLyricsSyncedAvailable = isSyncedAvailable;
        CanShareLyrics = canShare;
    }

    /// <summary>Sets the sidebar ViewModel for playlist access.</summary>
    public void SetSidebar(SidebarViewModel sidebar)
    {
        _sidebar = sidebar;
    }

    /// <summary>Exposes playlists for the Add to Playlist submenu.</summary>
    public ObservableCollection<Playlist>? Playlists => _sidebar?.Playlists;

    [RelayCommand]
    private void PlayNextCurrentTrack()
    {
        if (CurrentTrack == null) return;
        AddNext(CurrentTrack);
    }

    [RelayCommand]
    private void AddCurrentTrackToQueue()
    {
        if (CurrentTrack == null) return;
        AddToQueue(CurrentTrack);
    }

    [RelayCommand]
    private void ShuffleCurrentAlbum()
    {
        if (CurrentTrack == null) return;
        var album = _library.GetAlbumById(CurrentTrack.AlbumId);
        if (album?.Tracks == null || album.Tracks.Count == 0) return;
        // No recency weighting here: the user explicitly chose to shuffle this one album,
        // so every track on it should stay equally likely even if just played.
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(album.Tracks, allowExplicit: _allowExplicitContent);
        if (shuffled.Count == 0) return; // every track on the album is blocked explicit
        ReplaceQueueAndPlay(shuffled, 0);
    }

    /// <summary>
    /// Starts an endless "Track Radio" seeded from <paramref name="seed"/>: builds a
    /// queue of similar tracks from the user's own library and keeps refilling it as
    /// it drains (see the radio refill in <see cref="AdvanceQueueCore"/>).
    /// </summary>
    [RelayCommand]
    private void StartRadio(Track? seed)
    {
        if (seed == null) return;
        var exclude = new HashSet<Guid> { seed.Id };
        var batch = _radioService.BuildSimilar(seed, PlayableLibraryTracks(), RadioBatchSize, exclude);
        var queue = new List<Track> { seed };
        queue.AddRange(batch);
        // ReplaceQueueAndPlay clears IsRadioActive/_radioSeed; re-arm radio afterward.
        ReplaceQueueAndPlay(queue, 0);
        _radioSeed = seed;
        IsRadioActive = true;
        IsShuffleEnabled = false;
    }

    /// <summary>
    /// Hides a track from shuffle and radio for 30 days (Apple Music-style "suggest less"
    /// but temporary). Reversible from Settings. App-only state — no file tag is written.
    /// </summary>
    private const int SnoozeDurationDays = 30;

    [RelayCommand]
    private async Task SnoozeForMonth(Track? track)
    {
        if (track == null) return;
        await _library.SetTracksSnoozedAsync(new[] { track }, DateTime.UtcNow.AddDays(SnoozeDurationDays));
    }

    [RelayCommand]
    private async Task ToggleCurrentTrackFavorite()
    {
        if (CurrentTrack == null) return;
        var newState = !CurrentTrack.IsFavorite;
        CurrentTrack.IsFavorite = newState;
        // The queue can hold pre-reload Track instances; write through to the
        // library's instance so the change actually persists (SaveAsync saves
        // library tracks only) and every library-bound view agrees.
        var libraryTrack = _library.GetTrackById(CurrentTrack.Id);
        if (libraryTrack != null && !ReferenceEquals(libraryTrack, CurrentTrack))
            libraryTrack.IsFavorite = newState;
        await _library.SaveTrackUserStateAsync(new[] { libraryTrack ?? CurrentTrack });
        _library.NotifyFavoritesChanged(new[] { CurrentTrack });
    }

    /// <summary>Mirrors library favorite state onto queue/history Track instances
    /// that are no longer the library's objects (left behind by a library reload),
    /// so queue-driven UI (Cover Flow/Collage, player bar) matches the library.</summary>
    private void SyncQueueFavoritesFromLibrary()
    {
        void Sync(Track? track)
        {
            if (track == null) return;
            var libraryTrack = _library.GetTrackById(track.Id);
            if (libraryTrack != null && !ReferenceEquals(libraryTrack, track) &&
                track.IsFavorite != libraryTrack.IsFavorite)
                track.IsFavorite = libraryTrack.IsFavorite;
        }

        Sync(CurrentTrack);
        foreach (var track in UpNext) Sync(track);
        foreach (var track in History) Sync(track);
    }

    // GitHub #82: a dropped file played from outside the library has no album page, so
    // the island title (and the "…" menu item) must not offer one.
    private bool CanViewCurrentTrackAlbum() =>
        CurrentTrack is { } t && _library.GetAlbumById(t.AlbumId) != null;

    [RelayCommand(CanExecute = nameof(CanViewCurrentTrackAlbum))]
    private void ViewCurrentTrackAlbum()
    {
        var track = CurrentTrack;
        if (track == null) return;

        Dispatcher.UIThread.Post(
            () => _viewAlbumAction?.Invoke(track),
            DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task OpenCurrentTrackMetadata()
    {
        if (CurrentTrack == null) return;
        await MetadataHelper.OpenMetadataWindow(CurrentTrack);
    }

    [RelayCommand]
    private async Task AddCurrentTrackToNewPlaylist()
    {
        if (CurrentTrack == null || _sidebar == null) return;
        await _sidebar.CreatePlaylistWithTrackAsync(CurrentTrack);
    }

    [RelayCommand]
    private async Task AddCurrentTrackToExistingPlaylist(Playlist playlist)
    {
        if (CurrentTrack == null || _sidebar == null || playlist == null) return;
        await _sidebar.AddTracksToPlaylist(playlist.Id, new[] { CurrentTrack });
    }

    [RelayCommand]
    private void ShowCurrentTrackInExplorer()
    {
        if (CurrentTrack == null || !File.Exists(CurrentTrack.FilePath)) return;
        Helpers.PlatformHelper.ShowInFileManager(CurrentTrack.FilePath);
    }

    [RelayCommand]
    private async Task RemoveCurrentTrackFromLibrary()
    {
        if (CurrentTrack == null) return;
        var trackToRemove = CurrentTrack;

        var choice = await Views.RemoveFromLibraryDialog.ShowAsync(1);
        if (choice == Views.RemoveFromLibraryChoice.Cancel)
            return;

        // Advance to next track or stop playback
        if (UpNext.Count > 0)
            AdvanceQueue(QueueAdvanceReason.UserSkip);
        else
        {
            CancelAutoMixTransition("track removed");
            _audioPlayer.Stop();
            State = PlaybackState.Stopped;
            CurrentTrack = null;
            AlbumArt = null;
            CurrentAnimatedCoverPath = null;
        }

        if (choice == Views.RemoveFromLibraryChoice.Trash)
            await Helpers.LibraryRemovalHelper.TrashLocalFilesAsync(new[] { trackToRemove });
        await _library.RemoveTrackAsync(trackToRemove.Id);
    }

    // ── Public methods for queue management ───────────────────

    /// <summary>
    /// Replaces the entire queue and starts playing from the given index.
    /// Called when the user double-clicks a track in any library view.
    /// </summary>
    public void ReplaceQueueAndPlay(IList<Track> tracks, int startIndex)
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "ReplaceQueueAndPlay", $"tracks={tracks.Count}, startIdx={startIndex}");
        if (tracks.Count == 0) return;
        if (startIndex < 0 || startIndex >= tracks.Count) startIndex = 0;
        // Any non-radio queue replacement (album/playlist/shuffle/etc.) ends radio so
        // it stops refilling. StartRadio re-enables IsRadioActive after calling this.
        IsRadioActive = false;
        _radioSeed = null;
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();

        // Move current to history if playing
        if (CurrentTrack != null)
        {
            History.Insert(0, CurrentTrack);
            TrimHistory();
        }
        _queueHistoryDepth = 0; // that entry belongs to what played before this queue

        // Clear stale shuffle state — a new queue replaces whatever was shuffled.
        _originalQueue.Clear();
        _parkedExplicit.Clear(); // parked explicit tracks belonged to the old queue
        IsShuffleEnabled = false;

        // Record the full cycle for Repeat All, starting at the track being played so a
        // wrap replays the queue in the order the user actually started it.
        _repeatCycleTracks = tracks.Skip(startIndex).Concat(tracks.Take(startIndex)).ToList();

        // GitHub #74: the tracks BEFORE the start point are what Previous should step back
        // through (playlist started at track 5 → Previous plays track 4), not the last
        // thing that happened to play before this queue. Kept apart from History, which
        // the Queue panel and Cover Flow show as songs actually played.
        _precedingInQueue.Clear();
        for (int i = 0; i < startIndex; i++)
            _precedingInQueue.Add(tracks[i]);

        // Clear and rebuild the queue
        var upNextTracks = new List<Track>(tracks.Count - startIndex - 1);
        for (int i = startIndex + 1; i < tracks.Count; i++)
            upNextTracks.Add(tracks[i]);
        UpNext.ReplaceAll(upNextTracks);

        // Play the selected track
        PlayTrack(tracks[startIndex]);
    }

    /// <summary>Inserts a track at the front of the UpNext queue ("Play Next").</summary>
    public void AddNext(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "AddNext", $"track={track.Title}");
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        UpNext.Insert(0, track);
    }

    /// <summary>Appends a track to the end of the UpNext queue ("Add to Queue").</summary>
    public void AddToQueue(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "AddToQueue", $"track={track.Title}, newLen={UpNext.Count + 1}");
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        UpNext.Add(track);
    }

    /// <summary>Appends multiple tracks to the end of the UpNext queue in a single batch.</summary>
    public void AddRangeToQueue(IList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        DebugLogger.Info(DebugLogger.Category.Queue, "AddRangeToQueue", $"count={tracks.Count}, newLen={UpNext.Count + tracks.Count}");
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        _suppressHasContentNotify = true;
        try { UpNext.AddRange(tracks); }
        finally
        {
            _suppressHasContentNotify = false;
            OnPropertyChanged(nameof(HasContent));
        }
    }

    /// <summary>Removes a track from the UpNext queue by index.</summary>
    public void RemoveFromQueue(int index)
    {
        if (index >= 0 && index < UpNext.Count)
        {
            DebugLogger.Info(DebugLogger.Category.Queue, "RemoveFromQueue", $"idx={index}, track={UpNext[index].Title}");
            CancelAutoMixTransition("queue changed");
            MarkQueueChanged();
            UpNext.RemoveAt(index);
        }
    }

    /// <summary>Clears all upcoming tracks.</summary>
    public void ClearQueue()
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "ClearQueue", $"cleared={UpNext.Count} tracks");
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        UpNext.Clear();
        _parkedExplicit.Clear();
    }

    /// <summary>Stops playback and clears all queue data.</summary>
    /// <param name="reason">Why the queue is being wiped; recorded in the log.</param>
    public void StopAndClear(string reason = "unspecified")
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "StopAndClear",
            $"reason={reason}, track={CurrentTrack?.Id}, upNext={UpNext.Count}, history={History.Count}");
        if (CurrentTrack?.RememberPlaybackPosition == true)
        {
            CurrentTrack.SavedPositionMs = (long)Position.TotalMilliseconds;
            MarkPlayStateDirty(CurrentTrack);
        }
        _hasPendingSeekTarget = false;
        CancelAutoMixTransition("player stopped");
        CancelNaturalEndFallback();
        MarkQueueChanged();
        _audioPlayer.Stop();
        State = PlaybackState.Stopped;
        CurrentTrack = null;
        UpNext.Clear();
        History.Clear();
        _queueHistoryDepth = 0;
        _precedingInQueue.Clear();
        _originalQueue.Clear();
        _parkedExplicit.Clear();
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        PositionFraction = 0;
        PositionText = "0:00";
        DurationText = "0:00";
        RemainingTimeText = "0:00";
        // AlbumArt bitmaps are owned by the shared ArtworkCache — drop the reference,
        // don't dispose (other UI surfaces and the cache may still hold it).
        AlbumArt = null;
        // The island draws the path, not the bitmap: songs queued onto the emptied player
        // (GitHub #92) showed the last song's cover over a blank title (09-24).
        CurrentArtPath = null;
        // Stop the animated cover with playback — otherwise the loop keeps
        // playing over the "No track playing" state after the queue drains.
        CurrentAnimatedCoverPath = null;
    }

    /// <summary>Reorders a track in the UpNext queue via drag & drop.</summary>
    public void MoveInQueue(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= UpNext.Count) return;
        if (toIndex < 0 || toIndex >= UpNext.Count) return;

        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        var track = UpNext[fromIndex];
        UpNext.RemoveAt(fromIndex);
        UpNext.Insert(toIndex, track);
    }

    /// <summary>
    /// GitHub #85: removes the UpNext rows at <paramref name="indices"/> (any order). Rows,
    /// not tracks: the same track queued twice is two rows, removed only where selected.
    /// </summary>
    public void RemoveManyFromQueue(IEnumerable<int> indices)
    {
        var rows = indices.Where(i => i >= 0 && i < UpNext.Count).Distinct().OrderByDescending(i => i).ToList();
        if (rows.Count == 0) return;

        DebugLogger.Info(DebugLogger.Category.Queue, "RemoveManyFromQueue", $"count={rows.Count}");
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        // High → low so the lower indices stay valid.
        foreach (var i in rows)
            UpNext.RemoveAt(i);
    }

    /// <summary>
    /// GitHub #85: moves the UpNext rows at <paramref name="indices"/> as one block to
    /// <paramref name="insertIndex"/>, an insertion index in the CURRENT queue (before the
    /// block is lifted out) — the same contract as PlaylistViewModel.ReorderBlock, but by
    /// row so a duplicated track moves only where it is selected. The block keeps its queue
    /// order. Returns the block's first row after the move, or -1 when nothing moved.
    /// </summary>
    public int MoveBlockInQueue(IReadOnlyCollection<int> indices, int insertIndex)
    {
        var rows = indices.Where(i => i >= 0 && i < UpNext.Count).Distinct().Order().ToList();
        if (rows.Count == 0) return -1;
        insertIndex = Math.Clamp(insertIndex, 0, UpNext.Count);

        // Where the block lands once lifted out: the non-block rows before insertIndex.
        var landAt = insertIndex - rows.Count(i => i < insertIndex);
        var contiguous = rows[^1] - rows[0] == rows.Count - 1;
        if (contiguous && landAt == rows[0]) return -1;

        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        // Remove + Insert per block row (as MoveInQueue does) rather than ReplaceAll, so the
        // virtualized list keeps its realized rows, and the notification count scales with
        // the block, not with the queue.
        var block = rows.Select(i => UpNext[i]).ToList();
        for (var k = rows.Count - 1; k >= 0; k--)
            UpNext.RemoveAt(rows[k]);
        for (var k = 0; k < block.Count; k++)
            UpNext.Insert(landAt + k, block[k]);
        return landAt;
    }

    /// <summary>Plays the track at the given index in UpNext, discarding prior queue items.</summary>
    public void PlayFromUpNextAt(int index)
    {
        if (index < 0 || index >= UpNext.Count) return;
        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        var remaining = UpNext.Skip(index).ToList();
        ReplaceQueueAndPlay(remaining, 0);
    }

    /// <summary>
    /// Plays the track <paramref name="index"/> steps back in History (0 = most recent).
    /// The current track and every history entry skipped over go to the FRONT of UpNext
    /// in their original order, so after the jump the queue replays them in sequence —
    /// the same bookkeeping as <see cref="GoBackInQueue"/>, applied N deep at once
    /// (Cover Flow: clicking the −2 card).
    /// </summary>
    public void PlayFromHistoryAt(int index)
    {
        if (index < 0 || index >= History.Count) return;
        DebugLogger.Info(DebugLogger.Category.Playback, "PlayFromHistoryAt", $"index={index}, historyLen={History.Count}");
        CancelAutoMixTransition("user skipped");
        MarkQueueChanged();

        if (CurrentTrack != null)
            UpNext.Insert(0, CurrentTrack);
        // History[0] is the most recent, so it plays soonest after the target: insert in
        // order at 0,1,2… → [h0, h1, …, current, rest].
        for (var i = 0; i < index; i++)
            UpNext.Insert(i, History[i]);

        var target = History[index];
        for (var i = index; i >= 0; i--)
            History.RemoveAt(i);
        _queueHistoryDepth = Math.Max(0, _queueHistoryDepth - (index + 1));
        PlayTrack(target);
    }

    /// <summary>
    /// Saves the current queue (now playing + up next) as a playlist via the
    /// unified Add to Playlist dialog.
    /// </summary>
    [RelayCommand]
    private async Task SaveQueueAsPlaylist()
    {
        if (_sidebar == null) return;
        var tracks = new List<Track>();
        if (CurrentTrack != null) tracks.Add(CurrentTrack);
        tracks.AddRange(UpNext);
        if (tracks.Count == 0) return;
        await _sidebar.OpenAddToPlaylistAsync(tracks);
    }

    // ── Queue state persistence ──────────────────────────────

    // One-shot resume target restored from the previous session: pressing Play
    // on the restored (stopped) track resumes where the user left off.
    private long _resumePositionMs = -1;
    private Guid _resumeTrackId;

    // Serializes background queue snapshots so rapid track changes can't race
    // the temp-file rename in PersistenceService (latest snapshot wins).
    private readonly object _queueSaveLock = new();
    private Task _queueSaveChain = Task.CompletedTask;

    /// <summary>Saves the current queue state so it can be restored on next launch.</summary>
    public async Task SaveQueueStateAsync()
    {
        var state = new QueueState
        {
            CurrentTrackId = CurrentTrack?.Id,
            PositionSeconds = Position.TotalSeconds,
            UpNextIds = UpNext.Select(t => t.Id).ToList(),
            HistoryIds = History.Select(t => t.Id).ToList(),
            RepeatMode = RepeatMode,
            IsShuffleEnabled = IsShuffleEnabled,
            IsMuted = IsMuted,
            RepeatCycleIds = _repeatCycleTracks.Select(t => t.Id).ToList(),
            ExternalTrackPaths = BuildExternalTrackPaths()
        };
        await _persistence.SaveQueueStateAsync(state);
    }

    /// <summary>
    /// GitHub #86: Id → file of every non-library track the snapshot references, so the
    /// next launch can re-read them (their Ids exist only in this session). Null when none.
    /// </summary>
    private Dictionary<Guid, string>? BuildExternalTrackPaths()
    {
        Dictionary<Guid, string>? map = null;
        void Add(Track? t)
        {
            if (t is not { IsExternal: true } || string.IsNullOrEmpty(t.FilePath)) return;
            (map ??= new Dictionary<Guid, string>())[t.Id] = t.FilePath;
        }
        Add(CurrentTrack);
        foreach (var t in UpNext) Add(t);
        foreach (var t in History) Add(t);
        foreach (var t in _repeatCycleTracks) Add(t);
        return map;
    }

    /// <summary>
    /// Fire-and-forget queue snapshot. The queue used to persist only during a
    /// graceful shutdown, so tray + OS-shutdown / task-kill exits lost it and
    /// the next launch had an empty playbar. Called on track change, pause and
    /// close-to-tray so a snapshot always exists.
    /// </summary>
    /// <summary>Flushes the debounced per-play library save immediately (call on shutdown).</summary>
    public async Task FlushPendingLibrarySaveAsync()
    {
        _librarySaveDebounce?.Dispose();
        _librarySaveDebounce = null;
        // Journal first: the journal wins over library.json on load, so it must be
        // at least as new as the full save below.
        await FlushPendingPlayStateAsync();
        // Shutdown still writes one full library.json so the JSON stays a complete,
        // downgrade-safe snapshot (an older app version reads it as before).
        await _library.SaveAsync();
    }

    public void SaveQueueStateInBackground()
    {
        QueueState state;
        try
        {
            state = new QueueState
            {
                CurrentTrackId = CurrentTrack?.Id,
                PositionSeconds = Position.TotalSeconds,
                UpNextIds = UpNext.Select(t => t.Id).ToList(),
                HistoryIds = History.Select(t => t.Id).ToList(),
                RepeatMode = RepeatMode,
                IsShuffleEnabled = IsShuffleEnabled,
                IsMuted = IsMuted,
                RepeatCycleIds = _repeatCycleTracks.Select(t => t.Id).ToList(),
                ExternalTrackPaths = BuildExternalTrackPaths()
            };
        }
        catch
        {
            // Queue mutated mid-snapshot (off-thread caller) — the next event
            // will snapshot again.
            return;
        }

        lock (_queueSaveLock)
        {
            _queueSaveChain = _queueSaveChain
                .ContinueWith(_ => _persistence.SaveQueueStateAsync(state), TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception?.GetBaseException() is { } ex)
                        DebugLog.Write("Queue", $"Queue snapshot failed: {ex.GetType().Name}: {ex.Message}");
                }, TaskScheduler.Default);
        }
    }

    /// <summary>Restores queue state from persistence. Called during app startup.</summary>
    public async Task RestoreQueueStateAsync()
    {
        var state = await _persistence.LoadQueueStateAsync();
        if (state == null) return;

        // GitHub #86: non-library tracks (dropped with "Import dropped files" off) are
        // unknown to the library, so they are re-read from their saved files.
        var external = await ResolveSavedExternalTracksAsync(state);
        Track? Resolve(Guid id) => _library.GetTrackById(id) ?? external.GetValueOrDefault(id);

        // Restore history
        var restoredHistory = new List<Track>();
        foreach (var id in state.HistoryIds)
        {
            var track = Resolve(id);
            if (track != null) restoredHistory.Add(track);
        }
        History.AddRange(restoredHistory);

        // Restore up-next
        var restoredUpNext = new List<Track>();
        foreach (var id in state.UpNextIds)
        {
            var track = Resolve(id);
            if (track != null) restoredUpNext.Add(track);
        }
        UpNext.AddRange(restoredUpNext);

        // Restore the transport modes. These were never persisted, so repeat and shuffle
        // silently reset to Off and the app un-muted on every restart.
        RepeatMode = state.RepeatMode;
        IsShuffleEnabled = state.IsShuffleEnabled;
        if (IsMuted != state.IsMuted)
            DebugLogger.Info(DebugLogger.Category.Playback, "Mute", $"muted={state.IsMuted}, source=restore");
        IsMuted = state.IsMuted;

        var restoredCycle = new List<Track>(state.RepeatCycleIds.Count);
        foreach (var id in state.RepeatCycleIds)
        {
            var track = Resolve(id);
            if (track != null) restoredCycle.Add(track);
        }
        _repeatCycleTracks = restoredCycle;

        // Restore current track (paused, not auto-playing)
        var usedUpNextFallback = false;
        if (state.CurrentTrackId.HasValue)
        {
            var track = Resolve(state.CurrentTrackId.Value);
            var positionSeconds = state.PositionSeconds;
            if (track == null && UpNext.Count > 0)
            {
                // Its file was moved or deleted since (GitHub #91): load the next queued
                // track from its start instead of leaving the island without a track.
                usedUpNextFallback = true;
                track = UpNext[0];
                UpNext.RemoveAt(0);
                positionSeconds = 0;
            }
            if (track != null)
            {
                CurrentTrack = track;
                LoadAlbumArt(track);
                Duration = track.Duration;
                DurationText = FormatTime(track.Duration);
                Position = TimeSpan.FromSeconds(positionSeconds);
                PositionFraction = Duration.TotalSeconds > 0
                    ? positionSeconds / Duration.TotalSeconds
                    : 0;
                PositionText = FormatTime(Position);
                RemainingTimeText = FormatTime(Duration > Position ? Duration - Position : TimeSpan.Zero);
                State = PlaybackState.Stopped; // user must press play

                // Pressing Play on this restored track resumes where the user
                // left off (consumed one-shot inside PlayTrack's seek chain).
                _resumePositionMs = (long)(positionSeconds * 1000);
                _resumeTrackId = track.Id;

                // Ensure UI updates on UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(CurrentTrack));
                    OnPropertyChanged(nameof(HasContent));
                }, DispatcherPriority.Render);
            }
        }

        DebugLogger.Info(DebugLogger.Category.Playback, "Queue.Restored",
            $"current={CurrentTrack?.Id.ToString() ?? (state.CurrentTrackId.HasValue ? "unresolved" : "none")}, " +
            $"savedPosSec={state.PositionSeconds:F1}, resumeMs={Interlocked.Read(ref _resumePositionMs)}, " +
            $"upNext={restoredUpNext.Count}/{state.UpNextIds.Count}, history={restoredHistory.Count}/{state.HistoryIds.Count}, " +
            $"cycle={restoredCycle.Count}/{state.RepeatCycleIds.Count}, upNextFallback={usedUpNextFallback}, " +
            $"shuffle={IsShuffleEnabled}, repeat={RepeatMode}, muted={IsMuted}");
    }

    /// <summary>
    /// Saved non-library tracks by their saved Id. A file that has since joined the library
    /// resolves to the library entry; otherwise it is re-read off the UI thread, keeping the
    /// saved Id so the Id lists (and Home's play log) still point at it. Missing files drop.
    /// </summary>
    private async Task<Dictionary<Guid, Track>> ResolveSavedExternalTracksAsync(QueueState state)
    {
        var result = new Dictionary<Guid, Track>();
        if (state.ExternalTrackPaths is not { Count: > 0 } saved) return result;

        var wanted = saved
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) && _library.GetTrackById(p.Key) == null)
            .ToList();
        if (wanted.Count == 0) return result;

        // Path index snapshot here — _library.Tracks is mutated on the UI thread.
        var wantedPaths = new HashSet<string>(wanted.Select(p => p.Value), StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _library.Tracks)
            if (wantedPaths.Contains(t.FilePath))
                byPath[t.FilePath] = t;

        var metadata = _metadata;
        var persistence = _persistence;
        var resolved = await Task.Run(() =>
        {
            var list = new List<KeyValuePair<Guid, Track>>(wanted.Count);
            foreach (var (id, path) in wanted)
            {
                if (byPath.TryGetValue(path, out var libraryTrack))
                {
                    list.Add(new(id, libraryTrack));
                    continue;
                }
                if (metadata == null) continue;
                try
                {
                    var track = ExternalTrackReader.Read(metadata, persistence, path);
                    if (track == null) continue;
                    track.Id = id;
                    list.Add(new(id, track));
                }
                catch (Exception ex)
                {
                    // Type only: IO/tag exception messages carry the full path.
                    DebugLog.Write("Queue", $"External track restore failed for {Path.GetFileName(path)}: {ex.GetType().Name}");
                }
            }
            return list;
        });

        foreach (var (id, track) in resolved)
            result[id] = track;
        return result;
    }

    /// <summary>
    /// The non-library tracks the player currently holds (current, Up Next, History, repeat
    /// cycle) by Id. Lets Home's Last Played resolve dropped files the library cannot
    /// (GitHub #86) without touching the disk.
    /// </summary>
    public Dictionary<Guid, Track> GetExternalTracksById()
    {
        var map = new Dictionary<Guid, Track>();
        void Add(Track? t)
        {
            if (t is { IsExternal: true }) map.TryAdd(t.Id, t);
        }
        Add(CurrentTrack);
        foreach (var t in UpNext) Add(t);
        foreach (var t in History) Add(t);
        foreach (var t in _repeatCycleTracks) Add(t);
        return map;
    }

    // ── Volume property change handler ───────────────────────

    partial void OnVolumeChanged(int value)
    {
        // Fired on every drag pixel. VlcAudioPlayer feeds the value to its volume
        // ramp engine, which slews the applied gain in small paced steps —
        // real-time yet click-free (raw per-pixel session writes crackle).
        _audioPlayer.Volume = value;
        RefreshSignalPath();
    }

    partial void OnIsMutedChanged(bool value) => RefreshSignalPath();

    /// <summary>
    /// Flush the final volume to VLC immediately — call on slider drag-end
    /// so the exact value is applied without waiting for the trailing timer.
    /// </summary>
    public void CommitVolume() => _audioPlayer.CommitVolume();

    /// <summary>
    /// Unmute when the user actively adjusts volume (slider drag or wheel) —
    /// adjusting while muted means they want to hear the result.
    /// </summary>
    public void UnmuteForAdjust()
    {
        if (!IsMuted) return;
        IsMuted = false;
        _audioPlayer.IsMuted = false;
        DebugLogger.Info(DebugLogger.Category.Playback, "Mute", "muted=False, source=adjust");
    }

    partial void OnCurrentTrackChanged(Track? value)
    {
        OnPropertyChanged(nameof(HasContent));
        ViewCurrentTrackAlbumCommand.NotifyCanExecuteChanged();
        ResolveLyricsBackground();
        ResolveMusicVideo();
        // Re-apply ReplayGain so the new track's RG tags take effect. The
        // player already reads tags at Play() time, but settings or playback
        // path changes can leave us here without a Play() call.
        if (_settings != null)
            _audioPlayer.ApplyReplayGain(_settings.ReplayGainMode, _settings.ReplayGainPreampDb);
        // Nothing playing any more (stop / cleared queue): drop the last track's per-track
        // EQ preset, or switching the EQ back on would re-push it (GitHub #94).
        if (value == null)
            _settings?.ClearTrackEqPresetOverride();

        RefreshSignalPath();
        // The player applies ReplayGain / opens the output on a worker shortly
        // after Play(); refresh once more so applied-gain and output format are
        // accurate for the new track.
        DispatcherTimer.RunOnce(RefreshSignalPath, TimeSpan.FromMilliseconds(800));
    }

    // Keep the shared Track instances' now-playing flag in sync so flat track
    // lists (Folders/Songs) can highlight the current row via a style class.
    partial void OnCurrentTrackChanged(Track? oldValue, Track? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsNowPlaying = false;
            oldValue.IsCurrentlyPlaying = false;
        }
        if (newValue != null) newValue.IsNowPlaying = true;
        SyncNowPlayingFlags();
        DebugLogger.Info(DebugLogger.Category.Playback, "CurrentTrack.Changed",
            $"old={oldValue?.Title ?? "<null>"} new={newValue?.Title ?? "<null>"}");
    }

    partial void OnStateChanged(PlaybackState value) => SyncNowPlayingFlags();

    private Album? _flaggedAlbum;

    /// <summary>
    /// Album/single tiles (Albums grid, Home, Favorites, Artist page) show Pause on the
    /// item that is audibly playing and Play otherwise. The flags live on the shared
    /// model instances: Track.IsCurrentlyPlaying and the loaded track's Album
    /// (IsCurrent = loaded, IsNowPlaying = loaded and playing).
    /// </summary>
    private void SyncNowPlayingFlags()
    {
        var current = CurrentTrack;
        var playing = State == PlaybackState.Playing;
        if (current != null) current.IsCurrentlyPlaying = playing;

        var album = current != null && current.AlbumId != Guid.Empty ? _library.GetAlbumById(current.AlbumId) : null;
        if (_flaggedAlbum != null && !ReferenceEquals(_flaggedAlbum, album))
        {
            _flaggedAlbum.IsCurrent = false;
            _flaggedAlbum.IsNowPlaying = false;
        }
        if (album != null)
        {
            album.IsCurrent = true;
            album.IsNowPlaying = playing;
        }
        _flaggedAlbum = album;
    }

    /// <summary>
    /// Rebuild the quality badge + expanded chain (source → ReplayGain → EQ →
    /// crossfade → output). Called on track changes, output-mode changes and by
    /// Settings whenever an audio toggle is applied.
    /// </summary>
    public void RefreshSignalPath()
    {
        var track = CurrentTrack;
        if (track == null)
        {
            SignalPathStages = Array.Empty<SignalPathStage>();
            SignalPathQuality = "";
            return;
        }

        var rgDb = _audioPlayer.ReplayGainAppliedDb;
        var rgMode = _settings?.ReplayGainMode ?? "Off";
        var rgOn = !string.Equals(rgMode, "Off", StringComparison.OrdinalIgnoreCase);
        // Enabled-but-flat is a true bypass in the player, so the master toggle alone
        // can't drive this: it made the stock "EQ on / Flat preset" install permanently
        // "Enhanced", hiding Lossless and Bit-perfect. Mirrors how rgOn below only
        // counts ReplayGain when it actually applies gain.
        var eqEnabled = _settings?.EqualizerEnabled ?? false;
        var eqOn = _audioPlayer.EqualizerActive;
        var soundCheckOn = _settings?.SoundCheckEnabled ?? false;
        var crossfadeOn = _settings?.CrossfadeEnabled ?? false;
        var exclusive = _audioPlayer.ExclusiveModeActive;
        var volumeUnity = Volume >= 100 && (track.VolumeAdjust == 0) && !IsMuted;

        var codec = !string.IsNullOrEmpty(track.CodecShortName)
            ? track.CodecShortName
            : Path.GetExtension(track.FilePath).TrimStart('.').ToUpperInvariant();
        var sourceDetail = codec;
        if (track.BitsPerSample > 0 && track.SampleRate > 0)
            sourceDetail += $" {track.BitsPerSample}-bit / {track.SampleRate / 1000.0:0.#} kHz";
        else if (track.SampleRate > 0)
            sourceDetail += $" {track.SampleRate / 1000.0:0.#} kHz";
        if (!track.IsLossless && track.Bitrate > 0)
            sourceDetail += $" ({track.Bitrate} kbps)";

        var rgDetail = !rgOn
            ? "Off"
            : Math.Abs(rgDb) > 0.01
                ? $"{rgMode} — {rgDb:+0.0;-0.0} dB"
                : $"{rgMode} — no tags (bypass)";

        // Index 0 is the Custom curve; a user preset rides it under its own name (GitHub #95).
        var eqName = _settings?.SelectedEqPresetName;
        var eqDetail = !eqEnabled
            ? "Off"
            : eqOn
                ? (_settings?.SelectedEqPresetIndex == 0 && (string.IsNullOrEmpty(eqName) || eqName == SettingsViewModel.EqPresetNames[0])
                    ? "Parametric (custom)"
                    : eqName ?? "On")
                : "Flat — bypass";

        var crossfadeDetail = crossfadeOn
            ? $"{_settings?.CrossfadeDuration ?? 6:0.#} s"
            : GaplessEnabled ? "Off (gapless)" : "Off";

        SignalPathStages = new[]
        {
            new SignalPathStage("Source", sourceDetail, true),
            new SignalPathStage("ReplayGain", rgDetail, rgOn),
            new SignalPathStage("Equalizer", eqDetail, eqOn),
            new SignalPathStage("Sound Check", soundCheckOn ? "On (loudness normalization)" : "Off", soundCheckOn),
            new SignalPathStage("Crossfade", crossfadeDetail, crossfadeOn),
            new SignalPathStage("Volume", volumeUnity ? "100% (unity)" : IsMuted ? "Muted" : $"{Volume}%", !volumeUnity),
            new SignalPathStage("Output", _audioPlayer.OutputDescription, true),
        };

        var dspActive = (rgOn && Math.Abs(rgDb) > 0.01) || eqOn || soundCheckOn || crossfadeOn;
        if (exclusive && !dspActive && volumeUnity)
        {
            SignalPathQuality = "Bit-perfect";
            SignalPathColor = "#B197FC"; // violet — untouched samples reach the DAC
        }
        else if (track.IsLossless && !dspActive)
        {
            SignalPathQuality = track.IsHiResLossless ? "Hi-Res Lossless" : "Lossless";
            SignalPathColor = "#4ADE80"; // green
        }
        else if (track.IsLossless)
        {
            SignalPathQuality = "Enhanced";
            SignalPathColor = "#60A5FA"; // blue — lossless source with DSP applied
        }
        else
        {
            SignalPathQuality = dspActive ? "Lossy · Enhanced" : "Lossy";
            SignalPathColor = "#9CA3AF"; // gray
        }
    }

    partial void OnPositionFractionChanged(double value)
    {
        if (!_isSeeking || CurrentTrack == null || Duration <= TimeSpan.Zero)
            return;

        value = Math.Clamp(value, 0.0, 1.0);
        var target = TimeSpan.FromTicks((long)(Duration.Ticks * value));
        var remaining = Duration - target;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        // During dragging we only update UI. The actual seek is committed once on release.
        Position = target;
        PositionText = FormatTime(target);
        RemainingTimeText = FormatTime(remaining);
        _pendingSeekTarget = target;
        _hasPendingSeekTarget = true;
    }

    /// <summary>Call when the user starts dragging the seek slider.</summary>
    public void BeginSeek()
    {
        // Cancel any pending debounce seek from the previous EndSeek so it cannot
        // fire mid-drag and send a stale position to VLC while the user is still dragging.
        _seekDebounceTimer?.Dispose();
        _seekDebounceTimer = null;
        _isSeeking = true;
        _hasPendingSeekTarget = false;
        _pendingSeekTarget = TimeSpan.Zero;
    }

    /// <summary>Call when the user releases the seek slider.  Idempotent — safe to call multiple times.</summary>
    public void EndSeek()
    {
        if (!_isSeeking) return; // already ended — prevent duplicate seeks
        _isSeeking = false;

        // A committed seek cancels the player's prepared/staged next track
        // (Seek → CancelPreparedNext), but this path never re-armed the prepare
        // latch — so a seek inside the prepare window left the transition with
        // "no prepared standby" and an audible cold fallback. Re-arm: the next
        // position tick re-prepares (a no-op duplicate if the standby survived).
        if (!_autoMixAdvanceQueued)
        {
            _autoMixPreparedTrackId = Guid.Empty;
            _autoMixPreparedSnapshot = null;
        }

        if (CurrentTrack == null || Duration <= TimeSpan.Zero)
            return;

        // A seek gesture that never moved the slider value is not a real seek — the
        // user pressed and released on the slider's empty hit-area (the band above or
        // below the thin track) without landing on it. Committing here would seek to
        // the current position and audibly restart playback from there, so ignore it.
        if (!_hasPendingSeekTarget)
            return;

        var target = _pendingSeekTarget;
        _hasPendingSeekTarget = false;

        // Update UI immediately so the slider stays where the user clicked
        _lastSeekTime = DateTime.UtcNow;
        _lastCommittedSeekTarget = target;

        // Debounce: if the user clicks again within SeekDebounceMs, cancel
        // the previous seek and only send the latest position to VLC.
        // This prevents hammering VLC with rapid seeks that cause audio crackling.
        DebugLogger.Info(DebugLogger.Category.Playback, "EndSeek", $"targetMs={target.TotalMilliseconds:F0}, debounce={SeekDebounceMs}ms");
        _seekDebounceTimer?.Dispose();
        _seekDebounceTimer = null;
        if (DeferSeekWhileStopped(target))
        {
            Seeked?.Invoke(this, target);
            return;
        }
        _seekDebounceTimer = new System.Threading.Timer(_ =>
        {
            DebugLogger.Info(DebugLogger.Category.Playback, "SeekDebounce.Fire", $"targetMs={target.TotalMilliseconds:F0}");
            _audioPlayer.Seek(target);
            Dispatcher.UIThread.Post(() => Seeked?.Invoke(this, target));
        }, null, SeekDebounceMs, Timeout.Infinite);
    }

    // ── Private helpers ──────────────────────────────────────

    /// <summary>Debounces the per-play library save (play count / LastPlayed persistence).</summary>
    private System.Threading.Timer? _librarySaveDebounce;
    private const int LibrarySaveDebounceMs = 5000;

    // Tracks whose play state (play count / LastPlayed / saved position) changed
    // since the last save. The debounce timer flushes them as journal rows in
    // library.db instead of re-serializing the whole library.json per play.
    private readonly HashSet<Track> _pendingPlayStateSaves = new();

    private void MarkPlayStateDirty(Track? track)
    {
        // A non-library track has no library row: journaling it only left orphan
        // user-state rows in library.db under its per-session Id (GitHub #86).
        if (track == null || track.IsExternal) return;
        lock (_pendingPlayStateSaves)
            _pendingPlayStateSaves.Add(track);
    }

    /// <summary>Writes the accumulated play-state changes as user-state journal rows.</summary>
    private async Task FlushPendingPlayStateAsync()
    {
        List<Track> pending;
        lock (_pendingPlayStateSaves)
        {
            if (_pendingPlayStateSaves.Count == 0) return;
            pending = _pendingPlayStateSaves.ToList();
            _pendingPlayStateSaves.Clear();
        }
        await _library.SaveTrackUserStateAsync(pending);
    }

    private void PlayTrack(Track track)
    {
        var playTrackStart = Stopwatch.GetTimestamp();
        _seekDebounceTimer?.Dispose();
        _seekDebounceTimer = null;
        CancelNaturalEndFallback();
        _hasPendingSeekTarget = false;
        _autoMixAdvanceQueued = false;
        _autoMixPreparedTrackId = Guid.Empty;
        _autoMixCommitGuardUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _autoMixPreparedSnapshot = null;

        // Save playback position for the outgoing track if it has RememberPlaybackPosition
        if (CurrentTrack?.RememberPlaybackPosition == true)
        {
            CurrentTrack.SavedPositionMs = (long)Position.TotalMilliseconds;
            MarkPlayStateDirty(CurrentTrack);
        }

        CurrentTrack = track;
        LoadAlbumArt(track);
        Duration = track.Duration;
        DurationText = FormatTime(track.Duration);
        RemainingTimeText = FormatTime(track.Duration);
        Position = TimeSpan.Zero;
        PositionFraction = 0;
        PositionText = "0:00";

        // Arm the seek-settle guard so stale VLC position callbacks from the
        // previous track are rejected (same mechanism used after user seeks).
        _lastSeekTime = DateTime.UtcNow;
        _lastCommittedSeekTarget = TimeSpan.Zero;

        State = PlaybackState.Playing;

        // Update play count and last played time
        track.PlayCount++;
        track.LastPlayed = DateTime.UtcNow;
        MarkPlayStateDirty(track);
        _playHistory?.RecordPlay(track);
        MarkRecentlyPlayed(track);

        // Apply per-track volume adjustment
        _audioPlayer.VolumeAdjust = track.VolumeAdjust;

        // Set pending seek position BEFORE Play() so VlcAudioPlayer applies it
        // inside PlayInternal after the media is loaded (avoids race condition).
        long seekMs = -1;
        var seekSource = "none";
        var autoMixStartMs = Interlocked.Exchange(ref _pendingAutoMixNextStartMs, -1);
        // One-shot: any track change consumes the restored-session resume target.
        var resumeMs = Interlocked.Exchange(ref _resumePositionMs, -1);
        if (autoMixStartMs > 0 && TimeSpan.FromMilliseconds(autoMixStartMs) < track.Duration)
        {
            seekMs = autoMixStartMs;
            seekSource = "automix";
        }
        else if (resumeMs > 0 && track.Id == _resumeTrackId
                 && TimeSpan.FromMilliseconds(resumeMs) < track.Duration)
        {
            seekMs = resumeMs;
            seekSource = "restore";
        }
        else if (track.StartTimeMs > 0 && TimeSpan.FromMilliseconds(track.StartTimeMs) < track.Duration)
        {
            seekMs = track.StartTimeMs;
            seekSource = "startTime";
        }
        else if (track.RememberPlaybackPosition && track.SavedPositionMs > 0
                 && TimeSpan.FromMilliseconds(track.SavedPositionMs) < track.Duration)
        {
            seekMs = track.SavedPositionMs;
            seekSource = "savedPosition";
        }
        _audioPlayer.PendingSeekMs = seekMs;
        // Logged here rather than on entry so it can say which start position won.
        DebugLogger.Info(DebugLogger.Category.Playback, "PlayTrack",
            $"title={track.Title}, id={track.Id}, duration={track.Duration}, seekMs={seekMs}, seekSource={seekSource}");

        if (seekMs > 0)
        {
            var seekPos = TimeSpan.FromMilliseconds(seekMs);
            Position = seekPos;
            PositionText = FormatTime(seekPos);
            _lastCommittedSeekTarget = seekPos;
            PositionFraction = track.Duration.TotalSeconds > 0
                ? seekPos.TotalSeconds / track.Duration.TotalSeconds
                : 0;
        }

        _audioPlayer.Play(track.FilePath);

        // Apply per-track EQ preset (or restore global)
        _settings?.ApplyEqPresetByName(
            string.IsNullOrEmpty(track.EqPreset) ? null : track.EqPreset);

        // Ensure UI updates happen on UI thread and force property re-evaluation
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CurrentTrack));
            OnPropertyChanged(nameof(HasContent));
        }, DispatcherPriority.Render);

        // Fire event to notify that a new track started
        TrackStarted?.Invoke(this, track);

        // Persist play count/LastPlayed with a debounce: rapid skips coalesce
        // into a single write. Only the changed tracks' journal rows are written
        // (library.db) — not the whole library.json.
        _librarySaveDebounce?.Dispose();
        _librarySaveDebounce = new System.Threading.Timer(
            _ => _ = FlushPendingPlayStateAsync(), null, LibrarySaveDebounceMs, System.Threading.Timeout.Infinite);

        // Keep the on-disk queue snapshot current so a non-graceful exit
        // (tray + OS shutdown, task kill) still restores this session.
        SaveQueueStateInBackground();
        UiStallWatchdog.ReportIfSlow("PlayTrack", playTrackStart);
    }

    private enum QueueAdvanceReason
    {
        Natural,
        AutoMix,
        UserSkip,
        Previous,
        Error
    }

    private void AdvanceQueue(QueueAdvanceReason reason = QueueAdvanceReason.Natural)
    {
        // Re-entrancy guard — if TrackEnded fires twice (VLC race), ignore the second.
        if (_isAdvancingQueue)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AdvanceQueue.Reentrant", $"reason={reason}");
            return;
        }
        _isAdvancingQueue = true;
        try
        {
            AdvanceQueueCore(reason);
        }
        finally
        {
            _isAdvancingQueue = false;
        }
    }

    private bool _allowExplicitContent = true;

    /// <summary>
    /// Explicit tracks taken OUT of UpNext while the filter is off, each with the track
    /// that followed it at the time, so turning the filter back on can put them back where
    /// they were (or at the end if that neighbour has since played). Removing without
    /// parking made the toggle one-way: off pruned the queue, on brought nothing back.
    /// Cleared whenever the queue is replaced or cleared — parked tracks belong to the
    /// queue they were parked from.
    /// </summary>
    private readonly List<(Track Track, Track? Successor)> _parkedExplicit = new();

    /// <summary>
    /// Settings → Explicit Content. On (default): no effect. Off: explicit tracks are a
    /// PLAYBACK filter — parked out of the queue the moment the switch flips (and restored
    /// when it flips back), skipped on queue advance, left out of shuffle / Autoplay /
    /// Radio, never pre-rolled for a gapless or AutoMix handoff. Not a library filter: a
    /// track the user plays directly (click, Play Now) still plays, since that is a
    /// deliberate act.
    /// </summary>
    public bool AllowExplicitContent
    {
        get => _allowExplicitContent;
        set
        {
            if (_allowExplicitContent == value) return;
            _allowExplicitContent = value;
            if (value) RestoreParkedExplicit();
            else PruneBlockedExplicit(wholeQueue: true);
        }
    }

    /// <summary>True when the filter is on and this track must not start on its own.</summary>
    private bool IsBlockedExplicit(Track track) => track.IsExplicit && !_allowExplicitContent;

    /// <summary>Library tracks eligible for automatic selection (Autoplay, Radio, library shuffle).</summary>
    private IEnumerable<Track> PlayableLibraryTracks()
        => _allowExplicitContent ? _library.Tracks : _library.Tracks.Where(t => !t.IsExplicit);

    /// <summary>
    /// Takes blocked explicit tracks out of UpNext. <paramref name="wholeQueue"/> (the
    /// switch flipping off, or a fresh shuffle order) PARKS every explicit track with its
    /// following non-explicit neighbour so <see cref="RestoreParkedExplicit"/> can undo it.
    /// The head-only form (an advance or pre-roll about to pick UpNext[0]) drops the
    /// leading run outright: those are tracks the queue is moving past, exactly like a
    /// played track, so they do not come back. Nothing removed here plays or enters History.
    /// </summary>
    private void PruneBlockedExplicit(bool wholeQueue = false)
    {
        if (_allowExplicitContent || UpNext.Count == 0) return;

        int removed = 0;
        if (wholeQueue)
        {
            var kept = new List<Track>(UpNext.Count);
            var pendingParked = new List<Track>();
            foreach (var t in UpNext)
            {
                if (t.IsExplicit)
                {
                    pendingParked.Add(t);
                    continue;
                }
                // The first clean track after a run of explicit ones is the anchor for all of them.
                foreach (var p in pendingParked) _parkedExplicit.Add((p, t));
                pendingParked.Clear();
                kept.Add(t);
            }
            foreach (var p in pendingParked) _parkedExplicit.Add((p, null)); // trailing: restore at the end
            removed = UpNext.Count - kept.Count;
            if (removed > 0) UpNext.ReplaceAll(kept);
        }
        else
        {
            while (UpNext.Count > 0 && UpNext[0].IsExplicit)
            {
                UpNext.RemoveAt(0);
                removed++;
            }
        }

        if (removed > 0)
        {
            MarkQueueChanged();
            DebugLogger.Info(DebugLogger.Category.Queue, "ExplicitFilter.Pruned",
                $"removed={removed}, wholeQueue={wholeQueue}, parked={_parkedExplicit.Count}, queueCount={UpNext.Count}");
        }
    }

    /// <summary>
    /// Puts parked explicit tracks back: in front of their remembered neighbour when it is
    /// still queued, otherwise at the end (the neighbour has played, so the slot is gone).
    /// A parked track the user has meanwhile re-queued by hand is not added twice.
    /// </summary>
    private void RestoreParkedExplicit()
    {
        if (_parkedExplicit.Count == 0) return;

        int restored = 0;
        foreach (var (track, successor) in _parkedExplicit)
        {
            if (UpNext.Any(t => t.Id == track.Id)) continue;
            var index = successor == null ? -1 : IndexOfTrack(successor);
            if (index < 0) UpNext.Add(track);
            else UpNext.Insert(index, track);
            restored++;
        }
        _parkedExplicit.Clear();

        if (restored > 0)
        {
            MarkQueueChanged();
            DebugLogger.Info(DebugLogger.Category.Queue, "ExplicitFilter.Restored",
                $"restored={restored}, queueCount={UpNext.Count}");
        }

        int IndexOfTrack(Track t)
        {
            for (int i = 0; i < UpNext.Count; i++)
                if (UpNext[i].Id == t.Id) return i;
            return -1;
        }
    }

    private void AdvanceQueueCore(QueueAdvanceReason reason)
    {
        if (reason is QueueAdvanceReason.UserSkip or QueueAdvanceReason.Previous or QueueAdvanceReason.Error)
            CancelAutoMixTransition(reason == QueueAdvanceReason.Error ? "playback error" : "user skipped");

        // "Stop after current track": when the track ends naturally, halt here
        // instead of advancing. Queue and current track stay intact so PlayPause
        // resumes from a sensible state. User skips bypass and clear the flag.
        if (StopAfterCurrentTrack && reason is QueueAdvanceReason.Natural or QueueAdvanceReason.AutoMix)
        {
            // TryAdvanceForAutoMix has already armed the crossfade and stashed the *next*
            // track's planned entry offset in _pendingAutoMixNextStartMs. Returning without
            // clearing it left that offset for the next PlayTrack to consume — pressing
            // Play afterwards restarted the current track at the following track's entry
            // point instead of from where it stopped.
            CancelAutoMixTransition("stop after current track");
            StopAfterCurrentTrack = false;
            State = PlaybackState.Stopped;
            DebugLogger.Info(DebugLogger.Category.Playback, "SleepTimer.StoppedAfterTrack",
                $"track={CurrentTrack?.Title}");
            return;
        }
        if (StopAfterCurrentTrack && reason is QueueAdvanceReason.UserSkip or QueueAdvanceReason.Previous)
            StopAfterCurrentTrack = false;

        // Handle repeat one mode — replay via PlayTrack() so PlayCount is
        // incremented, TrackStarted fires, and all state updates properly.
        // Only on natural end: an explicit Next/Previous must still advance,
        // otherwise the user is stuck on the repeating track.
        if (RepeatMode == RepeatMode.One && CurrentTrack != null &&
            reason is QueueAdvanceReason.Natural or QueueAdvanceReason.AutoMix)
        {
            PlayTrack(CurrentTrack);
            return;
        }

        if (CurrentTrack != null)
        {
            if (CurrentTrack.RememberPlaybackPosition == true)
            {
                CurrentTrack.SavedPositionMs = 0;
                MarkPlayStateDirty(CurrentTrack);
            }
            History.Insert(0, CurrentTrack);
            _queueHistoryDepth++;
            TrimHistory();
        }

        // Explicit Content off: whatever explicit tracks sit at the head of the queue are
        // dropped here, so the advance below lands on the first playable one (or falls
        // through to repeat / autoplay / stop when nothing playable is left).
        PruneBlockedExplicit();

        if (UpNext.Count > 0)
        {
            DebugLogger.Info(
                DebugLogger.Category.Queue,
                "TrackEnded.Next",
                $"queueCount={UpNext.Count}, historyCount={History.Count}, reason={reason}");
            var next = UpNext[0];
            UpNext.RemoveAt(0);
            PlayTrack(next);
            RefillRadioIfNeeded();
        }
        else if (RepeatMode == RepeatMode.All && (_repeatCycleTracks.Count > 0 || History.Count > 0))
        {
            // Repeat all: restart the full cycle. Prefer the uncapped cycle list; fall
            // back to History only when there isn't one (e.g. a queue restored from a
            // previous session, where the cycle was never recorded).
            var allTracks = _repeatCycleTracks.Count > 0
                ? new List<Track>(_repeatCycleTracks)
                : History.Reverse().ToList();
            if (!_allowExplicitContent)
                allTracks.RemoveAll(IsBlockedExplicit);

            History.Clear();
            _queueHistoryDepth = 0;
            _originalQueue.Clear(); // clear stale shuffle state to prevent wrong restore

            if (allTracks.Count == 0) { StopAndClear("repeatAllNothingPlayable"); return; }

            UpNext.ReplaceAll(allTracks.Skip(1).ToList());
            PlayTrack(allTracks[0]);
        }
        else
        {
            if (TryContinueWithAutoplay(reason))
                return;

            DebugLogger.Info(
                DebugLogger.Category.Queue,
                "TrackEnded.NoNext",
                $"queueCount={UpNext.Count}, historyCount={History.Count}, repeat={RepeatMode}");
            StopAndClear("queueEnded");
        }
    }

    /// <summary>
    /// Tag-based Autoplay (Settings > Playback): when the queue is exhausted by a
    /// natural track end, continues playback with similar tracks from the library —
    /// same genre as the just-ended track, then same primary artist when the genre
    /// tier has nothing. Returns false (caller stops exactly as before) when the
    /// setting is off, the end wasn't natural, or neither tier has candidates.
    ///
    /// This runs after the stop-after-current guard, and repeat modes never reach
    /// the exhaustion branch at all (Repeat One replays above, Repeat All wraps the
    /// cycle), so autoplay can only extend a queue that would otherwise go silent.
    /// The first pick plays immediately; the rest are appended to Up Next so the
    /// queue popup shows where playback is heading. Selection is a synchronous O(n)
    /// scan (see <see cref="AutoplayService"/>) — cheap enough for the advance path.
    /// </summary>
    private bool TryContinueWithAutoplay(QueueAdvanceReason reason)
    {
        if (!AutoplayEnabled)
            return false;
        // Only a genuinely finished track flows into autoplay. User skips on an empty
        // queue, Previous, and playback errors all keep today's stop behavior — and a
        // queue drained by removals never advances by itself, so it can't land here.
        if (reason is not (QueueAdvanceReason.Natural or QueueAdvanceReason.AutoMix))
            return false;
        // Defensive: both repeat modes are handled before the exhaustion branch; if
        // that ever changes, autoplay must still never fight a repeat wrap.
        if (RepeatMode != RepeatMode.Off)
            return false;
        var seed = CurrentTrack; // the just-ended track (already prepended to History)
        if (seed == null)
            return false;

        // No immediate repeats: skip everything just played (History) and everything
        // autoplay already picked this session.
        var exclude = new HashSet<Guid>(_autoplayPickedIds) { seed.Id };
        foreach (var t in History)
            exclude.Add(t.Id);

        // Materialize once: the fall-back call below scans the same pool again.
        var pool = _allowExplicitContent ? _library.Tracks : PlayableLibraryTracks().ToList();
        var picks = _autoplayService.PickSimilar(seed, pool, AutoplayBatchSize, exclude);
        if (picks.Count == 0)
        {
            // Candidate pool exhausted for this run — allow reuse: start a fresh cycle
            // that only refuses to replay the seed itself back-to-back.
            _autoplayPickedIds.Clear();
            picks = _autoplayService.PickSimilar(
                seed, pool, AutoplayBatchSize, new HashSet<Guid> { seed.Id });
            if (picks.Count == 0)
                return false;
        }

        foreach (var t in picks)
            _autoplayPickedIds.Add(t.Id);

        DebugLogger.Info(
            DebugLogger.Category.Queue,
            "Autoplay.Continue",
            $"seed={seed.Title}, genre={seed.Genre}, picked={picks.Count}, reason={reason}");

        MarkQueueChanged();
        if (picks.Count > 1)
        {
            _suppressHasContentNotify = true;
            try
            {
                for (int i = 1; i < picks.Count; i++)
                    UpNext.Add(picks[i]);
            }
            finally
            {
                _suppressHasContentNotify = false;
                OnPropertyChanged(nameof(HasContent));
            }
        }
        PlayTrack(picks[0]);
        return true;
    }

    /// <summary>
    /// When Track Radio is active and the queue is running low, appends another batch
    /// of similar tracks so playback never runs dry. Excludes everything already played
    /// or queued so the radio doesn't repeat itself.
    ///
    /// The similarity scan (<see cref="IRadioService.BuildSimilar"/>) is O(library size)
    /// and runs at every track transition once the queue drains, so it is offloaded to the
    /// ThreadPool to avoid stuttering the UI thread on large libraries. The candidate pool
    /// and exclude set are snapshotted on the UI thread first so the background enumeration
    /// can't race library mutations; results are marshaled back to append to <see cref="UpNext"/>
    /// (which is bound to the UI). A re-entrancy guard prevents overlapping transitions from
    /// launching concurrent refills.
    /// </summary>
    private void RefillRadioIfNeeded()
    {
        if (!IsRadioActive || _radioSeed == null || UpNext.Count > RadioRefillThreshold)
            return;
        if (_radioRefillInFlight)
            return;

        // Snapshot on the UI thread so the background scan can't race library mutations
        // or live queue collections.
        var seed = _radioSeed;
        var snapshot = PlayableLibraryTracks().ToList();
        var exclude = new HashSet<Guid>(History.Select(t => t.Id));
        foreach (var t in UpNext) exclude.Add(t.Id);
        if (CurrentTrack != null) exclude.Add(CurrentTrack.Id);
        exclude.Add(seed.Id);

        _radioRefillInFlight = true;
        _ = Task.Run(() =>
        {
            var more = _radioService.BuildSimilar(seed, snapshot, RadioBatchSize, exclude);
            Dispatcher.UIThread.Post(() =>
            {
                _radioRefillInFlight = false;
                // The user may have replaced the queue (or re-seeded radio) during the hop;
                // discard a stale batch rather than polluting the new queue.
                if (!IsRadioActive || !ReferenceEquals(_radioSeed, seed))
                    return;
                foreach (var t in more) UpNext.Add(t);
            });
        });
    }

    private void GoBackInQueue(QueueAdvanceReason reason = QueueAdvanceReason.Previous)
    {
        if (History.Count == 0) return;
        CancelAutoMixTransition(reason == QueueAdvanceReason.Previous ? "user skipped" : "queue changed");
        MarkQueueChanged();

        // Push current track back to front of queue
        if (CurrentTrack != null)
        {
            UpNext.Insert(0, CurrentTrack);
        }

        var prev = History[0];
        History.RemoveAt(0);
        if (_queueHistoryDepth > 0) _queueHistoryDepth--;
        PlayTrack(prev);
    }

    /// <summary>
    /// Decode width for the player-bar / now-playing artwork bitmap.
    /// 768 px keeps the ~350px now-playing / 336px mini-player covers sharp on
    /// 150–200% DPI displays (which need physical pixels beyond the logical size)
    /// and matches the album-grid surfaces (Albums/Favorites/Home use 768), so
    /// every large surface shares one <see cref="ArtworkCache"/> entry instead of
    /// decoding the same cover at a player-only size. Mismatched sizes caused
    /// visible cache-miss decodes — the artwork "flicker" on track switch.
    /// </summary>
    private const int PlayerArtDecodeWidth = 512;

    /// <summary>Bumped on every <see cref="LoadAlbumArt"/> call so a slow background
    /// decode that finishes after the track changed again is discarded.</summary>
    private int _albumArtGeneration;

    private void LoadAlbumArt(Track track)
    {
        // Bitmaps come from the shared LRU cache, which owns their lifetime — never
        // dispose them here. The cache de-dupes, so track switches no longer leak.
        // The index points a track with its own embedded cover at that cover
        // (TrackArtwork); anything else falls back to the album's.
        var artPath = !string.IsNullOrEmpty(track.AlbumArtworkPath) && File.Exists(track.AlbumArtworkPath)
            ? track.AlbumArtworkPath
            : _persistence.GetArtworkPath(track.AlbumId);
        var generation = Interlocked.Increment(ref _albumArtGeneration);

        // Drive CachedImage-based surfaces (playback bar) via a path string. They
        // handle previous-frame retention during background decode, so there's no
        // flash. Set null when the file is missing so the placeholder renders.
        var hasArtFile = !string.IsNullOrEmpty(artPath) && File.Exists(artPath);
        CurrentArtPath = hasArtFile ? artPath : null;

        // No artwork available for this track — clear immediately so we don't
        // keep showing the previous track's cover. GetArtworkPath always returns
        // a computed path, so the file-existence check is what actually detects
        // a coverless track here.
        if (!hasArtFile)
        {
            AlbumArt = null;
            ArtworkAccentColor = null;
            CurrentAnimatedCoverPath = _animatedCovers.Resolve(track);
            return;
        }

        // Vibrant colour for the visualizer tint: SkiaSharp file decode, path-cached after
        // the first call, so it runs off the UI thread and lands only if still current.
        _ = Task.Run(() =>
        {
            Color? accent = null;
            try { accent = Color.Parse(ShareCardRenderer.GetVibrantColorHex(artPath)); }
            catch { /* no tint for this cover */ }
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == Volatile.Read(ref _albumArtGeneration))
                    ArtworkAccentColor = accent;
            });
        });

        // Fast path: a cache hit returns synchronously — no I/O or decode on the UI thread.
        var cached = ArtworkCache.TryGet(artPath, PlayerArtDecodeWidth);
        if (cached != null)
        {
            AlbumArt = cached;
        }
        else
        {
            // Cache miss: decode on a background thread so click-to-play doesn't stall.
            // Keep the previous AlbumArt visible until the new bitmap is ready —
            // clearing here causes a placeholder flash on every track switch.
            _ = Task.Run(() =>
            {
                var bitmap = ArtworkCache.LoadAndCache(artPath, PlayerArtDecodeWidth);
                if (bitmap == null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Volatile.Read(ref _albumArtGeneration))
                        AlbumArt = bitmap;
                });
            });
        }

        CurrentAnimatedCoverPath = _animatedCovers.Resolve(track);
    }

    /// <summary>
    /// Re-resolves the current track's animated cover. Call after a metadata edit
    /// may have added or removed one, so player-bound surfaces (album detail header,
    /// now playing, mini-art) pick it up without a track change.
    /// </summary>
    public void RefreshAnimatedCover()
        => CurrentAnimatedCoverPath = CurrentTrack != null ? _animatedCovers.Resolve(CurrentTrack) : null;

    private void TrimHistory()
    {
        // Keep only the last 50 items
        while (History.Count > 50)
            History.RemoveAt(History.Count - 1);
    }

    private void OnDurationResolved(object? sender, TimeSpan resolvedDuration)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (resolvedDuration > TimeSpan.Zero &&
                Math.Abs((resolvedDuration - Duration).TotalMilliseconds) > 200)
            {
                Duration = resolvedDuration;
                DurationText = FormatTime(Duration);

                // Recalculate remaining and fraction with corrected duration
                var remaining = Duration - Position;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                RemainingTimeText = FormatTime(remaining);

                if (Duration.TotalSeconds > 0)
                    PositionFraction = Position.TotalSeconds / Duration.TotalSeconds;
            }
        });
    }

    private long _lastPositionRejectedLogTick; // UI thread only

    // Rate-limited (1/s): a tick one of the seek guards below dropped. The slider
    // snap-back / frozen-position evidence.
    private void NotePositionRejected(string guard, TimeSpan latest, double msSinceSeek)
    {
        if (!DebugLogger.IsEnabled) return;
        var now = Environment.TickCount64;
        if (now - _lastPositionRejectedLogTick < 1000) return;
        _lastPositionRejectedLogTick = now;
        DebugLogger.Info(DebugLogger.Category.Playback, "Position.Rejected",
            $"guard={guard}, latestMs={latest.TotalMilliseconds:F0}, targetMs={_lastCommittedSeekTarget.TotalMilliseconds:F0}, " +
            $"msSinceSeek={msSinceSeek:F0}, rate={PlaybackRate}");
    }

    private void OnPositionChanged(object? sender, TimeSpan pos)
    {
        // Store latest position and coalesce: if an update is already queued on the
        // UI dispatcher, just overwrite the value so only the freshest position is applied.
        // This prevents jitter when the UI thread is briefly busy (e.g. during scroll).
        _latestVlcPosition = pos;
        if (_positionUpdateQueued) return;
        _positionUpdateQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _positionUpdateQueued = false;
            // Audio is demonstrably playing — the error-cascade breaker resets.
            if (_consecutivePlaybackErrors != 0) _consecutivePlaybackErrors = 0;
            var latest = _latestVlcPosition;

            if (_isSeeking) return; // don't update while user is dragging

            // Ignore stale position updates after seeking to prevent snap-back.
            // VLC needs time to flush old buffers and settle at the new position.
            var msSinceSeek = (DateTime.UtcNow - _lastSeekTime).TotalMilliseconds;
            if (msSinceSeek < SeekSettleWindowMs)
            {
                NotePositionRejected("settle", latest, msSinceSeek);
                return;
            }

            // After PlayTrack() updates CurrentTrack, the single VLC player can still
            // report positions from the outgoing song while it fades/stops. Those old
            // near-end positions must not drive AutoMix for the newly selected track.
            if (msSinceSeek < TrackStartStalePositionGuardMs)
            {
                var expectedSeconds = _lastCommittedSeekTarget.TotalSeconds;
                var maxPlausibleSeconds = expectedSeconds + (msSinceSeek / 1000d) + 4;
                if (latest.TotalSeconds > maxPlausibleSeconds)
                {
                    NotePositionRejected("trackStartStale", latest, msSinceSeek);
                    return;
                }
            }

            // Extended settle: even after the base window, reject positions that are
            // clearly stale (>2s from the seek target). VLC's buffer refill can take
            // longer than SeekSettleWindowMs on some codecs/containers.
            if (msSinceSeek < SeekSettleWindowMs * 2 &&
                _lastCommittedSeekTarget > TimeSpan.Zero &&
                Math.Abs((latest - _lastCommittedSeekTarget).TotalSeconds) > 2.0)
            {
                NotePositionRejected("extendedSettle", latest, msSinceSeek);
                return;
            }

            // Clamp position to duration — VLC may report a position slightly past
            // the stored metadata duration. Prefer decoder-reported duration and
            // never force position backwards, otherwise final lyric lines can be missed.
            var decoderDuration = _audioPlayer.Duration;
            var effectiveDuration = Duration;

            if (decoderDuration > TimeSpan.Zero &&
                Math.Abs((decoderDuration - Duration).TotalSeconds) > 0.5)
            {
                effectiveDuration = decoderDuration;
                Duration = decoderDuration;
                DurationText = FormatTime(Duration);
            }

            if (effectiveDuration > TimeSpan.Zero && latest > effectiveDuration)
            {
                effectiveDuration = latest;
                Duration = effectiveDuration;
                DurationText = FormatTime(Duration);
            }

            // Calculate ALL values FIRST before setting any properties
            var newPosition = latest;
            var newPositionText = FormatTime(latest);
            var newPositionFraction = effectiveDuration.TotalSeconds > 0
                ? latest.TotalSeconds / effectiveDuration.TotalSeconds
                : 0;

            // Clamp fraction to valid range
            newPositionFraction = Math.Clamp(newPositionFraction, 0.0, 1.0);

            // Calculate remaining time
            var remaining = effectiveDuration - latest;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var newRemainingTimeText = FormatTime(remaining);

            // Now update ALL properties in immediate succession
            Position = newPosition;
            PositionText = newPositionText;
            PositionFraction = newPositionFraction;
            RemainingTimeText = newRemainingTimeText;

            if (TryAdvanceForAutoMix(newPosition, effectiveDuration))
                return;

            if (TryAdvanceForGapless(newPosition, effectiveDuration))
                return;

            ScheduleNaturalEndFallbackIfNeeded(newPosition, effectiveDuration);

            // Check per-track stop time
            if (CurrentTrack?.StopTimeMs > 0)
            {
                var stopTime = TimeSpan.FromMilliseconds(CurrentTrack.StopTimeMs);
                if (newPosition >= stopTime)
                {
                    AdvanceQueue();
                    return;
                }
            }

        });
    }

    private void ScheduleNaturalEndFallbackIfNeeded(TimeSpan position, TimeSpan duration)
    {
        if (CurrentTrack == null ||
            State != PlaybackState.Playing ||
            duration <= TimeSpan.Zero ||
            position < duration - TimeSpan.FromSeconds(NaturalEndToleranceSeconds))
        {
            return;
        }

        // Don't reschedule if a fallback for this track is already armed — let it fire.
        if (_naturalEndFallbackTimer != null) return;

        var trackId = CurrentTrack.Id;
        var sessionId = _audioPlayer.CurrentSessionId;
        var armedAtPosition = position;
        _naturalEndFallbackTimer = new System.Threading.Timer(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                // The timer is one-shot and has fired — clear it up front so the
                // scheduler can re-arm if an early return below leaves the track
                // still approaching its end (e.g. bailed on a pause/drag). A spent
                // timer left in the field blocked all re-arming for the rest of
                // the track, so a missed VLC TrackEnded stalled playback at the end.
                _naturalEndFallbackTimer?.Dispose();
                _naturalEndFallbackTimer = null;

                if (CurrentTrack?.Id != trackId ||
                    _audioPlayer.CurrentSessionId != sessionId ||
                    State != PlaybackState.Playing ||
                    _isSeeking)
                {
                    return;
                }

                var durationNow = Duration;
                if (durationNow <= TimeSpan.Zero ||
                    Position < durationNow - TimeSpan.FromSeconds(NaturalEndToleranceSeconds))
                {
                    return;
                }

                // If position is still moving forward beyond where we armed, the
                // track is genuinely playing past metadata-reported duration —
                // wait for the next sample rather than advancing prematurely.
                if (Position > armedAtPosition + TimeSpan.FromSeconds(NaturalEndToleranceSeconds))
                {
                    return;
                }

                DebugLogger.Info(
                    DebugLogger.Category.Playback,
                    "TrackEnded.Fallback",
                    $"track={CurrentTrack?.Title}, queueCount={UpNext.Count}, repeat={RepeatMode}");
                AdvanceQueue(QueueAdvanceReason.Natural);
            });
        }, null, NaturalEndFallbackDelayMs, Timeout.Infinite);
    }

    private void CancelNaturalEndFallback()
    {
        _naturalEndFallbackTimer?.Dispose();
        _naturalEndFallbackTimer = null;
    }

    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal bool TryAdvanceForAutoMix(TimeSpan position, TimeSpan duration)
    {
        // Stop-after-current: no early handoff. The stop branch in AdvanceQueueCore only
        // flips State, so an advance before the real end let the track play out and its
        // TrackEnded then started the next one. Let the track end naturally instead.
        if (AutoMixTransitionMode == Noctis.Models.AutoMixTransitionMode.Off ||
            StopAfterCurrentTrack ||
            _autoMixAdvanceQueued ||
            CurrentTrack == null ||
            UpNext.Count == 0)
            return false;

        if (State != PlaybackState.Playing || DateTime.UtcNow < _autoMixCommitGuardUntilUtc)
            return false;

        // Never pre-roll a track the explicit filter is about to skip.
        PruneBlockedExplicit();
        if (UpNext.Count == 0)
            return false;

        var nextTrack = UpNext[0];
        var plan = AutoMixTransitionPlanner.CreateTransitionPlan(CurrentTrack, nextTrack, CreateAutoMixOptions());
        LogAutoMixPlan(CurrentTrack, nextTrack, plan);

        if (!plan.IsEnabled)
        {
            _audioPlayer.SetCrossfade(false, 6);
            return false;
        }

        var transitionEnd = duration;
        if (CurrentTrack.StopTimeMs > 0)
        {
            var stopTime = TimeSpan.FromMilliseconds(CurrentTrack.StopTimeMs);
            if (stopTime > TimeSpan.Zero && stopTime < transitionEnd)
                transitionEnd = stopTime;
        }

        if (plan.UseSilenceTrim && plan.CurrentSilence.EndSilence > TimeSpan.Zero)
            transitionEnd -= plan.CurrentSilence.EndSilence;

        if (transitionEnd <= TimeSpan.Zero)
            return false;

        var fadeStart = plan.Duration > TimeSpan.Zero
            ? transitionEnd - plan.Duration
            : plan.CurrentTrackStartFadePosition;
        if (fadeStart < TimeSpan.Zero)
            fadeStart = TimeSpan.Zero;

        // A seek can land directly inside the fade window without ever crossing the
        // approach band; without a prepared snapshot the validator below would cancel
        // on every tick and the track would end with no transition at all.
        var preloadLead = TimeSpan.FromSeconds(Math.Clamp(plan.Duration.TotalSeconds + 2, 3, 8));
        if (position >= fadeStart - preloadLead && _autoMixPreparedTrackId != nextTrack.Id)
        {
            _autoMixPreparedTrackId = nextTrack.Id;
            _autoMixPreparedSnapshot = new AutoMixPreparedTransitionSnapshot(
                nextTrack.Id,
                nextTrack.FilePath,
                _queueVersion,
                IsShuffleEnabled,
                RepeatMode,
                AutoMixTransitionMode,
                _audioPlayer.CurrentSessionId);
            _audioPlayer.PrepareNext(nextTrack.FilePath, (long)plan.NextTrackStartPosition.TotalMilliseconds);
            // Prepared late (already inside the window): give the async prepare a
            // tick of head start rather than committing against a cold standby.
            if (position >= fadeStart)
                return false;
        }

        if (position < fadeStart)
            return false;

        var remaining = transitionEnd - position;
        var overlapBlend = AutoMixTransitionMode == Noctis.Models.AutoMixTransitionMode.AutoMix;
        if (overlapBlend)
        {
            // AutoMix: start the overlap blend this far from the end so both tracks play
            // together through the crossover (the old's ending + the new's start), with no
            // early fade-out and no dead air.
            if (remaining > TimeSpan.FromSeconds(AutoMixOverlapLeadSeconds))
                return false;
        }
        else if (plan.Duration > TimeSpan.Zero && remaining > plan.Duration)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(nextTrack.FilePath) || !File.Exists(nextTrack.FilePath))
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.PreparedInvalid", "next track unavailable");
            CancelAutoMixTransition("next track unavailable");
            return false;
        }

        var validation = AutoMixPreparedTransitionValidator.Validate(
            _autoMixPreparedSnapshot,
            nextTrack,
            _queueVersion,
            IsShuffleEnabled,
            RepeatMode,
            AutoMixTransitionMode,
            _audioPlayer.CurrentSessionId);
        if (!validation.IsValid)
        {
            DebugLogger.Warn(DebugLogger.Category.Playback, "AutoMix.PreparedInvalid", validation.Reason);
            CancelAutoMixTransition(validation.Reason);
            return false;
        }

        _autoMixAdvanceQueued = true;
        Interlocked.Exchange(ref _pendingAutoMixNextStartMs, (long)plan.NextTrackStartPosition.TotalMilliseconds);
        _autoMixTransitionArmedUntilUtc = DateTime.UtcNow.AddSeconds(3);
        if (overlapBlend)
        {
            // Overlap blend: both tracks play together through the crossover.
            _audioPlayer.SetCrossfade(true, AutoMixOverlapSeconds, plan.FadeCurve, overlap: true);
        }
        else
        {
            _audioPlayer.SetCrossfade(
                plan.TransitionType is AutoMixTransitionType.SimpleCrossfade or AutoMixTransitionType.BeatMatchedCrossfade or AutoMixTransitionType.SafeFade,
                Math.Max(1, (int)Math.Round(plan.Duration.TotalSeconds)),
                plan.FadeCurve);
        }
        AdvanceQueue(QueueAdvanceReason.AutoMix);
        return true;
    }

    private AutoMixPlannerOptions CreateAutoMixOptions() =>
        new(
            AutoMixTransitionMode,
            AutoMixStrength,
            AutoMixRemoveSilence,
            AutoMixAvoidAlbums,
            AutoMixBeatMatch,
            RepeatMode,
            IsShuffleEnabled,
            false,
            _settings?.CrossfadeDuration ?? 6);

    // Gapless timing: pre-roll the next track on the standby player well before
    // the end (input opened paused on its first frame), then advance ahead of the
    // final position ticks; the handoff in VlcAudioPlayer holds the swap until the
    // outgoing input actually ends and resumes the pre-rolled standby at that
    // moment, so a larger lead only buys dispatch margin — it no longer trims the
    // outgoing tail or starts the next track early. The lead must stay above one
    // position-timer period (100ms) or the end can slip past us into the
    // EndReached path; 0.5s also rides out a stalled tick (PositionTimer.Stall).
    private const double GaplessPrepareLeadSeconds = 8.0;
    private const double GaplessHandoffLeadSeconds = 0.5;

    // AutoMix overlap blend: both tracks play together through the crossover. The blend is
    // triggered AutoMixOverlapSeconds (plus a small margin so the old stops just before its
    // natural end) from the end; the engine holds both at the blend level for that long,
    // then stops the old and rises the new back to full.
    private const int AutoMixOverlapSeconds = 3;
    private const double AutoMixOverlapLeadSeconds = AutoMixOverlapSeconds + 1.0;

    private bool TryAdvanceForGapless(TimeSpan position, TimeSpan duration)
    {
        // StopAfterCurrentTrack: see TryAdvanceForAutoMix — the stop needs the natural end.
        if (!GaplessEnabled ||
            AutoMixTransitionMode != Noctis.Models.AutoMixTransitionMode.Off ||
            StopAfterCurrentTrack ||
            _autoMixAdvanceQueued ||
            CurrentTrack == null ||
            UpNext.Count == 0 ||
            RepeatMode == RepeatMode.One ||
            CurrentTrack.StopTimeMs > 0)
            return false;

        if (State != PlaybackState.Playing || _isSeeking || DateTime.UtcNow < _autoMixCommitGuardUntilUtc)
            return false;

        if (duration <= TimeSpan.Zero)
            return false;

        // Never pre-roll a track the explicit filter is about to skip.
        PruneBlockedExplicit();
        if (UpNext.Count == 0)
            return false;

        var nextTrack = UpNext[0];
        var remaining = duration - position;

        if (remaining > TimeSpan.FromSeconds(GaplessHandoffLeadSeconds))
        {
            if (remaining <= TimeSpan.FromSeconds(GaplessPrepareLeadSeconds) &&
                _autoMixPreparedTrackId != nextTrack.Id &&
                !string.IsNullOrWhiteSpace(nextTrack.FilePath))
            {
                _autoMixPreparedTrackId = nextTrack.Id;
                _autoMixPreparedSnapshot = new AutoMixPreparedTransitionSnapshot(
                    nextTrack.Id,
                    nextTrack.FilePath,
                    _queueVersion,
                    IsShuffleEnabled,
                    RepeatMode,
                    AutoMixTransitionMode,
                    _audioPlayer.CurrentSessionId);
                _audioPlayer.PrepareNext(
                    nextTrack.FilePath,
                    nextTrack.StartTimeMs > 0 ? nextTrack.StartTimeMs : -1);
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(nextTrack.FilePath) || !File.Exists(nextTrack.FilePath))
            return false;

        var validation = AutoMixPreparedTransitionValidator.Validate(
            _autoMixPreparedSnapshot,
            nextTrack,
            _queueVersion,
            IsShuffleEnabled,
            RepeatMode,
            AutoMixTransitionMode,
            _audioPlayer.CurrentSessionId,
            gapless: true);
        if (!validation.IsValid)
        {
            // Nothing usable prepared — let the normal EndReached path advance.
            DebugLogger.Info(DebugLogger.Category.Playback, "Gapless.NotPrepared", validation.Reason);
            return false;
        }

        _autoMixAdvanceQueued = true;
        _audioPlayer.SetCrossfade(false, 6);
        DebugLogger.Info(
            DebugLogger.Category.Playback,
            "Gapless.Advance",
            $"current={CurrentTrack.Title}, next={nextTrack.Title}, remainingMs={remaining.TotalMilliseconds:F0}");
        AdvanceQueue(QueueAdvanceReason.Natural);
        return true;
    }

    partial void OnAutoMixTransitionModeChanged(AutoMixTransitionMode value)
    {
        CancelAutoMixTransition(value == Noctis.Models.AutoMixTransitionMode.Off
            ? "disabled"
            : "transition mode changed");
    }

    partial void OnAutoMixStrengthChanged(AutoMixStrength value) =>
        CancelAutoMixTransition("settings changed");

    partial void OnAutoMixRemoveSilenceChanged(bool value) =>
        CancelAutoMixTransition("settings changed");

    partial void OnAutoMixAvoidAlbumsChanged(bool value) =>
        CancelAutoMixTransition("settings changed");

    partial void OnAutoMixBeatMatchChanged(bool value) =>
        CancelAutoMixTransition("settings changed");

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        if (value == RepeatMode.One)
            CancelAutoMixTransition("repeat-one enabled");
    }

    // Arming stop-after-current also drops a successor already prepared for the
    // gapless/AutoMix handoff: the splice engine (and Media3's playlist) plays a staged
    // next track on its own at the seam, so the stop would otherwise land a track late.
    partial void OnStopAfterCurrentTrackChanged(bool value)
    {
        if (value && _autoMixPreparedTrackId != Guid.Empty)
            CancelAutoMixTransition("stop after current track");
    }

    private void CancelAutoMixTransition(string reason)
    {
        var hadPending = _autoMixAdvanceQueued ||
                         Interlocked.Read(ref _pendingAutoMixNextStartMs) >= 0 ||
                         DateTime.UtcNow < _autoMixTransitionArmedUntilUtc;
        _autoMixAdvanceQueued = false;
        Interlocked.Exchange(ref _pendingAutoMixNextStartMs, -1);
        _autoMixTransitionArmedUntilUtc = DateTime.MinValue;
        _autoMixPreparedTrackId = Guid.Empty;
        _autoMixPreparedSnapshot = null;
        _audioPlayer.CancelPreparedNext();
        if (hadPending || AutoMixTransitionMode != Noctis.Models.AutoMixTransitionMode.Crossfade)
            _audioPlayer.SetCrossfade(false, 6);
        _lastAutoMixLogKey = string.Empty;
        if (hadPending)
            DebugLogger.Info(DebugLogger.Category.Playback, "AutoMix.Cancelled", $"reason={reason}");
    }

    private void MarkQueueChanged() => _queueVersion++;

    private void LogAutoMixPlan(Track current, Track next, AutoMixTransitionPlan plan)
    {
        var key = $"{current.Id:N}:{next.Id:N}:{plan.TransitionType}:{plan.Reason}";
        if (key == _lastAutoMixLogKey)
            return;

        _lastAutoMixLogKey = key;
        DebugLogger.Info(
            DebugLogger.Category.Playback,
            plan.IsEnabled ? "AutoMix.Planned" : "AutoMix.Skipped",
            $"{plan.Reason}; duration={plan.Duration.TotalSeconds:0.0}s; curve={plan.FadeCurve}; silenceTrim={plan.UseSilenceTrim}; bpmUsed={plan.UsedBpmData}; keyUsed={plan.UsedKeyData}; missingBpm={plan.MissingBpmData}; missingKey={plan.MissingKeyData}");
    }

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "TrackEnded", $"track={CurrentTrack?.Title}, queueCount={UpNext.Count}, repeat={RepeatMode}");
        Dispatcher.UIThread.Post(() =>
        {
            CancelNaturalEndFallback();
            AdvanceQueue();
        });
    }

    // Circuit breaker for the error→skip loop: a single missing/corrupt file is
    // skipped, but a queue full of dead paths (e.g. the music folder renamed or
    // unmounted while the app was closed) must not cascade through thousands of
    // tracks in a tight error loop. Reset by the first real position callback.
    private const int MaxConsecutivePlaybackErrors = 5;
    private int _consecutivePlaybackErrors;

    private void OnPlaybackError(object? sender, string message)
    {
        DebugLogger.Error(DebugLogger.Category.Playback, "PlaybackError", $"msg={message}, track={CurrentTrack?.Title}");
        // DebugLogger is the dev-only ring (off by default) — the session log
        // must carry playback errors too, or Copy Logs shows nothing for the
        // single most-reported failure class.
        DebugLog.Write("Audio", $"Playback error: {message} — track: {CurrentTrack?.Title ?? "(none)"}");
        Dispatcher.UIThread.Post(() =>
        {
            if (++_consecutivePlaybackErrors >= MaxConsecutivePlaybackErrors)
            {
                DebugLogger.Error(DebugLogger.Category.Playback, "PlaybackError.CascadeStop",
                    $"{_consecutivePlaybackErrors} consecutive playback errors — stopping instead of skipping");
                DebugLog.Write("Audio",
                    $"{_consecutivePlaybackErrors} consecutive playback errors — stopping playback");
                // A whole queue of dead paths almost always means the volume itself is
                // gone (drive offline, letter changed, partition remounted), not five
                // deleted files. Say so once per cascade so the log tells the story.
                if (UnreachableRootHint(CurrentTrack?.FilePath, Directory.Exists) is { } hint)
                    DebugLog.Write("Audio", hint);
                _consecutivePlaybackErrors = 0;
                StopAndClear("errorCascade");
                return;
            }

            // Skip to next track on error
            if (UpNext.Count > 0)
                AdvanceQueue(QueueAdvanceReason.Error);
            else
                StopAndClear("errorNoNext");
        });
    }

    /// <summary>
    /// When a local track's drive/share root cannot be reached at all, a one-line
    /// explanation for the session log; null when the root is there (so the files
    /// themselves are missing) or the path has no file-system root.
    /// </summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static string? UnreachableRootHint(string? filePath, Func<string, bool> rootExists)
    {
        if (string.IsNullOrWhiteSpace(filePath) || VlcAudioPlayer.IsPathlessMedia(filePath))
            return null;
        string? root;
        try { root = Path.GetPathRoot(filePath); }
        catch { return null; }
        if (string.IsNullOrEmpty(root)) return null;
        bool exists;
        try { exists = rootExists(root); }
        catch { exists = false; }
        return exists
            ? null
            : $"{root} is not reachable right now — every track stored there will fail until the drive is back " +
              "(or the music folder is re-added under its new drive letter).";
    }

    /// <summary>Library publishes reconciled this session (UI thread only). The first
    /// <see cref="ReconcileLogCap"/> are bracketed in the session log (#97).</summary>
    private int _libraryReconcileCount;
    private const int ReconcileLogCap = 20;

    private void OnLibraryUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A scan's progressive fill publishes the tracks found SO FAR every 1.5 s;
            // judging "deleted" against that partial index purged the queue and stopped
            // playback the moment a folder was removed or a rescan started (GitHub #72:
            // the playing album wasn't even in the removed folder). The authoritative
            // publish follows with IsPublishingPartial false — reconcile then.
            if (_library.IsPublishingPartial)
                return;

            // #97: a startup scan that found new albums ended the process with no managed
            // exception logged. Only such a scan reconciles a restored track here (the load
            // publish lands before the queue restore), and it runs while the scan thread
            // writes its own [Scan] lines, so without these the journal's last line would
            // blame whichever scan step was running. Always on, first few publishes only.
            var reconcile = ++_libraryReconcileCount;
            var log = reconcile <= ReconcileLogCap;
            if (log)
                DebugLog.Write("Player", $"library reconcile #{reconcile}: start " +
                    $"(library={_library.Tracks.Count}, current={(CurrentTrack != null ? "set" : "none")})");
            else if (reconcile == ReconcileLogCap + 1)
                DebugLog.Write("Player", "library reconcile: later ones are not logged");

            // If library is now empty, stop playback and clear everything — unless dropped
            // files are playing from outside the library (GitHub #84); the prune below
            // then drops only the library entries.
            if (_library.Tracks.Count == 0 &&
                CurrentTrack?.IsExternal != true && !UpNext.Any(t => t.IsExternal))
            {
                StopAndClear("libraryEmpty");
                if (log) DebugLog.Write("Player", $"library reconcile #{reconcile}: done (library empty)");
                return;
            }

            // Clean up UpNext and History FIRST so that if we need to advance,
            // we only advance into tracks that still exist in the library.
            // External (dropped, non-library) tracks were never indexed — not "deleted".
            var deletedTracks = UpNext.Where(t => !t.IsExternal && _library.GetTrackById(t.Id) == null).ToList();
            if (deletedTracks.Count > 0)
            {
                CancelAutoMixTransition("queue changed");
                MarkQueueChanged();
            }
            foreach (var track in deletedTracks)
                UpNext.Remove(track);

            var deletedHistory = History.Where(t => !t.IsExternal && _library.GetTrackById(t.Id) == null).ToList();
            foreach (var track in deletedHistory)
                History.Remove(track);

            // Check if current track was deleted
            if (CurrentTrack is { IsExternal: false } && _library.GetTrackById(CurrentTrack.Id) == null)
            {
                DebugLogger.Info(DebugLogger.Category.Playback, "CurrentTrackRemoved",
                    $"track={CurrentTrack.Id}, state={State}, upNext={UpNext.Count}, action={(UpNext.Count > 0 ? "advance" : "stop")}");
                // Current track was deleted, skip to next or stop
                if (UpNext.Count > 0)
                {
                    AdvanceQueue();
                }
                else
                {
                    StopAndClear("currentTrackRemoved");
                }
            }

            // Reload album art in case artwork was changed via metadata editor
            if (CurrentTrack != null)
            {
                // The decodes LoadAlbumArt starts run on the pool; "album art" brackets
                // only its UI-thread part (cache hit, CurrentArtPath, animated cover).
                if (log) DebugLog.Write("Player", $"library reconcile #{reconcile}: album art");
                LoadAlbumArt(CurrentTrack);
                // Force converter-based bindings on CurrentTrack.* to re-evaluate
                if (log) DebugLog.Write("Player", $"library reconcile #{reconcile}: CurrentTrack re-raise");
                OnPropertyChanged(nameof(CurrentTrack));
            }

            // The playing track's album may have just been imported (or removed).
            ViewCurrentTrackAlbumCommand.NotifyCanExecuteChanged();
            if (log) DebugLog.Write("Player", $"library reconcile #{reconcile}: done");
        });
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
