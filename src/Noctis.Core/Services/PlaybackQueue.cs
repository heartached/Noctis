using Noctis.Models;

namespace Noctis.Services;

/// <summary>Why the queue is advancing; mirrors the desktop's QueueAdvanceReason.</summary>
public enum QueueAdvance { Natural, UserSkip, Previous }

/// <summary>Persisted shape of a <see cref="PlaybackQueue"/> (track ids only).
/// <see cref="PlayedInQueue"/> is null in a state saved before it existed.</summary>
public sealed record PlaybackQueueState(
    Guid? CurrentId,
    IReadOnlyList<Guid> UpNextIds,
    IReadOnlyList<Guid> HistoryIds,
    IReadOnlyList<Guid> RepeatCycleIds,
    RepeatMode RepeatMode,
    bool IsShuffleEnabled,
    IReadOnlyList<Guid> OriginalOrderIds,
    int? PlayedInQueue = null);

/// <summary>
/// Ordered playback state: what is playing, what is next, what played. Pure and
/// synchronous; the caller starts the audio for whatever <see cref="Current"/> becomes.
/// Semantics copied from the desktop PlayerViewModel (ReplaceQueueAndPlay, AddNext,
/// AddToQueue, ToggleShuffle, AdvanceQueueCore, GoBackInQueue, TrimHistory), minus
/// the desktop-only radio refill, autoplay, explicit-content parking and AutoMix.
/// The desktop ViewModel keeps its own implementation; this one is for the Android
/// player.
/// </summary>
public sealed class PlaybackQueue
{
    /// <summary>The desktop's TrimHistory cap.</summary>
    public const int DefaultHistoryCap = 50;

    private readonly int _historyCap;
    private readonly List<Track> _upNext = new();
    private readonly List<Track> _history = new();
    private List<Track> _repeatCycle = new();
    private List<Track> _originalOrder = new();
    // Tracks Shuffle holds out of Up Next (snoozed, Skip When Shuffling), by reference: never
    // dealt into the shuffled order, back in their place on Shuffle off (the desktop's
    // ToggleShuffle / BuildUnshuffledQueue). Without this set Shuffle off would drop them.
    private HashSet<Track> _heldOut = new(ReferenceEqualityComparer.Instance);
    // The generator Shuffle was turned on with, for the reshuffle of each Repeat All pass.
    private Random? _rng;

    /// <summary>
    /// Whether Shuffle leaves <paramref name="track"/> out of the order it deals: snoozed (Snooze
    /// for a Month) or marked Skip When Shuffling, as the desktop's ShuffleHelper.WeightedShuffle
    /// and ToggleShuffle do. Played on its own (picked, queued while shuffled) it still plays.
    /// </summary>
    public static bool IsHeldOutOfShuffle(Track track) => track.IsSnoozed || track.SkipWhenShuffling;

    public PlaybackQueue(int historyCap = DefaultHistoryCap) => _historyCap = Math.Max(1, historyCap);

    /// <summary>The playing track, or null when stopped.</summary>
    public Track? Current { get; private set; }

    /// <summary>Upcoming tracks, next-to-play first.</summary>
    public IReadOnlyList<Track> UpNext => _upNext;

    /// <summary>Played tracks, most recent first, capped.</summary>
    public IReadOnlyList<Track> History => _history;

    /// <summary>
    /// How many tracks of the current queue played before <see cref="Current"/>: the newest
    /// entries of <see cref="History"/> pushed since the last <see cref="ReplaceAll"/> or
    /// Repeat All wrap. History alone cannot say this — it also holds earlier queues and stops
    /// at its cap — and this count does neither, so the phone's "Track N of M" stays exact.
    /// Null when unknown: <see cref="Back"/> stepped into an earlier queue's history, or the
    /// queue was restored from a state saved before the count existed.
    /// </summary>
    public int? PlayedInQueue { get; private set; } = 0;

    public RepeatMode RepeatMode { get; set; }

    public bool IsShuffleEnabled { get; private set; }

    /// <summary>
    /// Replace everything and make <c>tracks[startIndex]</c> current. Records the repeat
    /// cycle starting at that index so a Repeat All wrap replays the queue in the order
    /// the user actually started it; clears shuffle state; the old current goes to history.
    /// </summary>
    public Track? ReplaceAll(IReadOnlyList<Track> tracks, int startIndex)
    {
        if (tracks.Count == 0) return Current;
        if (startIndex < 0 || startIndex >= tracks.Count) startIndex = 0;

        PushHistory(Current);
        PlayedInQueue = 0;

        _originalOrder.Clear();
        _heldOut.Clear();
        IsShuffleEnabled = false;
        // List order, so Repeat All wraps to the first track (not to the one picked mid-list).
        _repeatCycle = tracks.ToList();

        _upNext.Clear();
        for (int i = startIndex + 1; i < tracks.Count; i++)
            _upNext.Add(tracks[i]);

        Current = tracks[startIndex];
        return Current;
    }

    // While shuffled, songs queued go into the pre-shuffle order too (Play Next at its front,
    // Add to Queue at its end): Shuffle off rebuilds Up Next from that order, and without them
    // there it dropped every song queued in the meantime.

    /// <summary>"Play Next": front of UpNext.</summary>
    public void AddNext(Track track)
    {
        _upNext.Insert(0, track);
        if (IsShuffleEnabled) _originalOrder.Insert(0, track);
    }

    /// <summary>"Add to Queue": end of UpNext.</summary>
    public void Add(Track track)
    {
        _upNext.Add(track);
        if (IsShuffleEnabled) _originalOrder.Add(track);
    }

    public void AddRange(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        _upNext.AddRange(list);
        if (IsShuffleEnabled) _originalOrder.AddRange(list);
    }

    /// <summary>
    /// "Add Before Current Song": <paramref name="tracks"/> go in just before <see cref="Current"/>,
    /// in their order, as if they had played last: the newest <see cref="History"/> entries, so
    /// Previous plays the last of them first and Current keeps playing. They count as played in
    /// this queue (<see cref="PlayedInQueue"/>), so "Track N of M" moves on with them. Nothing
    /// happens without a Current (there is no song to go before); returns whether they went in.
    /// </summary>
    public bool AddBeforeCurrent(IReadOnlyList<Track> tracks)
    {
        if (Current == null || tracks.Count == 0) return false;
        for (int i = 0; i < tracks.Count; i++) PushHistory(tracks[i]);
        if (PlayedInQueue is { } played) PlayedInQueue = played + tracks.Count;
        return true;
    }

    /// <summary>
    /// Plays a played song again from where it is: <see cref="Back"/> until History[<paramref name="historyIndex"/>]
    /// is Current, so the songs played after it go back to the front of UpNext in their order
    /// and none is copied. Returns the new Current, or null for an index outside History.
    /// </summary>
    public Track? JumpBack(int historyIndex)
    {
        if ((uint)historyIndex >= (uint)_history.Count) return null;
        for (int i = 0; i <= historyIndex; i++) Back();
        return Current;
    }

    /// <summary>
    /// A move in the queue read as one list, the way the phone's Queue page shows it with its
    /// History: the played songs oldest first (indices 0..History.Count-1), then <see cref="Current"/>
    /// (History.Count), then UpNext. The song at <paramref name="from"/> ends up at <paramref name="to"/>
    /// (ObservableCollection.Move); Current never moves itself, it only ends up after or before
    /// the song, so a song of Up Next moved above it plays before it (Previous reaches it) and a
    /// played song moved below it plays again after it. A played song that leaves or joins this
    /// queue's newest History entries changes <see cref="PlayedInQueue"/> with it. Returns false
    /// (and changes nothing) without a Current, for Current itself or an index outside the list.
    /// </summary>
    public bool MoveInTimeline(int from, int to)
    {
        if (Current == null) return false;
        int h = _history.Count, count = h + 1 + _upNext.Count;
        if ((uint)from >= (uint)count || (uint)to >= (uint)count || from == h) return false;
        if (from == to) return true;
        var list = new List<Track>(count);
        for (int i = h - 1; i >= 0; i--) list.Add(_history[i]);
        list.Add(Current);
        list.AddRange(_upNext);
        var song = list[from];
        var wasPlayed = from < h;
        var oldHistoryIndex = wasPlayed ? h - 1 - from : -1;
        list.RemoveAt(from);
        list.Insert(to, song);
        // Current's slot by position (the same Track may also sit in History): it shifts down
        // one when the song lands at or above it.
        var c0 = wasPlayed ? h - 1 : h;
        var c = to <= c0 ? c0 + 1 : c0;
        _history.Clear();
        for (int i = c - 1; i >= 0; i--) _history.Add(list[i]);
        _upNext.Clear();
        for (int i = c + 1; i < list.Count; i++) _upNext.Add(list[i]);
        var nowPlayed = to < c;
        if (PlayedInQueue is { } played)
        {
            if (wasPlayed && !nowPlayed && oldHistoryIndex < played) PlayedInQueue = played - 1;
            else if (!wasPlayed && nowPlayed && c - 1 - to <= played) PlayedInQueue = played + 1;
        }
        // Shuffle off rebuilds Up Next from the pre-shuffle order: a played song back in Up Next
        // must be in it (as Play Next's are). One moved out drops from it there on its own.
        if (wasPlayed && !nowPlayed && IsShuffleEnabled) _originalOrder.Insert(0, song);
        if (_history.Count > _historyCap) _history.RemoveRange(_historyCap, _history.Count - _historyCap);
        return true;
    }

    public void RemoveAt(int upNextIndex)
    {
        if ((uint)upNextIndex < (uint)_upNext.Count)
            _upNext.RemoveAt(upNextIndex);
    }

    public void Move(int fromUpNextIndex, int toUpNextIndex)
    {
        if ((uint)fromUpNextIndex >= (uint)_upNext.Count) return;
        if ((uint)toUpNextIndex >= (uint)_upNext.Count) return;
        if (fromUpNextIndex == toUpNextIndex) return;
        var t = _upNext[fromUpNextIndex];
        _upNext.RemoveAt(fromUpNextIndex);
        _upNext.Insert(toUpNextIndex, t);
    }

    /// <summary>Clears UpNext only; Current keeps playing.</summary>
    public void Clear() => _upNext.Clear();

    /// <summary>
    /// Drops every track matching <paramref name="match"/> from UpNext, History, the repeat
    /// cycle and the pre-shuffle order (tracks that left the library, e.g. a signed-out
    /// desktop's songs). Returns true when Current matched too; it is then cleared (stopped).
    /// </summary>
    public bool RemoveWhere(Func<Track, bool> match)
    {
        // This queue's played tracks are the newest History entries: those removed stop counting.
        if (PlayedInQueue is { } played)
            PlayedInQueue = played - _history.Take(played).Count(match);
        _upNext.RemoveAll(t => match(t));
        _history.RemoveAll(t => match(t));
        _repeatCycle.RemoveAll(t => match(t));
        _originalOrder.RemoveAll(t => match(t));
        _heldOut.RemoveWhere(t => match(t));
        if (Current == null || !match(Current)) return false;
        Current = null;
        return true;
    }

    /// <summary>
    /// Toggle shuffle. On: remember the order, hold out the tracks Shuffle skips
    /// (<see cref="IsHeldOutOfShuffle"/>) and Fisher-Yates the rest of UpNext. Off: restore the
    /// remembered order minus tracks that have since played or been removed; the held-out ones
    /// come back in their places.
    /// </summary>
    public void SetShuffle(bool enabled, Random? rng = null)
    {
        if (enabled == IsShuffleEnabled) return;
        IsShuffleEnabled = enabled;

        if (enabled)
        {
            _rng = rng;
            _originalOrder = _upNext.ToList();
            _heldOut.Clear();
            HoldOutAndDeal(_upNext);
        }
        else
        {
            if (_originalOrder.Count > 0)
            {
                var stillQueued = new HashSet<Track>(_upNext, ReferenceEqualityComparer.Instance);
                _upNext.Clear();
                foreach (var t in _originalOrder)
                    if (stillQueued.Contains(t) || _heldOut.Contains(t)) _upNext.Add(t);
                _originalOrder.Clear();
            }
            _heldOut.Clear();
        }
    }

    // Takes the held-out tracks out of the list into _heldOut, then shuffles what is left.
    private void HoldOutAndDeal(List<Track> list)
    {
        list.RemoveAll(t =>
        {
            if (!IsHeldOutOfShuffle(t)) return false;
            _heldOut.Add(t);
            return true;
        });
        var rng = _rng ?? Random.Shared;
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Advance per the desktop rules. A natural end with <see cref="RepeatMode.One"/> replays
    /// Current (an explicit skip still advances). Otherwise Current goes to History and the
    /// head of UpNext becomes Current; with UpNext empty and <see cref="RepeatMode.All"/> the
    /// recorded cycle restarts (falling back to reversed History when no cycle was recorded,
    /// e.g. a queue restored without one), keeping History so Back reaches the last pass;
    /// else Current becomes null (stopped).
    /// </summary>
    public Track? Advance(QueueAdvance reason)
    {
        if (RepeatMode == RepeatMode.One && Current != null && reason == QueueAdvance.Natural)
            return Current;

        if (Current != null && PlayedInQueue is { } played) PlayedInQueue = played + 1;
        PushHistory(Current);

        if (_upNext.Count > 0)
        {
            Current = _upNext[0];
            _upNext.RemoveAt(0);
            return Current;
        }

        if (RepeatMode == RepeatMode.All && (_repeatCycle.Count > 0 || _history.Count > 0))
        {
            // No cycle recorded: replay History and keep it as the cycle, since History is no
            // longer cleared here (GitHub #124: Back on a new pass's first song did nothing).
            if (_repeatCycle.Count == 0)
                _repeatCycle = Enumerable.Reverse(_history).ToList();
            var all = new List<Track>(_repeatCycle);
            _originalOrder.Clear();
            _heldOut.Clear();
            PlayedInQueue = 0;
            if (all.Count == 0) { Current = null; return null; }
            // Shuffle stays on across the wrap, as on the desktop (BuildRepeatAllPass): each pass
            // is dealt anew, minus the tracks Shuffle holds out, with the cycle as the Shuffle-off
            // order. It replayed the cycle in list order with Shuffle still lit.
            if (IsShuffleEnabled)
            {
                var dealt = new List<Track>(all);
                HoldOutAndDeal(dealt);
                if (dealt.Count > 0)
                {
                    _originalOrder = new List<Track>(all);
                    all = dealt;
                }
                else _heldOut.Clear();   // every one held out: the pass plays in order (the desktop's too)
            }
            _upNext.Clear();
            _upNext.AddRange(all.Skip(1));
            Current = all[0];
            return Current;
        }

        Current = null;
        return null;
    }

    /// <summary>Previous: pops History into Current and pushes the old Current to the front
    /// of UpNext. With an empty History the current track stays (the caller restarts it).</summary>
    public Track? Back()
    {
        if (_history.Count == 0) return Current;
        // At the queue's first track History[0] belongs to the queue before it.
        PlayedInQueue = PlayedInQueue > 0 ? PlayedInQueue - 1 : null;
        if (Current != null) _upNext.Insert(0, Current);
        Current = _history[0];
        _history.RemoveAt(0);
        return Current;
    }

    private void PushHistory(Track? t)
    {
        if (t == null) return;
        _history.Insert(0, t);
        if (_history.Count > _historyCap)
            _history.RemoveRange(_historyCap, _history.Count - _historyCap);
    }

    public PlaybackQueueState Snapshot() => new(
        Current?.Id,
        _upNext.Select(t => t.Id).ToList(),
        _history.Select(t => t.Id).ToList(),
        _repeatCycle.Select(t => t.Id).ToList(),
        RepeatMode,
        IsShuffleEnabled,
        _originalOrder.Select(t => t.Id).ToList(),
        PlayedInQueue);

    /// <summary>An independent copy holding the same tracks, for undoing a look-ahead.</summary>
    public PlaybackQueue Clone()
    {
        var q = new PlaybackQueue(_historyCap)
        {
            Current = Current, RepeatMode = RepeatMode, IsShuffleEnabled = IsShuffleEnabled, PlayedInQueue = PlayedInQueue,
        };
        q._upNext.AddRange(_upNext);
        q._history.AddRange(_history);
        q._repeatCycle = new List<Track>(_repeatCycle);
        q._originalOrder = new List<Track>(_originalOrder);
        q._heldOut = new HashSet<Track>(_heldOut, ReferenceEqualityComparer.Instance);
        q._rng = _rng;
        return q;
    }

    /// <summary>Rebuild from a snapshot; ids that no longer resolve are dropped.</summary>
    public static PlaybackQueue Restore(PlaybackQueueState s, Func<Guid, Track?> resolve, int historyCap = DefaultHistoryCap)
    {
        var q = new PlaybackQueue(historyCap)
        {
            RepeatMode = s.RepeatMode, IsShuffleEnabled = s.IsShuffleEnabled, PlayedInQueue = s.PlayedInQueue,
        };
        q.Current = s.CurrentId is { } id ? resolve(id) : null;
        q._upNext.AddRange(s.UpNextIds.Select(resolve).OfType<Track>());
        q._history.AddRange(s.HistoryIds.Select(resolve).OfType<Track>());
        q._repeatCycle = s.RepeatCycleIds.Select(resolve).OfType<Track>().ToList();
        q._originalOrder = s.OriginalOrderIds.Select(resolve).OfType<Track>().ToList();
        // The held-out tracks are not saved: those of the pre-shuffle order that Up Next lacks
        // and Shuffle still holds out (a restart does not lose them on Shuffle off).
        if (q.IsShuffleEnabled && q._originalOrder.Count > 0)
        {
            var queued = new HashSet<Track>(q._upNext, ReferenceEqualityComparer.Instance);
            foreach (var t in q._originalOrder)
                if (!queued.Contains(t) && IsHeldOutOfShuffle(t) && !ReferenceEquals(t, q.Current)) q._heldOut.Add(t);
        }
        return q;
    }
}
