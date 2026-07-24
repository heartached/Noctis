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
}
