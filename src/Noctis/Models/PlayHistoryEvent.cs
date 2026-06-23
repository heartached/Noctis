namespace Noctis.Models;

/// <summary>
/// A single persisted playback event. Title/Artist are denormalized so the
/// log stays readable even after the track leaves the library.
/// </summary>
public class PlayHistoryEvent
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public DateTime PlayedAtUtc { get; set; }

    /// <summary>True when the user skipped away before hearing half the track.</summary>
    public bool Skipped { get; set; }
}
