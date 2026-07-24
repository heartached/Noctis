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

    /// <summary>
    /// Loads the log off the calling thread. Call once during startup so the first
    /// <see cref="Events"/> access — which happens on the UI thread — doesn't pay for
    /// the file read.
    /// </summary>
    Task PreloadAsync();

    /// <summary>Records that playback of a track started.</summary>
    void RecordPlay(Track track);

    /// <summary>Marks the most recent play of the track as skipped.</summary>
    void RecordSkip(Track track);

    /// <summary>Writes any pending events to disk immediately.</summary>
    Task FlushAsync();
}
