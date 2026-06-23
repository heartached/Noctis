using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Persistent log of playback events (plays and skips), used by the
/// Statistics page for the play log, hourly heatmap, and skip rates.
/// </summary>
public interface IPlayHistoryService
{
    /// <summary>Snapshot of all recorded events, oldest first.</summary>
    IReadOnlyList<PlayHistoryEvent> Events { get; }

    /// <summary>Records that playback of a track started.</summary>
    void RecordPlay(Track track);

    /// <summary>Marks the most recent play of the track as skipped.</summary>
    void RecordSkip(Track track);

    /// <summary>Writes any pending events to disk immediately.</summary>
    Task FlushAsync();
}
