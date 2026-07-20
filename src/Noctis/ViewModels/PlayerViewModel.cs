using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
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
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IAnimatedCoverService _animatedCovers;

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
    private PlaybackState _state = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private double _positionFraction; // 0.0 – 1.0 for slider
    [ObservableProperty] private int _volume = 75;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private Bitmap? _albumArt;
    [ObservableProperty] private string? _currentAnimatedCoverPath;
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
    /// <summary>Opacity of the playback bar's glass fill (0–1). Driven by Settings; default
    /// 0.4 matches the original #66 alpha. Background only — controls/text stay opaque.</summary>
    [ObservableProperty] private double _islandBackgroundOpacity = 0.4;

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
    /// for legacy bindings (NowPlaying, Lyrics) that haven't been migrated yet.
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

    public PlayerViewModel(IAudioPlayer audioPlayer, ILibraryService library, IPersistenceService persistence, IAnimatedCoverService animatedCovers)
    {
        _audioPlayer = audioPlayer;
        _library = library;
        _persistence = persistence;
        _animatedCovers = animatedCovers;

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
                else if (_library.Tracks.Count > 0)
                {
                    // No track loaded — shuffle entire library
                    var allTracks = Helpers.ShuffleHelper.WeightedShuffle(_library.Tracks, recentlyPlayed: _recentlyPlayed);
                    IsShuffleEnabled = true;
                    ReplaceQueueAndPlay(allTracks, 0);
                }
                break;
        }
    }

    [RelayCommand]
    private void Next()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "Next", $"queueLen={UpNext.Count}");
        CancelAutoMixTransition("user skipped");
        if (UpNext.Count == 0) return;

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
            _audioPlayer.Seek(TimeSpan.Zero);
            _lastSeekTime = DateTime.UtcNow;
            _lastCommittedSeekTarget = TimeSpan.Zero;
            Position = TimeSpan.Zero;
            PositionFraction = 0;
            PositionText = "0:00";
            RemainingTimeText = FormatTime(Duration);
            Seeked?.Invoke(this, TimeSpan.Zero);
        }
        else if (History.Count > 0)
        {
            CancelAutoMixTransition("user skipped");
            GoBackInQueue(QueueAdvanceReason.Previous);
        }
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
        _audioPlayer.Seek(target);
        _lastSeekTime = DateTime.UtcNow;
        _lastCommittedSeekTarget = target;
        Seeked?.Invoke(this, target);
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _audioPlayer.IsMuted = IsMuted;
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
                var shuffled = Helpers.ShuffleHelper.WeightedShuffle(
                    UpNext.Where(t => !t.SkipWhenShuffling), recentlyPlayed: _recentlyPlayed);
                UpNext.ReplaceAll(shuffled);
            }
            else if (_originalQueue.Count > 0)
            {
                // Restore original queue order
                UpNext.ReplaceAll(_originalQueue);
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
    public void SetSettingsViewModel(SettingsViewModel settings) => _settings = settings;

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
    private void ViewCurrentArtist()
    {
        var artist = CurrentTrack?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
    }

    [RelayCommand]
    private void ViewArtist(Track? track)
    {
        var artist = track?.Artist;
        if (!string.IsNullOrWhiteSpace(artist))
            _viewArtistAction?.Invoke(artist);
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
        var shuffled = Helpers.ShuffleHelper.WeightedShuffle(album.Tracks);
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
        var batch = _radioService.BuildSimilar(seed, _library.Tracks, RadioBatchSize, exclude);
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
        CurrentTrack.IsFavorite = !CurrentTrack.IsFavorite;
        await _library.SaveAsync();
        _library.NotifyFavoritesChanged();
    }

    [RelayCommand]
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

        // Clear stale shuffle state — a new queue replaces whatever was shuffled.
        _originalQueue.Clear();
        IsShuffleEnabled = false;

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
    }

    /// <summary>Stops playback and clears all queue data.</summary>
    public void StopAndClear()
    {
        if (CurrentTrack?.RememberPlaybackPosition == true)
        {
            CurrentTrack.SavedPositionMs = (long)Position.TotalMilliseconds;
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
        _originalQueue.Clear();
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        PositionFraction = 0;
        PositionText = "0:00";
        DurationText = "0:00";
        RemainingTimeText = "0:00";
        // AlbumArt bitmaps are owned by the shared ArtworkCache — drop the reference,
        // don't dispose (other UI surfaces and the cache may still hold it).
        AlbumArt = null;
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
            HistoryIds = History.Select(t => t.Id).ToList()
        };
        await _persistence.SaveQueueStateAsync(state);
    }

    /// <summary>
    /// Fire-and-forget queue snapshot. The queue used to persist only during a
    /// graceful shutdown, so tray + OS-shutdown / task-kill exits lost it and
    /// the next launch had an empty playbar. Called on track change, pause and
    /// close-to-tray so a snapshot always exists.
    /// </summary>
    /// <summary>Flushes the debounced per-play library save immediately (call on shutdown).</summary>
    public Task FlushPendingLibrarySaveAsync()
    {
        _librarySaveDebounce?.Dispose();
        _librarySaveDebounce = null;
        return _library.SaveAsync();
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
                HistoryIds = History.Select(t => t.Id).ToList()
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
                    if (t.IsFaulted)
                        System.Diagnostics.Debug.WriteLine(
                            $"[Player] Queue snapshot failed: {t.Exception?.GetBaseException().Message}");
                }, TaskScheduler.Default);
        }
    }

    /// <summary>Restores queue state from persistence. Called during app startup.</summary>
    public async Task RestoreQueueStateAsync()
    {
        var state = await _persistence.LoadQueueStateAsync();
        if (state == null) return;

        // Restore history
        var restoredHistory = new List<Track>();
        foreach (var id in state.HistoryIds)
        {
            var track = _library.GetTrackById(id);
            if (track != null) restoredHistory.Add(track);
        }
        History.AddRange(restoredHistory);

        // Restore up-next
        var restoredUpNext = new List<Track>();
        foreach (var id in state.UpNextIds)
        {
            var track = _library.GetTrackById(id);
            if (track != null) restoredUpNext.Add(track);
        }
        UpNext.AddRange(restoredUpNext);

        // Restore current track (paused, not auto-playing)
        if (state.CurrentTrackId.HasValue)
        {
            var track = _library.GetTrackById(state.CurrentTrackId.Value);
            if (track != null)
            {
                CurrentTrack = track;
                LoadAlbumArt(track);
                Duration = track.Duration;
                DurationText = FormatTime(track.Duration);
                Position = TimeSpan.FromSeconds(state.PositionSeconds);
                PositionFraction = Duration.TotalSeconds > 0
                    ? state.PositionSeconds / Duration.TotalSeconds
                    : 0;
                PositionText = FormatTime(Position);
                RemainingTimeText = FormatTime(Duration > Position ? Duration - Position : TimeSpan.Zero);
                State = PlaybackState.Stopped; // user must press play

                // Pressing Play on this restored track resumes where the user
                // left off (consumed one-shot inside PlayTrack's seek chain).
                _resumePositionMs = (long)(state.PositionSeconds * 1000);
                _resumeTrackId = track.Id;

                // Ensure UI updates on UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(CurrentTrack));
                    OnPropertyChanged(nameof(HasContent));
                }, DispatcherPriority.Render);
            }
        }
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
    }

    partial void OnCurrentTrackChanged(Track? value)
    {
        OnPropertyChanged(nameof(HasContent));
        // Re-apply ReplayGain so the new track's RG tags take effect. The
        // player already reads tags at Play() time, but settings or playback
        // path changes can leave us here without a Play() call.
        if (_settings != null)
            _audioPlayer.ApplyReplayGain(_settings.ReplayGainMode, _settings.ReplayGainPreampDb);

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
        if (oldValue != null) oldValue.IsNowPlaying = false;
        if (newValue != null) newValue.IsNowPlaying = true;
        DebugLogger.Info(DebugLogger.Category.Playback, "CurrentTrack.Changed",
            $"old={oldValue?.Title ?? "<null>"} new={newValue?.Title ?? "<null>"}");
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
        var eqOn = _settings?.EqualizerEnabled ?? false;
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

        var eqDetail = eqOn
            ? (_settings?.SelectedEqPresetIndex == 0 ? "Parametric (custom)" : _settings?.SelectedEqPresetName ?? "On")
            : "Off";

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

    private void PlayTrack(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "PlayTrack", $"title={track.Title}, id={track.Id}, duration={track.Duration}");
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
        _playHistory?.RecordPlay(track);
        MarkRecentlyPlayed(track);

        // Apply per-track volume adjustment
        _audioPlayer.VolumeAdjust = track.VolumeAdjust;

        // Set pending seek position BEFORE Play() so VlcAudioPlayer applies it
        // inside PlayInternal after the media is loaded (avoids race condition).
        long seekMs = -1;
        var autoMixStartMs = Interlocked.Exchange(ref _pendingAutoMixNextStartMs, -1);
        // One-shot: any track change consumes the restored-session resume target.
        var resumeMs = Interlocked.Exchange(ref _resumePositionMs, -1);
        if (autoMixStartMs > 0 && TimeSpan.FromMilliseconds(autoMixStartMs) < track.Duration)
        {
            seekMs = autoMixStartMs;
        }
        else if (resumeMs > 0 && track.Id == _resumeTrackId
                 && TimeSpan.FromMilliseconds(resumeMs) < track.Duration)
        {
            seekMs = resumeMs;
        }
        else if (track.StartTimeMs > 0 && TimeSpan.FromMilliseconds(track.StartTimeMs) < track.Duration)
        {
            seekMs = track.StartTimeMs;
        }
        else if (track.RememberPlaybackPosition && track.SavedPositionMs > 0
                 && TimeSpan.FromMilliseconds(track.SavedPositionMs) < track.Duration)
        {
            seekMs = track.SavedPositionMs;
        }
        _audioPlayer.PendingSeekMs = seekMs;

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

        // Persist play count/LastPlayed with a debounce: rewriting the whole
        // library JSON on every track change caused a per-play UI stall, and
        // rapid skips coalesce into a single write this way.
        _librarySaveDebounce?.Dispose();
        _librarySaveDebounce = new System.Threading.Timer(
            _ => _ = _library.SaveAsync(), null, LibrarySaveDebounceMs, System.Threading.Timeout.Infinite);

        // Keep the on-disk queue snapshot current so a non-graceful exit
        // (tray + OS shutdown, task kill) still restores this session.
        SaveQueueStateInBackground();
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
        if (_isAdvancingQueue) return;
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

    private void AdvanceQueueCore(QueueAdvanceReason reason)
    {
        if (reason is QueueAdvanceReason.UserSkip or QueueAdvanceReason.Previous or QueueAdvanceReason.Error)
            CancelAutoMixTransition(reason == QueueAdvanceReason.Error ? "playback error" : "user skipped");

        // "Stop after current track": when the track ends naturally, halt here
        // instead of advancing. Queue and current track stay intact so PlayPause
        // resumes from a sensible state. User skips bypass and clear the flag.
        if (StopAfterCurrentTrack && reason is QueueAdvanceReason.Natural or QueueAdvanceReason.AutoMix)
        {
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
        if (RepeatMode == RepeatMode.One && CurrentTrack != null)
        {
            PlayTrack(CurrentTrack);
            return;
        }

        if (CurrentTrack != null)
        {
            if (CurrentTrack.RememberPlaybackPosition == true)
            {
                CurrentTrack.SavedPositionMs = 0;
            }
            History.Insert(0, CurrentTrack);
            TrimHistory();
        }

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
        else if (RepeatMode == RepeatMode.All && History.Count > 0)
        {
            // Repeat all: move all history back to queue and restart
            var allTracks = History.Reverse().ToList();
            History.Clear();
            _originalQueue.Clear(); // clear stale shuffle state to prevent wrong restore
            foreach (var track in allTracks.Skip(1))
                UpNext.Add(track);

            if (allTracks.Count > 0)
                PlayTrack(allTracks[0]);
        }
        else
        {
            DebugLogger.Info(
                DebugLogger.Category.Queue,
                "TrackEnded.NoNext",
                $"queueCount={UpNext.Count}, historyCount={History.Count}, repeat={RepeatMode}");
            StopAndClear();
        }
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
        var snapshot = _library.Tracks.ToList();
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
    private const int PlayerArtDecodeWidth = 768;

    /// <summary>Bumped on every <see cref="LoadAlbumArt"/> call so a slow background
    /// decode that finishes after the track changed again is discarded.</summary>
    private int _albumArtGeneration;

    private void LoadAlbumArt(Track track)
    {
        // Bitmaps come from the shared LRU cache, which owns their lifetime — never
        // dispose them here. The cache de-dupes, so track switches no longer leak.
        var artPath = _persistence.GetArtworkPath(track.AlbumId);
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
            CurrentAnimatedCoverPath = _animatedCovers.Resolve(track);
            return;
        }

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
                return;

            // After PlayTrack() updates CurrentTrack, the single VLC player can still
            // report positions from the outgoing song while it fades/stops. Those old
            // near-end positions must not drive AutoMix for the newly selected track.
            if (msSinceSeek < TrackStartStalePositionGuardMs)
            {
                var expectedSeconds = _lastCommittedSeekTarget.TotalSeconds;
                var maxPlausibleSeconds = expectedSeconds + (msSinceSeek / 1000d) + 4;
                if (latest.TotalSeconds > maxPlausibleSeconds)
                    return;
            }

            // Extended settle: even after the base window, reject positions that are
            // clearly stale (>2s from the seek target). VLC's buffer refill can take
            // longer than SeekSettleWindowMs on some codecs/containers.
            if (msSinceSeek < SeekSettleWindowMs * 2 &&
                _lastCommittedSeekTarget > TimeSpan.Zero &&
                Math.Abs((latest - _lastCommittedSeekTarget).TotalSeconds) > 2.0)
                return;

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

    private bool TryAdvanceForAutoMix(TimeSpan position, TimeSpan duration)
    {
        if (AutoMixTransitionMode == Noctis.Models.AutoMixTransitionMode.Off ||
            _autoMixAdvanceQueued ||
            CurrentTrack == null ||
            UpNext.Count == 0)
            return false;

        if (State != PlaybackState.Playing || DateTime.UtcNow < _autoMixCommitGuardUntilUtc)
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

        if (position < fadeStart)
        {
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
            }
            return false;
        }

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

    // Gapless timing: pre-decode the next track on the standby player well before
    // the end, then advance just inside the final position tick so the handoff in
    // VlcAudioPlayer starts the prepared player with no audible gap. The handoff
    // lead must stay above one position-timer period (100ms) or the end can slip
    // past us into the EndReached path.
    private const double GaplessPrepareLeadSeconds = 8.0;
    private const double GaplessHandoffLeadSeconds = 0.3;

    // AutoMix overlap blend: both tracks play together through the crossover. The blend is
    // triggered AutoMixOverlapSeconds (plus a small margin so the old stops just before its
    // natural end) from the end; the engine holds both at the blend level for that long,
    // then stops the old and rises the new back to full.
    private const int AutoMixOverlapSeconds = 3;
    private const double AutoMixOverlapLeadSeconds = AutoMixOverlapSeconds + 1.0;

    private bool TryAdvanceForGapless(TimeSpan position, TimeSpan duration)
    {
        if (!GaplessEnabled ||
            AutoMixTransitionMode != Noctis.Models.AutoMixTransitionMode.Off ||
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
        Dispatcher.UIThread.Post(() =>
        {
            if (++_consecutivePlaybackErrors >= MaxConsecutivePlaybackErrors)
            {
                DebugLogger.Error(DebugLogger.Category.Playback, "PlaybackError.CascadeStop",
                    $"{_consecutivePlaybackErrors} consecutive playback errors — stopping instead of skipping");
                _consecutivePlaybackErrors = 0;
                StopAndClear();
                return;
            }

            // Skip to next track on error
            if (UpNext.Count > 0)
                AdvanceQueue(QueueAdvanceReason.Error);
            else
                StopAndClear();
        });
    }

    private void OnLibraryUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // If library is now empty, stop playback and clear everything
            if (_library.Tracks.Count == 0)
            {
                StopAndClear();
                return;
            }

            // Clean up UpNext and History FIRST so that if we need to advance,
            // we only advance into tracks that still exist in the library.
            var deletedTracks = UpNext.Where(t => _library.GetTrackById(t.Id) == null).ToList();
            if (deletedTracks.Count > 0)
            {
                CancelAutoMixTransition("queue changed");
                MarkQueueChanged();
            }
            foreach (var track in deletedTracks)
                UpNext.Remove(track);

            var deletedHistory = History.Where(t => _library.GetTrackById(t.Id) == null).ToList();
            foreach (var track in deletedHistory)
                History.Remove(track);

            // Check if current track was deleted
            if (CurrentTrack != null && _library.GetTrackById(CurrentTrack.Id) == null)
            {
                // Current track was deleted, skip to next or stop
                if (UpNext.Count > 0)
                {
                    AdvanceQueue();
                }
                else
                {
                    StopAndClear();
                }
            }

            // Reload album art in case artwork was changed via metadata editor
            if (CurrentTrack != null)
            {
                LoadAlbumArt(CurrentTrack);
                // Force converter-based bindings on CurrentTrack.* to re-evaluate
                OnPropertyChanged(nameof(CurrentTrack));
            }
        });
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
