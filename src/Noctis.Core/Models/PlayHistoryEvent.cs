using System.Text.Json.Serialization;

namespace Noctis.Models;

/// <summary>
/// A single persisted playback event. Title/Artist are denormalized so the
/// log stays readable even after the track leaves the library.
/// </summary>
public class PlayHistoryEvent
{
    /// <summary>Members a newer Noctis saved that this build does not know, written back
    /// unchanged so an older build sharing the data folder does not erase them (GitHub #230).</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

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

    /// <summary>
    /// A media-server song's album, as the Server section keys it: "&lt;connection id&gt;:&lt;server
    /// album id&gt;" (ServerAlbum.Key), so Album Roulette counts plays made in Noctis for that
    /// album (GitHub #268). Null on library plays, on events logged before it existed, and when
    /// the server did not name the album.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerAlbumKey { get; set; }

    /// <summary>
    /// A media-server song's album identity (album artist + title, <see cref="Track.ComputeAlbumId"/>),
    /// the identity the merged server list shares across servers. Matches the album when the
    /// server's album id changed or was unknown. Null on library plays and older events.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ServerAlbumIdentity { get; set; }
}
