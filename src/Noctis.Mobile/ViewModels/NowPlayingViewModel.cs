using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The phone transport: Core <see cref="PlaybackQueue"/> for order/repeat/shuffle/history,
/// <see cref="IAudioPlayer"/> for sound. The next queued file is handed to
/// <see cref="IAudioPlayer.PrepareNext"/> so the Media3 player can chain it gaplessly; on
/// that automatic transition the player raises TrackEnded and our Play(next) is a no-op
/// restart (the player recognises the path). The queue is saved the moment it changes; on top
/// of that the position alone is checkpointed every <see cref="SaveIntervalSeconds"/>, and the
/// two are restored together, paused, on launch — so process death costs at most five seconds
/// of position without rewriting the queue for it. Player events may arrive on any thread;
/// <c>marshal</c> hops them to the UI thread (tests pass a direct call).
/// </summary>
public sealed partial class NowPlayingViewModel : ObservableObject, IDisposable
{
    private const int SaveIntervalSeconds = 5;
    // Circuit breaker for the error→skip loop (a folder of dead URIs after a revoked grant).
    private const int MaxConsecutiveErrors = 5;

    private readonly IAudioPlayer _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IPlayHistoryService? _history;
    private readonly Action<Action> _marshal;

    private PlaybackQueue _queue = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private bool _gapless = true;
    private int _consecutiveErrors;
    private bool _seeking;
    private int _seekIdleTicks;
    private bool _disposed;
    private readonly IVolumeControl? _volume;
    private bool _syncingVolume;

    public NowPlayingViewModel(IAudioPlayer player, ILibraryService library, IPersistenceService persistence,
        IPlayHistoryService? history = null, Action<Action>? marshal = null, IVolumeControl? volume = null)
    {
        _player = player;
        _library = library;
        _persistence = persistence;
        _history = history;
        _marshal = marshal ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));

        _player.PositionChanged += OnPlayerPosition;
        _player.DurationResolved += OnPlayerDuration;
        _player.TrackEnded += OnPlayerTrackEnded;
        _player.PlaybackError += OnPlayerError;

        _volume = volume;
        if (_volume != null)
        {
            SyncVolume();
            _volume.Changed += OnVolumeChanged;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    private Track? _currentTrack;

    [ObservableProperty] private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction), nameof(ElapsedText), nameof(RemainingText))]
    private TimeSpan _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction), nameof(ElapsedText), nameof(RemainingText))]
    private TimeSpan _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne))]
    private RepeatMode _repeatMode;

    public bool IsRepeatOn => RepeatMode != RepeatMode.Off;
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;

    [ObservableProperty] private bool _isShuffleEnabled;
    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>Mirror of the queue's UpNext for the Queue page; rebuilt with one Reset per change.</summary>
    public BulkObservableCollection<Track> UpNext { get; } = new();

    public bool HasUpNext => UpNext.Count > 0;

    public bool HasTrack => CurrentTrack != null;

    /// <summary>
    /// Whether <see cref="NextCommand"/> would land on a track, for the media notification's
    /// Next button (Android reads this off a Java binder thread, so it must stay a couple of
    /// field reads — no Avalonia, no allocation, no locking). Mirrors
    /// <see cref="PlaybackQueue.Advance"/> with <see cref="QueueAdvance.UserSkip"/>: the head
    /// of UpNext, or a Repeat All wrap. The wrap is approximated by "a track is loaded"
    /// because the cycle PlaybackQueue replays is recorded by ReplaceAll — the only way the
    /// phone ever starts a queue — and is not otherwise observable from here. Erring towards
    /// enabled is the cheap direction: an enabled button that stops is far milder than a
    /// hidden button for a skip that would have worked.
    /// </summary>
    public bool HasNext => _queue.UpNext.Count > 0 || (RepeatMode == RepeatMode.All && CurrentTrack != null);

    /// <summary>
    /// Whether <see cref="PreviousCommand"/> would do something, for the notification's
    /// Previous button. True whenever a track is loaded: past three seconds Previous restarts
    /// the current track, and before that <see cref="PlaybackQueue.Back"/> steps into history
    /// or — with history empty — returns Current and the restart happens anyway. Only a
    /// stopped, empty queue has nothing to go back to. Same thread caveat as
    /// <see cref="HasNext"/>.
    /// </summary>
    public bool HasPrevious => CurrentTrack != null;

    /// <summary>0..1 for the seek bar.</summary>
    public double ProgressFraction => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;

    public string ElapsedText => FormatTime(Position);

    /// <summary>The right-hand seek label, counting down as in the mockup ("-2:04").</summary>
    public string RemainingText => "-" + FormatTime(Duration > Position ? Duration - Position : TimeSpan.Zero);

    /// <summary>mm:ss, or h:mm:ss past an hour (the previous labels' format, so 1:30 still reads "01:30").</summary>
    public static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>The device media volume, 0..1 (see IVolumeControl); inert without one.</summary>
    [ObservableProperty] private double _volumeLevel;

    public bool HasVolumeControl => _volume != null;

    partial void OnVolumeLevelChanged(double value)
    {
        if (_syncingVolume || _volume == null) return;
        _volume.Level = Math.Clamp(value, 0, 1);
    }

    private void OnVolumeChanged(object? sender, EventArgs e) => _marshal(() =>
    {
        if (!_disposed) SyncVolume();
    });

    private void SyncVolume()
    {
        _syncingVolume = true;
        VolumeLevel = _volume!.Level;
        _syncingVolume = false;
    }

    public void SetGapless(bool enabled)
    {
        _gapless = enabled;
        _player.SetGapless(enabled);
        PrepareUpcoming();
    }

    /// <summary>Replace the queue with <paramref name="tracks"/> and start at <paramref name="startIndex"/>.</summary>
    public void PlayTracks(IReadOnlyList<Track> tracks, int startIndex)
    {
        // ReplaceAll returns the still-playing Current (not null) for an empty list, so
        // without this guard tapping Play on an empty/filtered-to-nothing list would
        // restart the currently playing track from zero.
        if (tracks.Count == 0) return;
        var first = _queue.ReplaceAll(tracks, startIndex);
        IsShuffleEnabled = _queue.IsShuffleEnabled;
        if (first == null) return;
        // A new queue is a new chance: a stale failure streak from a previous queue must
        // not immediately trip the breaker on this one's very first track.
        _consecutiveErrors = 0;
        StartTrack(first, fromPosition: null);
    }

    /// <summary>
    /// Shuffle buttons on lists, albums and artists: start on a random track and shuffle the
    /// rest. The list is rotated so the start is first before <see cref="PlaybackQueue.ReplaceAll"/>
    /// (which queues only the tracks after the start index); the unshuffled order Shuffle-off
    /// restores is then the list's own order from that track, wrapping round.
    /// </summary>
    public void PlayShuffled(IReadOnlyList<Track> tracks, Random? rng = null)
    {
        if (tracks.Count == 0) return;
        rng ??= Random.Shared;
        var start = rng.Next(tracks.Count);
        var rotated = tracks.Skip(start).Concat(tracks.Take(start)).ToList();
        var first = _queue.ReplaceAll(rotated, 0);
        _queue.SetShuffle(true, rng);
        IsShuffleEnabled = true;
        if (first == null) return;
        _consecutiveErrors = 0;
        StartTrack(first, fromPosition: null);
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (CurrentTrack == null) return;
        switch (_player.State)
        {
            case PlaybackState.Playing:
                _player.Pause();
                IsPlaying = false;
                SaveStateNow();
                break;
            case PlaybackState.Paused:
                _player.Resume();
                IsPlaying = true;
                break;
            default:
                // Stopped: a queue restored after process death (or a finished queue) — start
                // the loaded track from the position we are showing. A deliberate user action
                // always clears the error streak, even one the breaker itself just stopped.
                _consecutiveErrors = 0;
                StartTrack(CurrentTrack, fromPosition: Position);
                break;
        }
    }

    [RelayCommand]
    private void Next()
    {
        // A deliberate skip is a user action, not part of the automatic error cascade the
        // breaker bounds — clear the streak so an unrelated later failure gets its own count.
        _consecutiveErrors = 0;
        var next = _queue.Advance(QueueAdvance.UserSkip);
        if (next == null) StopPlayback(); else StartTrack(next, null);
    }

    [RelayCommand]
    private void Previous()
    {
        if (CurrentTrack != null && Position > TimeSpan.FromSeconds(3))
        {
            Seek(TimeSpan.Zero);
            return;
        }
        var previous = _queue.Back();
        if (previous != null) StartTrack(previous, null);
    }

    /// <summary>
    /// The user grabbed the seek thumb. Suspends the position push until <see cref="EndSeek"/>:
    /// the player ticks four times a second, and each tick republishes <see cref="Position"/>
    /// into the Slider's Value, overwriting the value the drag put there (Slider writes its own
    /// Value with SetCurrentValue, which a binding update beats). Without this the thumb snaps
    /// back to the real playback position mid-drag and the release seeks to wherever the last
    /// tick landed — intermittently, since a short drag between ticks works fine.
    /// </summary>
    public void BeginSeek()
    {
        _seeking = true;
        _seekIdleTicks = 0;
    }

    /// <summary>
    /// The gesture is still alive — called for every pointer move over the seek bar, which is
    /// what keeps a slow deliberate drag from tripping the watchdog below.
    /// </summary>
    public void KeepSeekAlive() => _seekIdleTicks = 0;

    /// <summary>The drag is over (or was cancelled): let position ticks move the thumb again.
    /// Idempotent, so a PointerCaptureLost after a normal release is harmless.</summary>
    public void EndSeek() => _seeking = false;

    /// <summary>
    /// Position ticks a held seek may survive without any pointer activity before it cancels
    /// itself — thirty seconds at the Android player's 4 Hz poll. Android device run,
    /// 2026-09-22: a drag abandoned by pulling the notification shade down over it delivers
    /// neither a release nor a capture-lost — no further pointer event of any kind reaches the
    /// app — so no handler can end the seek, and the elapsed label, the thumb and the position
    /// written into queue.json all freeze while playback carries on. This is the only escape
    /// that does not depend on an input event the app may never get.
    ///
    /// The window is deliberately far longer than a gesture, because firing during a *live*
    /// gesture re-creates the exact bug <see cref="BeginSeek"/> exists to prevent: clearing
    /// _seeking lets the same tick push live playback position into the Slider's Value, so the
    /// thumb jumps out from under the finger and the release seeks to a stale value. Android
    /// emits no ACTION_MOVE for a stationary finger, so a press-and-hold while deciding where
    /// to drop the thumb produces no keepalive at all — at the original three seconds that was
    /// well inside human range. The defect being bounded here was an *unbounded* freeze, so
    /// what matters is that it ends unattended, not that it ends quickly.
    /// </summary>
    private const int SeekWatchdogTicks = 120;

    public void Seek(TimeSpan position)
    {
        if (CurrentTrack == null) return;
        Position = position;
        if (_player.State != PlaybackState.Stopped)
            _player.Seek(position);
        // While paused there are no position ticks to carry this to disk on the usual
        // 5-second cadence, so a seek made just before process death would otherwise be lost.
        if (_player.State != PlaybackState.Playing)
            SaveStateNow();
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        _queue.RepeatMode = RepeatMode;
        PrepareUpcoming();
        SaveStateNow();
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        _queue.SetShuffle(!_queue.IsShuffleEnabled);
        IsShuffleEnabled = _queue.IsShuffleEnabled;
        QueueChanged();
    }

    public void PlayNext(Track track) { _queue.AddNext(track); QueueChanged(); }
    public void AddToQueue(Track track) { _queue.Add(track); QueueChanged(); }
    public void RemoveFromQueue(int upNextIndex) { _queue.RemoveAt(upNextIndex); QueueChanged(); }
    public void MoveInQueue(int fromUpNextIndex, int toUpNextIndex) { _queue.Move(fromUpNextIndex, toUpNextIndex); QueueChanged(); }

    /// <summary>The Queue page's Clear: Up Next empties, the current track keeps playing.</summary>
    [RelayCommand]
    private void ClearUpNext()
    {
        _queue.Clear();
        QueueChanged();
    }

    /// <summary>"Play Next" for several tracks (an album or playlist from the long-press sheet):
    /// in their order at the front of Up Next, one save. With nothing loaded there is no "next"
    /// to insert before, so the tracks simply play.</summary>
    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (CurrentTrack == null) { PlayTracks(tracks, 0); return; }
        for (var i = tracks.Count - 1; i >= 0; i--) _queue.AddNext(tracks[i]);
        QueueChanged();
    }

    /// <summary>"Add to Queue" for several tracks; with nothing loaded they simply play.</summary>
    public void AddToQueue(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        if (CurrentTrack == null) { PlayTracks(tracks, 0); return; }
        _queue.AddRange(tracks);
        QueueChanged();
    }

    private void QueueChanged()
    {
        SyncUpNext();
        PrepareUpcoming();
        SaveStateNow();
    }

    private void StartTrack(Track track, TimeSpan? fromPosition)
    {
        // Before CurrentTrack changes: its PropertyChanged refreshes the Library Shelf and Home
        // rows from the play log, which must already hold this play.
        _history?.RecordPlay(track);
        CurrentTrack = track;
        Position = fromPosition ?? TimeSpan.Zero;
        Duration = track.Duration;
        ErrorText = string.Empty;
        _player.PendingSeekMs = fromPosition is { } p && p > TimeSpan.Zero ? (long)p.TotalMilliseconds : -1;
        _player.Play(track.FilePath);
        IsPlaying = true;
        SyncUpNext();
        PrepareUpcoming();
        SaveStateNow();
    }

    private void PrepareUpcoming()
    {
        // Repeat-one: the current track is its own successor (PlaybackQueue.Advance returns
        // Current for RepeatMode.One + Natural), so ExoPlayer must not chain past it. Without
        // this, PrepareUpcoming would gaplessly queue UpNext[0] behind the looping track; on
        // every natural end ExoPlayer auto-advances into that queued item and its audio
        // reaches the speaker before TrackEnded fires and our Play(current) restarts the loop
        // — an audible blip of the next track on every repeat.
        if (!_gapless || RepeatMode == RepeatMode.One || _queue.UpNext.Count == 0 || CurrentTrack == null)
        {
            _player.CancelPreparedNext();
            return;
        }
        _player.PrepareNext(_queue.UpNext[0].FilePath);
    }

    private void StopPlayback()
    {
        _player.Stop();
        IsPlaying = false;
        Position = TimeSpan.Zero;
        SyncUpNext();
        SaveStateNow();
    }

    private void SyncUpNext()
    {
        UpNext.ReplaceAll(_queue.UpNext);
        OnPropertyChanged(nameof(HasUpNext));
    }

    private void OnPlayerPosition(object? sender, TimeSpan position) => _marshal(() =>
    {
        if (_disposed) return;
        // Lock-screen pause, audio-focus loss and headphone unplug pause the engine
        // without going through TogglePlayPause; the tick is where the UI catches up.
        var playing = _player.State == PlaybackState.Playing;
        // Everything else in this tick still runs mid-drag — the engine keeps playing, so the
        // IsPlaying resync and the save cadence must not stall while the thumb is held.
        // Armed only while the engine is actually advancing: the freeze this bounds is a dead
        // display over live playback, and an externally paused engine has nothing to un-freeze,
        // so counting ticks there would only put a careful scrub made while paused-by-lock-screen
        // on the same clock for no benefit.
        if (_seeking && playing && ++_seekIdleTicks > SeekWatchdogTicks) EndSeek();
        if (!_seeking) Position = position;
        IsPlaying = playing;
        // Only a tick that arrives while actually playing proves the stream is healthy;
        // a timer-driven poll firing while paused/stopped must not clear a live streak.
        if (playing) _consecutiveErrors = 0;
        // Position only: the queue *structure* is written by its own mutators, so rewriting it
        // here would re-serialize and fsync the whole queue every five seconds for a value
        // that did not change. See SavePositionNow.
        if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= SaveIntervalSeconds)
            SavePositionNow();
    });

    private void OnPlayerDuration(object? sender, TimeSpan duration) => _marshal(() =>
    {
        if (_disposed) return;
        Duration = duration;
    });

    private void OnPlayerTrackEnded(object? sender, EventArgs e) => _marshal(() =>
    {
        if (_disposed) return;
        var next = _queue.Advance(QueueAdvance.Natural);
        if (next == null) StopPlayback(); else StartTrack(next, null);
    });

    private void OnPlayerError(object? sender, string message) => _marshal(() =>
    {
        if (_disposed) return;
        DebugLog.Write("Audio", $"Playback error: {message} — track: {CurrentTrack?.Title ?? "(none)"}");
        ErrorText = message;
        if (++_consecutiveErrors >= MaxConsecutiveErrors)
        {
            StopPlayback();
            return;
        }
        var next = _queue.Advance(QueueAdvance.UserSkip);
        if (next == null) StopPlayback(); else StartTrack(next, null);
    });

    /// <summary>Queue + position → queue.json (the desktop's QueueState shape).</summary>
    public Task SaveStateAsync()
    {
        var snapshot = _queue.Snapshot();
        var state = new QueueState
        {
            CurrentTrackId = snapshot.CurrentId,
            PositionSeconds = Position.TotalSeconds,
            UpNextIds = snapshot.UpNextIds.ToList(),
            HistoryIds = snapshot.HistoryIds.ToList(),
            RepeatCycleIds = snapshot.RepeatCycleIds.ToList(),
            RepeatMode = snapshot.RepeatMode,
            IsShuffleEnabled = snapshot.IsShuffleEnabled,
            IsMuted = _player.IsMuted,
            OriginalOrderIds = snapshot.OriginalOrderIds.ToList(),
        };
        return _persistence.SaveQueueStateAsync(state);
    }

    private void SaveStateNow()
    {
        _lastSaveUtc = DateTime.UtcNow;
        _ = SaveStateAsync().ContinueWith(
            t => DebugLog.Write("Queue", $"Save failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// The periodic checkpoint: two numbers, not the queue. Every genuine mutator — track
    /// change, queue edit, repeat/shuffle change, pause, seek-while-paused — already calls
    /// <see cref="SaveStateNow"/> the moment it happens, so between them the only thing that
    /// moves is the position, and re-serializing the queue for it wrote hundreds of KB (a
    /// queue started from the library holds the whole library) through an fsync every five
    /// seconds — on the order of 260 MB per hour of playback onto phone flash. The five-second
    /// resume guarantee is unchanged: the position still reaches disk on the same cadence, and
    /// PersistenceService folds the checkpoint back into the queue it loads.
    /// </summary>
    private void SavePositionNow()
    {
        _lastSaveUtc = DateTime.UtcNow;
        _ = _persistence.SaveQueuePositionAsync(CurrentTrack?.Id, Position.TotalSeconds).ContinueWith(
            t => DebugLog.Write("Queue", $"Position save failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Cold start: bring the queue back paused at the saved position. Never auto-plays.</summary>
    public async Task RestoreStateAsync()
    {
        var state = await _persistence.LoadQueueStateAsync();
        if (state == null) return;

        _queue = PlaybackQueue.Restore(
            new PlaybackQueueState(state.CurrentTrackId, state.UpNextIds, state.HistoryIds, state.RepeatCycleIds,
                state.RepeatMode, state.IsShuffleEnabled, state.OriginalOrderIds),
            id => _library.GetTrackById(id));
        RepeatMode = state.RepeatMode;
        IsShuffleEnabled = state.IsShuffleEnabled;
        CurrentTrack = _queue.Current;
        Duration = _queue.Current?.Duration ?? TimeSpan.Zero;
        Position = _queue.Current != null ? TimeSpan.FromSeconds(Math.Max(0, state.PositionSeconds)) : TimeSpan.Zero;
        IsPlaying = false;
        SyncUpNext();
    }

    public void Dispose()
    {
        _disposed = true;
        _player.PositionChanged -= OnPlayerPosition;
        _player.DurationResolved -= OnPlayerDuration;
        _player.TrackEnded -= OnPlayerTrackEnded;
        _player.PlaybackError -= OnPlayerError;
        if (_volume != null) _volume.Changed -= OnVolumeChanged;
    }
}
