using System.Text.Json.Serialization;

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

    /// <summary>
    /// How long the song was actually heard in this play, in milliseconds (the phone logs it;
    /// see <see cref="Noctis.Services.PlayHistoryService.SetListened"/>). Null on events logged
    /// before it existed, and on plays the desktop logged: <see cref="Noctis.Services.PlayListening"/>
    /// estimates those.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ListenedMs { get; set; }
}
