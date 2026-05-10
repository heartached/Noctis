using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using Avalonia.Media.Imaging;
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
    [ObservableProperty] private AutoMixStrength _autoMixStrength = AutoMixStrength.Balanced;
    [ObservableProperty] private bool _autoMixRemoveSilence = true;
    [ObservableProperty] private bool _autoMixAvoidAlbums = true;
    [ObservableProperty] private bool _autoMixBeatMatch = true;
    [ObservableProperty] private bool _trackTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _artistMarqueeEnabled = true;

    /// <summary>True if there's any content loaded (current track or upcoming tracks in queue).</summary>
    public bool HasContent => CurrentTrack != null || UpNext.Count > 0;

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
    private const int SeekSettleWindowMs = 300; // must be less than VLC's file-caching (500ms)
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
                    var allTracks = _library.Tracks
                        .OrderBy(_ => Random.Shared.Next())
                        .ToList();
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
                // Shuffle the queue, respecting SkipWhenShuffling
                var shuffled = UpNext
                    .Where(t => !t.SkipWhenShuffling)
                    .OrderBy(_ => Random.Shared.Next())
                    .ToList();
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
        var shuffled = album.Tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        ReplaceQueueAndPlay(shuffled, 0);
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
        if (CurrentTrack == null) return;
        _viewAlbumAction?.Invoke(CurrentTrack);
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
        if (!await Views.ConfirmationDialog.ShowAsync("Do you want to remove the selected item from your Library?"))
            return;
        var trackToRemove = CurrentTrack;

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
        }

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
        // Set null BEFORE dispose — the property setter fires PropertyChanged,
        // and the UI must not try to render a disposed bitmap.
        var oldArt = AlbumArt;
        AlbumArt = null;
        oldArt?.Dispose();
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

    // ── Queue state persistence ──────────────────────────────

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
                State = PlaybackState.Stopped; // user must press play

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
        _audioPlayer.Volume = value;
    }

    /// <summary>
    /// Flush the final volume to VLC immediately — call on slider drag-end
    /// so the exact value is applied without waiting for the trailing timer.
    /// </summary>
    public void CommitVolume() => _audioPlayer.CommitVolume();

    partial void OnCurrentTrackChanged(Track? value)
    {
        OnPropertyChanged(nameof(HasContent));
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

        var fraction = Math.Clamp(PositionFraction, 0.0, 1.0);
        var target = _hasPendingSeekTarget
            ? _pendingSeekTarget
            : TimeSpan.FromTicks((long)(Duration.Ticks * fraction));
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

        // Apply per-track volume adjustment
        _audioPlayer.VolumeAdjust = track.VolumeAdjust;

        // Set pending seek position BEFORE Play() so VlcAudioPlayer applies it
        // inside PlayInternal after the media is loaded (avoids race condition).
        long seekMs = -1;
        var autoMixStartMs = Interlocked.Exchange(ref _pendingAutoMixNextStartMs, -1);
        if (autoMixStartMs > 0 && TimeSpan.FromMilliseconds(autoMixStartMs) < track.Duration)
        {
            seekMs = autoMixStartMs;
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

        // Save library to persist play count
        _ = _library.SaveAsync();
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
    /// 800 px gives sharp results at both 40×40 (player bar) and 350×350 (now-playing)
    /// while avoiding the memory cost of full-resolution decodes (often 3000+ px).
    /// </summary>
    private const int PlayerArtDecodeWidth = 800;

    private void LoadAlbumArt(Track track)
    {
        // Dispose the previous bitmap to prevent memory leaks.
        // Each track switch was leaking the old Bitmap.
        var oldArt = AlbumArt;
        AlbumArt = null;
        oldArt?.Dispose();

        var artPath = _persistence.GetArtworkPath(track.AlbumId);
        if (File.Exists(artPath))
        {
            try
            {
                using var stream = File.OpenRead(artPath);
                AlbumArt = Bitmap.DecodeToWidth(stream, PlayerArtDecodeWidth,
                    BitmapInterpolationMode.HighQuality);
            }
            catch
            {
                // Corrupted image file — leave as null
            }
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
                    _naturalEndFallbackTimer?.Dispose();
                    _naturalEndFallbackTimer = null;
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
        if (plan.Duration > TimeSpan.Zero && remaining > plan.Duration)
            return false;

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
        _audioPlayer.SetCrossfade(
            plan.TransitionType is AutoMixTransitionType.SimpleCrossfade or AutoMixTransitionType.BeatMatchedCrossfade or AutoMixTransitionType.SafeFade,
            Math.Max(1, (int)Math.Round(plan.Duration.TotalSeconds)),
            plan.FadeCurve);
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
            false);

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

    private void OnPlaybackError(object? sender, string message)
    {
        DebugLogger.Error(DebugLogger.Category.Playback, "PlaybackError", $"msg={message}, track={CurrentTrack?.Title}");
        Dispatcher.UIThread.Post(() =>
        {
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
