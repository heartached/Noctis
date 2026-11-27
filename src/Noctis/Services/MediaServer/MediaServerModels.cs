using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Noctis.Localization;
using Noctis.Models;

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
    /// How many times the server counted the album played (Navidrome's <c>playCount</c>,
    /// Jellyfin's UserData.PlayCount). Null when the server does not say: unknown is not
    /// "never played", so Album Roulette's play filters leave such an album out.
    /// </summary>
    public int? PlayCount { get; internal set; }

    /// <summary>When the server last saw it played (Navidrome's <c>played</c>, Jellyfin's
    /// UserData.LastPlayedDate); null when never played or the server does not say.</summary>
    public DateTimeOffset? LastPlayed { get; init; }

    /// <summary>True when the server reported whether (and how often) the album was played.</summary>
    public bool HasPlayInfo => PlayCount != null || LastPlayed != null;

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

/// <summary>
/// Plays Noctis logged for media-server songs, by album (GitHub #268): the play log keeps a
/// server song's album (<see cref="PlayHistoryEvent.ServerAlbumKey"/>), so Album Roulette's
/// "Never played" and "Not played in 6 months" see plays made in Noctis, not only the server's.
/// </summary>
public sealed class NoctisAlbumPlays
{
    private readonly Dictionary<string, (int Count, DateTimeOffset Last)> _byKey = new(StringComparer.Ordinal);

    public static readonly NoctisAlbumPlays None = new();

    /// <summary>
    /// Indexes <paramref name="events"/> for <paramref name="albums"/> (the merged list). A play
    /// whose album key is in the list counts for that album (and the album it is an alternate
    /// of). One without a key, or whose album id the server no longer lists, falls back to the
    /// album artist + title, but only when that names ONE album: on the play's own server when
    /// the key names it, else across the merged list. Two albums with that identity (two
    /// "Greatest Hits" by one artist) are ambiguous, and the play counts for neither.
    /// </summary>
    public static NoctisAlbumPlays Build(IEnumerable<PlayHistoryEvent>? events, IReadOnlyList<ServerAlbum> albums)
    {
        var result = new NoctisAlbumPlays();
        if (events == null) return result;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // Identity -> the merged albums with it; (server, identity) -> the merged albums holding a copy there.
        var byIdentity = new Dictionary<Guid, List<ServerAlbum>>();
        var byServerIdentity = new Dictionary<(Guid Server, Guid Identity), List<ServerAlbum>>();
        foreach (var album in albums)
        {
            var identity = MediaServerService.AlbumIdentity(album);
            Collect(byIdentity, identity, album);
            foreach (var copy in album.Alternates.Prepend(album))
            {
                keys.Add(copy.Key);
                Collect(byServerIdentity, (copy.ConnectionId, MediaServerService.AlbumIdentity(copy)), album);
            }
        }
        foreach (var e in events)
        {
            if (e.ServerAlbumKey == null && e.ServerAlbumIdentity == null) continue;
            var played = new DateTimeOffset(DateTime.SpecifyKind(e.PlayedAtUtc, DateTimeKind.Utc));
            if (e.ServerAlbumKey is { } key && keys.Contains(key))
            {
                Add(result._byKey, key, played);
                continue;
            }
            if (e.ServerAlbumIdentity is not { } id || id == Track.UnknownAlbumBucketId) continue;
            var candidates = ServerOf(e.ServerAlbumKey) is { } server && byServerIdentity.TryGetValue((server, id), out var onServer)
                ? onServer
                : byIdentity.GetValueOrDefault(id);
            if (candidates is { Count: 1 }) Add(result._byKey, candidates[0].Key, played);
        }
        return result;
    }

    private static void Collect<TKey>(Dictionary<TKey, List<ServerAlbum>> map, TKey key, ServerAlbum album) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ServerAlbum>();
        if (!list.Contains(album)) list.Add(album);
    }

    /// <summary>The server a play's album key names ("&lt;connection id&gt;:&lt;album id&gt;"), or null.</summary>
    private static Guid? ServerOf(string? key)
    {
        var colon = key?.IndexOf(':') ?? -1;
        return colon > 0 && Guid.TryParseExact(key.AsSpan(0, colon), "N", out var server) ? server : null;
    }

    private static void Add<TKey>(Dictionary<TKey, (int Count, DateTimeOffset Last)> map, TKey key, DateTimeOffset played) where TKey : notnull
    {
        map[key] = map.TryGetValue(key, out var seen)
            ? (seen.Count + 1, played > seen.Last ? played : seen.Last)
            : (1, played);
    }

    /// <summary>How many times Noctis played the album's songs (any of its servers' copies), and when last.</summary>
    public (int Count, DateTimeOffset? Last) For(ServerAlbum album)
    {
        var count = 0;
        DateTimeOffset? last = null;
        foreach (var copy in album.Alternates.Prepend(album))
        {
            if (!_byKey.TryGetValue(copy.Key, out var plays)) continue;
            count += plays.Count;
            if (last is null || plays.Last > last) last = plays.Last;
        }
        return (count, last);
    }
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

/// <summary>A page of a server's album list, or why it could not be read (<see cref="Error"/> non-null).</summary>
public sealed record ServerAlbumPage(IReadOnlyList<ServerAlbum> Albums, MediaServerConnectResult? Error = null);

/// <summary>
/// A server whose album list could not be read (GitHub #268): the Server section names it with
/// a Retry while the other servers' albums keep showing. <see cref="Message"/> is the detail
/// the Settings status line would show (no response, HTTP 500, wrong login).
/// </summary>
public sealed record ServerFailure(Guid ConnectionId, string ServerName, MediaServerError Error, string Message);

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

/// <summary>Reads the play counts and dates servers put on albums (GitHub #268 follow-up).</summary>
internal static class ServerPlayInfo
{
    /// <summary>A play count field, or null when it is missing or not a number (a string number is accepted).</summary>
    public static int? Count(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
            return (int)Math.Clamp(l, 0, int.MaxValue);
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return (int)Math.Clamp(parsed, 0, int.MaxValue);
        return null;
    }

    /// <summary>An ISO 8601 date field ("2024-05-01T12:00:00Z"); one without a zone is taken as UTC. Null when missing or unreadable.</summary>
    public static DateTimeOffset? Date(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return null;
        // Go's zero time ("0001-01-01T00:00:00Z") means "never".
        return date.Year <= 1 ? null : date;
    }
}
