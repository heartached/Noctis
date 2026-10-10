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
        IsShuffleEnabled = false;
        _repeatCycle = tracks.Skip(startIndex).Concat(tracks.Take(startIndex)).ToList();

        _upNext.Clear();
        for (int i = startIndex + 1; i < tracks.Count; i++)
            _upNext.Add(tracks[i]);

        Current = tracks[startIndex];
        return Current;
    }

    /// <summary>"Play Next": front of UpNext.</summary>
    public void AddNext(Track track) => _upNext.Insert(0, track);

    /// <summary>"Add to Queue": end of UpNext.</summary>
    public void Add(Track track) => _upNext.Add(track);

    public void AddRange(IEnumerable<Track> tracks) => _upNext.AddRange(tracks);

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
        if (Current == null || !match(Current)) return false;
        Current = null;
        return true;
    }

    /// <summary>
    /// Toggle shuffle. On: remember the order and Fisher-Yates UpNext. Off: restore the
    /// remembered order minus tracks that have since played or been removed.
    /// </summary>
    public void SetShuffle(bool enabled, Random? rng = null)
    {
        if (enabled == IsShuffleEnabled) return;
        IsShuffleEnabled = enabled;

        if (enabled)
        {
            _originalOrder = _upNext.ToList();
            rng ??= Random.Shared;
            for (int i = _upNext.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_upNext[i], _upNext[j]) = (_upNext[j], _upNext[i]);
            }
        }
        else if (_originalOrder.Count > 0)
        {
            var stillQueued = new HashSet<Track>(_upNext, ReferenceEqualityComparer.Instance);
            _upNext.Clear();
            foreach (var t in _originalOrder)
                if (stillQueued.Contains(t)) _upNext.Add(t);
            _originalOrder.Clear();
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
            PlayedInQueue = 0;
            if (all.Count == 0) { Current = null; return null; }
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
        return q;
    }
}
