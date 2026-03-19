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
}
