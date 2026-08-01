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

    /// <summary>Human-readable status ("Connected", "Wrong username or password", …). Never contains secrets.</summary>
    public string Message { get; init; } = string.Empty;

    public static MediaServerConnectResult Ok(string message = "Connected") =>
        new() { Success = true, Error = MediaServerError.None, Message = message };

    public static MediaServerConnectResult Fail(MediaServerError error, string message) =>
        new() { Success = false, Error = error, Message = message };
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
