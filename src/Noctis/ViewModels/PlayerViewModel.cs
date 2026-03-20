using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // ── Observable properties bound to the playback bar ──

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private PlaybackState _state = PlaybackState.Stopped;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private double _positionFraction; // 0.0 – 1.0 for slider
    [ObservableProperty] private int _volume = 75;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private Bitmap? _albumArt;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _remainingTimeText = "0:00";
    [ObservableProperty] private bool _isShuffleEnabled;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.Off;
    [ObservableProperty] private bool _isQueuePopupOpen;
    [ObservableProperty] private bool _trackTitleMarqueeEnabled = true;
    [ObservableProperty] private bool _artistMarqueeEnabled = true;

    /// <summary>True if there's any content loaded (current track or upcoming tracks in queue).</summary>
    public bool HasContent => CurrentTrack != null || UpNext.Count > 0;

    // ── Queue ──

    /// <summary>Upcoming tracks to play.</summary>
    public ObservableCollection<Track> UpNext { get; } = new();

    /// <summary>Previously played tracks (most recent first).</summary>
    public ObservableCollection<Track> History { get; } = new();

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
    private System.Threading.Timer? _seekDebounceTimer; // debounce timer for rapid seek clicks
    private volatile bool _positionUpdateQueued; // coalesces rapid VLC position dispatches
    private TimeSpan _latestVlcPosition; // latest position from VLC timer (written from timer thread)
    private List<Track> _originalQueue = new(); // stored when shuffle is enabled
    private Action<string>? _navigateAction; // injected navigation action
    private Action<Track>? _viewAlbumAction; // injected from MainWindowViewModel
    private Action<Track>? _searchLyricsAction; // injected for search lyrics navigation
    private SidebarViewModel? _sidebar; // injected for playlist access
    private bool _suppressHasContentNotify; // prevents layout thrashing during batch queue updates
    private bool _isAdvancingQueue; // re-entrancy guard for AdvanceQueue

    public PlayerViewModel(IAudioPlayer audioPlayer, ILibraryService library, IPersistenceService persistence)
    {
        _audioPlayer = audioPlayer;
        _library = library;
        _persistence = persistence;

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
        if (UpNext.Count == 0) return;
        AdvanceQueue();
    }

    [RelayCommand]
    private void Previous()
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "Previous", $"pos={Position.TotalSeconds:F1}s, historyLen={History.Count}");
        if (Position.TotalSeconds > 3)
        {
            // Restart current track
            _audioPlayer.Seek(TimeSpan.Zero);
            _lastSeekTime = DateTime.UtcNow;
            Position = TimeSpan.Zero;
            PositionFraction = 0;
            PositionText = "0:00";
            RemainingTimeText = FormatTime(Duration);
            Seeked?.Invoke(this, TimeSpan.Zero);
        }
        else if (History.Count > 0)
        {
            GoBackInQueue();
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
        _audioPlayer.Seek(target);
        _lastSeekTime = DateTime.UtcNow;
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
        IsShuffleEnabled = !IsShuffleEnabled;
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
                UpNext.Clear();
                foreach (var track in shuffled)
                    UpNext.Add(track);
            }
            else if (_originalQueue.Count > 0)
            {
                // Restore original queue order
                UpNext.Clear();
                foreach (var track in _originalQueue)
                    UpNext.Add(track);
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
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.Off
        };
    }

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
        if (!await Views.ConfirmationDialog.ShowAsync($"Remove \"{CurrentTrack.Title}\" from your library?"))
            return;
        var trackToRemove = CurrentTrack;

        // Advance to next track or stop playback
        if (UpNext.Count > 0)
            AdvanceQueue();
        else
        {
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
        UpNext.Clear();
        for (int i = startIndex + 1; i < tracks.Count; i++)
        {
            UpNext.Add(tracks[i]);
        }

        // Play the selected track
        PlayTrack(tracks[startIndex]);
    }

    /// <summary>Inserts a track at the front of the UpNext queue ("Play Next").</summary>
    public void AddNext(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "AddNext", $"track={track.Title}");
        UpNext.Insert(0, track);
    }

    /// <summary>Appends a track to the end of the UpNext queue ("Add to Queue").</summary>
    public void AddToQueue(Track track)
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "AddToQueue", $"track={track.Title}, newLen={UpNext.Count + 1}");
        UpNext.Add(track);
    }

    /// <summary>Removes a track from the UpNext queue by index.</summary>
    public void RemoveFromQueue(int index)
    {
        if (index >= 0 && index < UpNext.Count)
        {
            DebugLogger.Info(DebugLogger.Category.Queue, "RemoveFromQueue", $"idx={index}, track={UpNext[index].Title}");
            UpNext.RemoveAt(index);
        }
    }

    /// <summary>Clears all upcoming tracks.</summary>
    public void ClearQueue()
    {
        DebugLogger.Info(DebugLogger.Category.Queue, "ClearQueue", $"cleared={UpNext.Count} tracks");
        UpNext.Clear();
    }

    /// <summary>Stops playback and clears all queue data.</summary>
    public void StopAndClear()
    {
        _hasPendingSeekTarget = false;
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

        var track = UpNext[fromIndex];
        UpNext.RemoveAt(fromIndex);
        UpNext.Insert(toIndex, track);
    }

    /// <summary>Plays the track at the given index in UpNext, discarding prior queue items.</summary>
    public void PlayFromUpNextAt(int index)
    {
        if (index < 0 || index >= UpNext.Count) return;
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
        foreach (var id in state.HistoryIds)
        {
            var track = _library.GetTrackById(id);
            if (track != null) History.Add(track);
        }

        // Restore up-next
        foreach (var id in state.UpNextIds)
        {
            var track = _library.GetTrackById(id);
            if (track != null) UpNext.Add(track);
        }

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
        _hasPendingSeekTarget = false;
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

        _audioPlayer.Play(track.FilePath);

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

    private void AdvanceQueue()
    {
        // Re-entrancy guard — if TrackEnded fires twice (VLC race), ignore the second.
        if (_isAdvancingQueue) return;
        _isAdvancingQueue = true;
        try
        {
            AdvanceQueueCore();
        }
        finally
        {
            _isAdvancingQueue = false;
        }
    }

    private void AdvanceQueueCore()
    {
        // Handle repeat one mode — replay via PlayTrack() so PlayCount is
        // incremented, TrackStarted fires, and all state updates properly.
        if (RepeatMode == RepeatMode.One && CurrentTrack != null)
        {
            PlayTrack(CurrentTrack);
            return;
        }

        if (CurrentTrack != null)
        {
            History.Insert(0, CurrentTrack);
            TrimHistory();
        }

        if (UpNext.Count > 0)
        {
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
            // End of queue with repeat off — fully stop and clear all state.
            // CurrentTrack must be nulled BEFORE State=Stopped so that no
            // stale PlayPause path can see Stopped+CurrentTrack and replay.
            _audioPlayer.Stop();
            CurrentTrack = null;
            State = PlaybackState.Stopped;

            // Dispose album art and reset position/duration to prevent any
            // stale UI state from persisting after the player bar hides.
            var oldArt = AlbumArt;
            AlbumArt = null;
            oldArt?.Dispose();
            Position = TimeSpan.Zero;
            PositionFraction = 0;
            PositionText = "0:00";
            Duration = TimeSpan.Zero;
            DurationText = "0:00";
            RemainingTimeText = "0:00";
        }
    }

    private void GoBackInQueue()
    {
        if (History.Count == 0) return;

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
    }

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

        });
    }

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        DebugLogger.Info(DebugLogger.Category.Playback, "TrackEnded", $"track={CurrentTrack?.Title}");
        Dispatcher.UIThread.Post(() => AdvanceQueue());
    }

    private void OnPlaybackError(object? sender, string message)
    {
        DebugLogger.Error(DebugLogger.Category.Playback, "PlaybackError", $"msg={message}, track={CurrentTrack?.Title}");
        Dispatcher.UIThread.Post(() =>
        {
            // Skip to next track on error
            if (UpNext.Count > 0)
                AdvanceQueue();
            else
                State = PlaybackState.Stopped;
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

            // Clean up UpNext queue - remove any deleted tracks
            var deletedTracks = UpNext.Where(t => _library.GetTrackById(t.Id) == null).ToList();
            foreach (var track in deletedTracks)
            {
                UpNext.Remove(track);
            }

            // Clean up History - remove any deleted tracks
            var deletedHistory = History.Where(t => _library.GetTrackById(t.Id) == null).ToList();
            foreach (var track in deletedHistory)
            {
                History.Remove(track);
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

