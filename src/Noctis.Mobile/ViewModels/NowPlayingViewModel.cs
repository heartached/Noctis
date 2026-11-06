using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

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

    internal static string ComputerUnreachableSkipping => Loc.T("Mobile.Player.UnreachableSkipping");
    internal static string ComputerUnreachableStopped => Loc.T("Mobile.Player.UnreachableStopped");
    internal static string TooManyErrors => Loc.T("Mobile.Player.TooManyErrors");

    private readonly IAudioPlayer _player;
    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly IPlayHistoryService? _history;
    private readonly Action<Action> _marshal;

    private PlaybackQueue _queue = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private int _consecutiveErrors;
    // Where the current track was started from: only a tick past it proves audio is moving.
    private TimeSpan _startedFrom;
    // A song that needs the computer failed to connect: until the user picks such a song again,
    // automatic advances and Next pass over the others instead of each waiting out a timeout.
    private bool _computerUnreachable;
    private bool _seeking;
    private int _seekIdleTicks;
    private bool _disposed;
    // The started track whose play is not in the log yet, and the position it started from.
    // See OnPlayerPosition: a track counts as played once its audio moves past that start.
    private Track? _unrecordedPlay;
    private TimeSpan _unrecordedFrom;
    // How long the current song has actually been heard (ticks of moving audio, a seek or a
    // stall is not listening), and the song whose play is in the log to credit it to.
    private TimeSpan _listened;
    private TimeSpan _listenAnchor;
    private Track? _listenTrack;
    private TimeSpan _flushedListened = TimeSpan.MinValue;
    // A bigger forward step between two ticks is a jump, not listening.
    private const double MaxListenStepSeconds = 2;

    /// <summary>Settings > Player > Count a play after: how much of a song (percent) must be heard
    /// before it is in the play log, 0 = as soon as it plays. Read per tick; null = 0.</summary>
    public Func<int>? PlayCountThresholdPercent { get; set; }
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
        _library.LibraryUpdated += OnLibraryUpdated;
        // The phone always plays gaplessly (owner, 10-11): there is no switch, and a desktop
        // settings.json saying otherwise is ignored, so the player is put in gapless mode
        // here, before the saved queue or any track can reach it.
        _player.SetGapless(true);

        _volume = volume;
        if (_volume != null)
        {
            SyncVolume();
            _volume.Changed += OnVolumeChanged;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(Quality), nameof(HasQuality))]
    private Track? _currentTrack;

    /// <summary>Now Playing's quality badge: the playing file's class (<see cref="TrackQuality"/>),
    /// null (no badge) for a file with no codec to name. Changes only with the track.</summary>
    public TrackQuality? Quality => TrackQuality.Of(CurrentTrack);

    public bool HasQuality => Quality != null;

    [ObservableProperty] private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction), nameof(ElapsedText), nameof(RemainingText))]
    private TimeSpan _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction), nameof(ElapsedText), nameof(RemainingText))]
    private TimeSpan _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(IsRepeatOne), nameof(HasNext))]
    private RepeatMode _repeatMode;

    public bool IsRepeatOn => RepeatMode != RepeatMode.Off;
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;

    [ObservableProperty] private bool _isShuffleEnabled;
    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>
    /// A short message for the shell's banner, wherever the user is (the error line only shows
    /// on Now Playing). Empty when there is none; the view dismisses it after a few seconds.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    public bool HasNotice => Notice.Length > 0;

    public void DismissNotice() => Notice = string.Empty;

    /// <summary>
    /// Whether a song plays only from the computer (a desktop song that is not downloaded).
    /// Null without an account: then no song does.
    /// </summary>
    public Func<Track, bool>? NeedsComputer { get; set; }

    /// <summary>Mirror of the queue's UpNext for the Queue page; rebuilt with one Reset per change.</summary>
    public BulkObservableCollection<Track> UpNext { get; } = new();

    public bool HasUpNext => UpNext.Count > 0;

    public bool HasTrack => CurrentTrack != null;

    /// <summary>What the queue was started from (an album, playlist or artist name, "Songs",
    /// "Search"...), as passed to <see cref="PlayTracks"/>; null when the caller gave none.
    /// Saved with the queue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceLabel), nameof(PlayingFromText))]
    private string? _sourceLabel;

    public bool HasSourceLabel => !string.IsNullOrEmpty(SourceLabel);

    /// <summary>The UI language changed: "Playing from …" and "Track 3 of 12" re-read.</summary>
    public void Relabel()
    {
        OnPropertyChanged(nameof(PlayingFromText));
        SyncUpNext();
    }

    public string PlayingFromText => HasSourceLabel ? Loc.T("Mobile.Player.PlayingFrom", SourceLabel!) : string.Empty;

    /// <summary>"Track 3 of 12": the current track's place in this queue, counting the tracks
    /// added to it since. Empty when stopped or when the place is unknown (see
    /// <see cref="PlaybackQueue.PlayedInQueue"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueuePosition))]
    private string _queuePositionText = string.Empty;

    public bool HasQueuePosition => QueuePositionText.Length > 0;

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

    /// <summary>Replace the queue with <paramref name="tracks"/> and start at <paramref name="startIndex"/>.
    /// <paramref name="source"/> is what the Queue sheet says it plays from (<see cref="SourceLabel"/>).</summary>
    public void PlayTracks(IReadOnlyList<Track> tracks, int startIndex, string? source = null)
    {
        // ReplaceAll returns the still-playing Current (not null) for an empty list, so
        // without this guard tapping Play on an empty/filtered-to-nothing list would
        // restart the currently playing track from zero.
        if (tracks.Count == 0) return;
        var first = _queue.ReplaceAll(tracks, startIndex);
        SourceLabel = source;
        IsShuffleEnabled = _queue.IsShuffleEnabled;
        if (first == null) return;
        // A new queue is a new chance: a stale failure streak from a previous queue must
        // not immediately trip the breaker on this one's very first track. A song the user
        // picks is tried even if the computer was unreachable a moment ago: it may be back.
        _consecutiveErrors = 0;
        _computerUnreachable = false;
        StartTrack(first, fromPosition: null);
    }

    /// <summary>
    /// Shuffle buttons on lists, albums and artists: start on a random track and shuffle the
    /// rest. The list is rotated so the start is first before <see cref="PlaybackQueue.ReplaceAll"/>
    /// (which queues only the tracks after the start index); the unshuffled order Shuffle-off
    /// restores is then the list's own order from that track, wrapping round.
    /// </summary>
    public void PlayShuffled(IReadOnlyList<Track> tracks, Random? rng = null, string? source = null)
    {
        if (tracks.Count == 0) return;
        rng ??= Random.Shared;
        var start = rng.Next(tracks.Count);
        var rotated = tracks.Skip(start).Concat(tracks.Take(start)).ToList();
        var first = _queue.ReplaceAll(rotated, 0);
        SourceLabel = source;
        _queue.SetShuffle(true, rng);
        IsShuffleEnabled = true;
        if (first == null) return;
        _consecutiveErrors = 0;
        _computerUnreachable = false;
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
                _computerUnreachable = false;
                // A queue that ran out keeps its last track for the mini bar only (the queue has
                // none current, it is the newest in History): take it back, or Previous restarted
                // it and the queue saved meanwhile had no song to restore.
                if (_queue.Current == null && _queue.History.Count > 0 && ReferenceEquals(_queue.History[0], CurrentTrack))
                    _queue.Back();
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
        var next = AdvancePlayable(QueueAdvance.UserSkip, out var heldBack);
        if (next != null) StartTrack(next, null);
        else if (heldBack) Notice = ComputerUnreachableStopped;   // the song playing keeps playing
        else StopPlayback();
    }

    /// <summary>
    /// <see cref="PlaybackQueue.Advance"/>, passing over songs that need the computer while it is
    /// unreachable (<see cref="_computerUnreachable"/>). When only such songs are left the queue
    /// is put back as it was, null is returned and <paramref name="heldBack"/> is set, so the
    /// queue still holds them for when the computer is back.
    /// </summary>
    private Track? AdvancePlayable(QueueAdvance reason, out bool heldBack)
    {
        heldBack = false;
        if (!_computerUnreachable || NeedsComputer is not { } needs) return _queue.Advance(reason);
        var before = _queue.Clone();
        // Through Up Next, then at most once round a Repeat All cycle (the recorded one, or
        // History when none was recorded), so an all-desktop Repeat All queue cannot spin.
        var snapshot = before.Snapshot();
        var budget = snapshot.UpNextIds.Count + Math.Max(snapshot.RepeatCycleIds.Count, snapshot.HistoryIds.Count) + 1;
        var next = _queue.Advance(reason);
        var passedOver = false;
        for (var i = 0; next != null && needs(next) && i < budget; i++)
        {
            passedOver = true;
            next = _queue.Advance(QueueAdvance.UserSkip);
        }
        if (next != null && !needs(next)) return next;
        // Still on such a song when the budget ran out, or the queue ended after passing over
        // some. A queue that simply ended (nothing passed over) ends as it always has.
        if (next != null || passedOver)
        {
            heldBack = true;
            _queue = before;
        }
        return null;
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
    /// The song a swipe left would bring in (the mini player's and the cover's, TrackSwipeGesture):
    /// exactly what <see cref="NextCommand"/> would start, worked out on a copy of the queue, so
    /// Up Next, a Repeat All wrap and songs passed over while the computer is out of reach all
    /// count. Null where Next would stop: the swipe then rubber-bands and does nothing.
    /// </summary>
    public Track? PeekNext()
    {
        var saved = _queue;
        _queue = saved.Clone();
        try
        {
            return AdvancePlayable(QueueAdvance.UserSkip, out _);
        }
        finally
        {
            _queue = saved;
        }
    }

    /// <summary>The song a swipe right would bring back: the one played before this. Unlike the
    /// Previous button a swipe never restarts the song it slid away from, so with nothing before
    /// it (or a queue that ran out, whose last song is only kept for the bar) it is null.</summary>
    public Track? PeekPrevious() =>
        _queue.Current != null && _queue.History.Count > 0 ? _queue.History[0] : null;

    /// <summary>A swipe right let go past its threshold: the song before, from its start.</summary>
    public void SkipToPrevious()
    {
        if (PeekPrevious() == null) return;
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

    /// <summary>A held scrub is steering <see cref="Position"/> (the lyrics follow it verbatim).</summary>
    public bool IsSeeking => _seeking;

    /// <summary>
    /// Mid-drag: shows <paramref name="position"/> in the elapsed and remaining times without
    /// seeking (the release does that). Held position ticks no longer move the times, so
    /// without this both froze at the drag's start (device, 10-13) and there was no telling
    /// where the release would land. Ignored outside a held seek, so a value pushed into the
    /// bar by anything else never moves the times.
    /// </summary>
    public void PreviewSeek(TimeSpan position)
    {
        if (!_seeking || CurrentTrack == null) return;
        _seekIdleTicks = 0;
        Position = position;
    }

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

    /// <summary>
    /// A signed-out desktop's songs leave the library, but the queue holds Track objects, so
    /// the mini player kept offering one that could no longer play. Only desktop songs are
    /// pruned: a local file missing from the library for a moment (mid-rescan) keeps its place.
    /// </summary>
    private void OnLibraryUpdated(object? sender, EventArgs e) => _marshal(() =>
    {
        if (_disposed) return;
        static bool Desktop(Track t) => t.SourceType == SourceType.NoctisServer;
        var queued = _queue.History.Concat(_queue.UpNext).Append(_queue.Current).Where(t => t != null && Desktop(t)).ToList();
        if (queued.Count == 0) return;
        Track[] tracks;
        try { tracks = _library.Tracks.ToArray(); }
        catch (InvalidOperationException) { return; }   // raced a library write; the next update retries
        var present = new HashSet<Guid>(tracks.Where(Desktop).Select(t => t.Id));
        if (queued.All(t => present.Contains(t!.Id))) return;
        DropTracks(t => Desktop(t) && !present.Contains(t.Id));
    });

    /// <summary>
    /// Drops tracks that left the library (a signed-out desktop's songs) from the whole queue;
    /// when the current track is one of them, playback stops and nothing stays loaded.
    /// </summary>
    public void DropTracks(Func<Track, bool> match)
    {
        if (!_queue.RemoveWhere(match))
        {
            QueueChanged();
            return;
        }
        CurrentTrack = null;
        StopPlayback();
    }
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
    /// to insert before, so the tracks simply play (from <paramref name="source"/>).</summary>
    public void PlayNext(IReadOnlyList<Track> tracks, string? source = null)
    {
        if (tracks.Count == 0) return;
        if (CurrentTrack == null) { PlayTracks(tracks, 0, source); return; }
        for (var i = tracks.Count - 1; i >= 0; i--) _queue.AddNext(tracks[i]);
        QueueChanged();
    }

    /// <summary>"Add to Queue" for several tracks; with nothing loaded they simply play.</summary>
    public void AddToQueue(IReadOnlyList<Track> tracks, string? source = null)
    {
        if (tracks.Count == 0) return;
        if (CurrentTrack == null) { PlayTracks(tracks, 0, source); return; }
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
        // Not recorded yet: a file that fails to load (broken.mp3, a revoked grant) must not
        // land in Last Played / On Repeat. The first tick that proves audio is moving records it.
        FlushListened();
        _unrecordedPlay = track;
        _unrecordedFrom = fromPosition ?? TimeSpan.Zero;
        _listened = TimeSpan.Zero;
        _listenAnchor = _unrecordedFrom;
        _listenTrack = null;
        _flushedListened = TimeSpan.MinValue;
        _startedFrom = _unrecordedFrom;
        CurrentTrack = track;
        Position = fromPosition ?? TimeSpan.Zero;
        Duration = track.Duration;
        ErrorText = string.Empty;
        _player.PendingSeekMs = fromPosition is { } p && p > TimeSpan.Zero ? (long)p.TotalMilliseconds : -1;
        _player.Play(track.FilePath);
        IsPlaying = true;
        TrackStarted?.Invoke(this, track);
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
        // While the computer is unreachable the next song is the first that plays without it,
        // the one AdvancePlayable will land on, so the gapless handoff still matches.
        var upcoming = _computerUnreachable && NeedsComputer is { } needs
            ? _queue.UpNext.FirstOrDefault(t => !needs(t))
            : _queue.UpNext.FirstOrDefault();
        if (RepeatMode == RepeatMode.One || upcoming == null || CurrentTrack == null)
        {
            _player.CancelPreparedNext();
            return;
        }
        _player.PrepareNext(upcoming.FilePath);
    }

    private void StopPlayback()
    {
        FlushListened();
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
        OnPropertyChanged(nameof(HasNext));   // the mini player greys its Next with nothing to skip to
        // _queue.Current, not CurrentTrack: a queue that ran out keeps its last track loaded
        // for the mini bar, but has no place left to number.
        QueuePositionText = _queue.Current != null && _queue.PlayedInQueue is { } played
            ? Loc.T("Mobile.Player.TrackOf", played + 1, played + 1 + _queue.UpNext.Count)
            : string.Empty;
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
        PlaybackTick?.Invoke(this, playing);
        // Only a tick that arrives while actually playing AND past the start proves the stream
        // is healthy. The Android player reports Playing from Play() on and polls 00:00 while
        // it is still connecting, so "Playing" alone cleared the streak between every two
        // errors and the breaker never tripped (S23, 2026-10-19: a 20-song queue of desktop
        // songs, computer off, cycled in silence for over half an hour).
        if (playing && position > _startedFrom) _consecutiveErrors = 0;
        // A song that needs the computer really playing proves it is back: stop passing over
        // the others (only a new queue or Play from stopped cleared this, so after Previous
        // restarted one, its end still skipped every later desktop song and stopped).
        if (_computerUnreachable && playing && position > _startedFrom && CurrentTrack is { } streaming
            && NeedsComputer?.Invoke(streaming) == true)
        {
            _computerUnreachable = false;
            PrepareUpcoming();
        }
        // Past the start, not merely Playing: the Android player reports Playing from Play()
        // on, and a file that never decodes keeps polling at its start position until the error.
        AccumulateListened(position, playing);
        if (playing && _unrecordedPlay != null && ReferenceEquals(_unrecordedPlay, CurrentTrack) && position > _unrecordedFrom
            && ThresholdReached())
            RecordStartedPlay();
        // Position only: the queue *structure* is written by its own mutators, so rewriting it
        // here would re-serialize and fsync the whole queue every five seconds for a value
        // that did not change. See SavePositionNow.
        if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= SaveIntervalSeconds)
        {
            SavePositionNow();
            FlushListened();
        }
    });

    /// <summary>Adds what was heard since the previous tick (see <see cref="MaxListenStepSeconds"/>).</summary>
    private void AccumulateListened(TimeSpan position, bool playing)
    {
        var step = position - _listenAnchor;
        _listenAnchor = position;
        if (playing && step > TimeSpan.Zero && step.TotalSeconds <= MaxListenStepSeconds)
            _listened += step;
    }

    /// <summary>True once the song has been heard for the chosen share of its length. A song of
    /// unknown length can't be measured against, so it counts at once.</summary>
    private bool ThresholdReached()
    {
        var percent = Math.Clamp(PlayCountThresholdPercent?.Invoke() ?? 0, 0, 100);
        var length = Duration > TimeSpan.Zero ? Duration : CurrentTrack?.Duration ?? TimeSpan.Zero;
        return percent == 0 || length <= TimeSpan.Zero || _listened.TotalSeconds >= length.TotalSeconds * percent / 100.0;
    }

    /// <summary>Writes the heard time to the current song's play in the log (absolute, so a repeat
    /// is harmless). Not for a play that is not in the log yet.</summary>
    private void FlushListened()
    {
        if (_listenTrack is not { } track || _listened == _flushedListened) return;
        _flushedListened = _listened;
        _history?.SetListened(track, _listened);
    }

    /// <summary>Raised once a started track's play is in the log (see <see cref="StartTrack"/>);
    /// the shell refreshes the Library Shelf and the Home rows from the log on it.</summary>
    public event EventHandler? PlayRecorded;

    /// <summary>A track was started (the scrobbler restarts its play clock).</summary>
    public event EventHandler<Track>? TrackStarted;

    /// <summary>Every position tick, with whether audio is moving (the scrobbler's played-time clock).</summary>
    public event EventHandler<bool>? PlaybackTick;

    private void RecordStartedPlay()
    {
        var track = _unrecordedPlay!;
        _unrecordedPlay = null;
        _listenTrack = track;
        _history?.RecordPlay(track);
        PlayRecorded?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerDuration(object? sender, TimeSpan duration) => _marshal(() =>
    {
        if (_disposed) return;
        Duration = duration;
    });

    private void OnPlayerTrackEnded(object? sender, EventArgs e) => _marshal(() =>
    {
        if (_disposed) return;
        var next = AdvancePlayable(QueueAdvance.Natural, out var heldBack);
        if (next != null)
        {
            StartTrack(next, null);
            return;
        }
        StopPlayback();
        if (heldBack) ShowProblem(ComputerUnreachableStopped);
    });

    private void OnPlayerError(object? sender, string message) => _marshal(() =>
    {
        if (_disposed) return;
        DebugLog.Write("Audio", $"Playback error: {message} — track: {CurrentTrack?.Title ?? "(none)"}");
        if (CurrentTrack is { } failed && NeedsComputer?.Invoke(failed) == true && IsConnectionError(message))
        {
            // The computer did not answer. Every other song that needs it would wait out the
            // same timeout in silence, so go straight to the next one on the phone, or stop
            // here with the queue intact; either way, say why.
            _computerUnreachable = true;
            _consecutiveErrors = 0;
            DebugLog.Write("Audio", "The computer is unreachable: passing over songs that are not downloaded");
            var playable = AdvancePlayable(QueueAdvance.UserSkip, out _);
            if (playable != null)
            {
                StartTrack(playable, null);
                ShowProblem(ComputerUnreachableSkipping);
            }
            else
            {
                StopPlayback();
                ShowProblem(ComputerUnreachableStopped);
            }
            return;
        }
        if (++_consecutiveErrors >= MaxConsecutiveErrors)
        {
            StopPlayback();
            ErrorText = TooManyErrors;
            return;
        }
        var next = AdvancePlayable(QueueAdvance.UserSkip, out var heldBack);
        if (next != null) StartTrack(next, null);
        else StopPlayback();
        // After the skip: StartTrack clears the line, so setting it first never showed it.
        if (heldBack) ShowProblem(ComputerUnreachableStopped);
        else ErrorText = message;
    });

    /// <summary>Media3's network failures (ERROR_CODE_IO_NETWORK_CONNECTION_FAILED / _TIMEOUT):
    /// the host did not answer, as opposed to answering with an error or sending a bad file.</summary>
    private static bool IsConnectionError(string message) =>
        message.StartsWith("ERROR_CODE_IO_NETWORK_CONNECTION", StringComparison.Ordinal);

    private void ShowProblem(string text)
    {
        ErrorText = text;
        Notice = text;
    }

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
            SourceLabel = SourceLabel,
            PlayedInQueue = snapshot.PlayedInQueue,
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
        // A song tapped while the start was still loading (the songs list before the restore)
        // already plays: the saved queue must not replace it under the running engine.
        if (state == null || CurrentTrack != null) return;

        _queue = PlaybackQueue.Restore(
            new PlaybackQueueState(state.CurrentTrackId, state.UpNextIds, state.HistoryIds, state.RepeatCycleIds,
                state.RepeatMode, state.IsShuffleEnabled, state.OriginalOrderIds, state.PlayedInQueue),
            id => _library.GetTrackById(id));
        SourceLabel = state.SourceLabel;
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
        _library.LibraryUpdated -= OnLibraryUpdated;
        if (_volume != null) _volume.Changed -= OnVolumeChanged;
    }
}
