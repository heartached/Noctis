using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// Browse/stream client for one media-server protocol family (Subsonic, Jellyfin).
/// Unlike <see cref="IMediaSourceConnector"/> (bulk scan into the library), this
/// contract is on-demand: the Server page browses albums page by page and only
/// materializes <see cref="Track"/>s for the album the user opens. Implementations
/// must be safe to call from any thread and must never log or embed raw passwords
/// anywhere except the per-request auth the protocol requires.
/// </summary>
public interface IMediaServerClient
{
    SourceType SourceType { get; }

    /// <summary>
    /// Validates the connection and credentials. On success the client stores
    /// whatever the protocol needs for later requests back onto
    /// <paramref name="connection"/> (Subsonic keeps the password for per-request
    /// token derivation; Jellyfin swaps the password for an access token + user id
    /// so the password itself is never persisted).
    /// </summary>
    Task<MediaServerConnectResult> ConnectAsync(SourceConnection connection, string password, CancellationToken ct = default);

    /// <summary>Alphabetical page of albums. Empty list when the window is past the end.</summary>
    Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(SourceConnection connection, int offset, int limit, CancellationToken ct = default);

    /// <summary>Tracks of one album, in disc/track order, mapped to playable <see cref="Track"/>s.</summary>
    Task<IReadOnlyList<Track>> GetAlbumTracksAsync(SourceConnection connection, ServerAlbum album, CancellationToken ct = default);

    /// <summary>Server-side search over albums and songs.</summary>
    Task<ServerSearchResult> SearchAsync(SourceConnection connection, string query, CancellationToken ct = default);

    /// <summary>
    /// Raw cover-art bytes for <paramref name="artId"/> (a <see cref="ServerAlbum.CoverArtId"/>),
    /// or null when unavailable. Responses are size-capped and signature-checked.
    /// </summary>
    Task<byte[]?> GetArtworkAsync(SourceConnection connection, string artId, int maxSize, CancellationToken ct = default);
}
