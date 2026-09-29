namespace Noctis.Models;

/// <summary>
/// Serializable snapshot of the playback queue, saved on exit and restored on launch.
/// </summary>
public class QueueState
{
    /// <summary>ID of the track that was playing (or paused) when the app closed.</summary>
    public Guid? CurrentTrackId { get; set; }

    /// <summary>Playback position within the current track.</summary>
    public double PositionSeconds { get; set; }

    /// <summary>Ordered list of upcoming track IDs.</summary>
    public List<Guid> UpNextIds { get; set; } = new();

    /// <summary>Recently played track IDs (most recent first).</summary>
    public List<Guid> HistoryIds { get; set; } = new();

    // The queue itself was restored on launch but the transport modes were not, so
    // repeat and shuffle silently reset to Off and the app un-muted on every restart.

    /// <summary>Repeat mode in effect when the app closed.</summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Off;

    /// <summary>Whether shuffle was on when the app closed.</summary>
    public bool IsShuffleEnabled { get; set; }

    /// <summary>Whether output was muted when the app closed.</summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Full repeat-all cycle, uncapped. History is a display list capped at 50, so a
    /// restored session could not wrap a longer queue correctly without this.
    /// </summary>
    public List<Guid> RepeatCycleIds { get; set; } = new();

    /// <summary>
    /// Pre-shuffle order, so turning shuffle off after a cold start restores the album
    /// order instead of leaving the queue scrambled. PlaybackQueue.Snapshot carries it;
    /// the desktop player writes its own QueueState and simply leaves this empty.
    /// </summary>
    public List<Guid> OriginalOrderIds { get; set; } = new();

    /// <summary>
    /// GitHub #86: file path of every queued track that is not in the library (dropped or
    /// opened with "Import dropped files" off). Their Ids are minted per session, so the
    /// Id lists alone could never resolve them on the next launch. Null in older files.
    /// </summary>
    public Dictionary<Guid, string>? ExternalTrackPaths { get; set; }
}

/// <summary>
/// "Where playback is", split out of <see cref="QueueState"/> so a periodic position
/// checkpoint does not have to rewrite the queue itself. The phone saves the position every
/// five seconds while playing (spec §7: process death costs at most five seconds), and a queue
/// started from the library holds the whole library — roughly 3N GUIDs across UpNextIds,
/// RepeatCycleIds and OriginalOrderIds, hundreds of KB, fsync'd, on phone flash, every five
/// seconds, for a value that is two numbers. This record is those two numbers.
/// <para>
/// Written only by hosts that checkpoint on a timer; the desktop saves its queue event-driven
/// and never produces one. <see cref="IPersistenceService.LoadQueueStateAsync"/> folds it back
/// in, so nothing above persistence has to know it exists.
/// </para>
/// </summary>
public class QueuePositionState
{
    /// <summary>
    /// The track the position belongs to. A checkpoint whose track is not the one queue.json
    /// names is stale — it raced a track change — and is discarded rather than applied to the
    /// wrong track.
    /// </summary>
    public Guid? CurrentTrackId { get; set; }

    /// <summary>Playback position within that track.</summary>
    public double PositionSeconds { get; set; }
}
