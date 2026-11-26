using System.Net.Http;
using Noctis.Localization;

namespace Noctis.Services.MediaServer;

/// <summary>Album summary as listed by a media server (not a library Album).</summary>
public sealed class ServerAlbum
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public int Year { get; init; }
    public int SongCount { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Server-side artwork id (Subsonic coverArt / Jellyfin item id); null when the album has none.</summary>
    public string? CoverArtId { get; init; }

    /// <summary>The album's genre as the server lists it; empty when it has none.</summary>
    public string Genre { get; init; } = string.Empty;

    /// <summary>
    /// The server connection this album was listed by (<see cref="SourceConnection.Id"/>), so
    /// its tracks, cover and stream URLs come from that server (GitHub #268). Stamped by
    /// <see cref="MediaServerService"/>; <see cref="Guid.Empty"/> on a bare client result.
    /// </summary>
    public Guid ConnectionId { get; internal set; }

    /// <summary>The display name of that server ("Home Navidrome", "Ampache (music.example.com)").</summary>
    public string ServerName { get; internal set; } = string.Empty;

    /// <summary>
    /// The same album on lower-priority servers (same album artist and title). The unified
    /// list shows it once, from the first server in the user's order; these are tried in
    /// order when that server cannot return its tracks.
    /// </summary>
    public IReadOnlyList<ServerAlbum> Alternates { get; internal set; } = Array.Empty<ServerAlbum>();

    /// <summary>Stable id across servers: "&lt;connection id&gt;:&lt;server album id&gt;".</summary>
    public string Key => $"{ConnectionId:N}:{Id}";
}

/// <summary>Categorised outcome of a connect/test attempt, for the Settings status line.</summary>
public enum MediaServerError
{
    None = 0,
    /// <summary>The URL is empty or not an absolute http/https URL.</summary>
    InvalidUrl,
    /// <summary>Plain http to a non-private host; credentials would travel in the clear.</summary>
    InsecureUrl,
    /// <summary>DNS/connect/timeout failure — server not reachable at that URL.</summary>
    Unreachable,
    /// <summary>The server answered but rejected the credentials.</summary>
    AuthFailed,
    /// <summary>The server answered with something unexpected (wrong software at the URL, protocol error).</summary>
    ServerError
}

/// <summary>Result of <see cref="IMediaServerClient.ConnectAsync"/>.</summary>
public sealed class MediaServerConnectResult
{
    public bool Success { get; init; }
    public MediaServerError Error { get; init; }

    /// <summary>Human-readable status ("Connected", "Wrong username or password", …), in the app's language. Never contains secrets.</summary>
    public string Message { get; init; } = string.Empty;

    public static MediaServerConnectResult Ok(string? message = null) =>
        new() { Success = true, Error = MediaServerError.None, Message = message ?? Loc.T("Settings.Server.Connected") };

    public static MediaServerConnectResult Fail(MediaServerError error, string message) =>
        new() { Success = false, Error = error, Message = message };

    /// <summary>
    /// Classifies a transport-layer failure (thrown before any HTTP status existed)
    /// for the Settings status line. DNS, refused, TLS and timeout all used to
    /// collapse into one generic "couldn't reach" — indistinguishable for support:
    /// a missing DNS record, a wrong port and a self-signed certificate each need
    /// a different fix on the user's side.
    /// </summary>
    public static MediaServerConnectResult FromTransportException(Exception ex)
    {
        var message = ex switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                Loc.T("Settings.Server.NameNotFound"),
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
                Loc.T("Settings.Server.Refused"),
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                Loc.T("Settings.Server.SecureFailed"),
            OperationCanceledException =>
                Loc.T("Settings.Server.NoResponse"),
            _ => Loc.T("Settings.Server.Unreachable")
        };
        return Fail(MediaServerError.Unreachable, message);
    }
}

/// <summary>Server-side search hits, split by kind.</summary>
public sealed class ServerSearchResult
{
    public IReadOnlyList<ServerAlbum> Albums { get; init; } = Array.Empty<ServerAlbum>();
    public IReadOnlyList<Noctis.Models.Track> Tracks { get; init; } = Array.Empty<Noctis.Models.Track>();

    /// <summary>
    /// Server artwork id per song hit (keyed by <c>Track.Id</c>), for lazily
    /// materializing cover art of tracks found outside an album listing.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> TrackArtIds { get; init; } =
        new Dictionary<Guid, string>();

    public static readonly ServerSearchResult Empty = new();
}
