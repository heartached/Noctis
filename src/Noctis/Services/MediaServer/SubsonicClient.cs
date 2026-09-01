using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// Subsonic REST API client (Navidrome, Airsonic, Gonic, LMS, Supysonic, Ampache, …).
/// Prefers API v1.16.1 with per-request salted-token auth: t=md5(password+salt),
/// s=salt, so the raw password never travels in a URL
/// (http://www.subsonic.org/pages/api.jsp). The password itself must be kept
/// (encrypted at rest by PersistenceService) to derive fresh tokens; it is never logged.
///
/// Compatibility is negotiated once at connect time and stored on the connection:
/// a server that is older than the client rejects the request with error 30, so we
/// retry with the version it reports; a server that cannot do token auth (error 41,
/// or anything below API 1.13) gets the legacy <c>p=enc:</c> form instead.
/// </summary>
public sealed class SubsonicClient : IMediaServerClient
{
    public const string DefaultApiVersion = "1.16.1";
    /// <summary>First API version with salted-token auth.</summary>
    internal static readonly Version TokenAuthMinVersion = new(1, 13, 0);
    private const string ClientName = "Noctis";
    private const int MaxNegotiationAttempts = 4;

    private readonly HttpClient _http;

    public SubsonicClient(HttpClient http) => _http = http;

    public SourceType SourceType => SourceType.Navidrome;

    public async Task<MediaServerConnectResult> ConnectAsync(SourceConnection connection, string password, CancellationToken ct = default)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out var urlError, out var urlMessage);
        if (baseUrl == null)
            return MediaServerConnectResult.Fail(urlError, urlMessage);

        connection.BaseUriOrPath = baseUrl;

        // Start from the newest protocol we speak and step down to whatever the server
        // actually supports. Each downgrade is driven by the server's own answer, never
        // by guesswork, so a modern server pays exactly one round trip.
        var version = DefaultApiVersion;
        var mode = SubsonicAuthMode.Token;
        SubsonicEnvelope ping;
        for (var attempt = 0; ; attempt++)
        {
            ping = await GetSubsonicRootAsync(
                BuildRequestUrl(baseUrl, connection.Username, password, "ping", version, mode), ct);
            if (ping.Doc != null) break;

            var next = NextNegotiationStep(ping, version, mode);
            if (next == null || attempt + 1 >= MaxNegotiationAttempts)
                return MediaServerConnectResult.Fail(ping.Error, ping.Message);
            (version, mode) = next.Value;
        }

        using (ping.Doc)
        {
            // A server that reports an older version than we sent would reject a
            // future request that lands on a stricter code path; speak its version.
            if (TryParseVersion(ping.ServerVersion, out var reported) &&
                TryParseVersion(version, out var ours) && reported < ours)
            {
                version = reported.ToString(3);
            }
        }

        // Credentials verified: keep the password for per-request token derivation.
        connection.TokenOrPassword = password;
        connection.UserId = string.Empty;
        connection.ApiVersion = version;
        connection.AuthMode = mode;

        // Cheap probe so "connected but empty" is called out immediately.
        var albums = await GetAlbumsAsync(connection, 0, 1, ct);
        return albums.Count == 0
            ? MediaServerConnectResult.Ok("Connected — no albums visible on this server yet.")
            : MediaServerConnectResult.Ok();
    }

    /// <summary>
    /// Given a failed ping, the (version, auth mode) to try next, or null when the
    /// failure is not a compatibility problem this client can negotiate around.
    /// </summary>
    internal static (string Version, SubsonicAuthMode Mode)? NextNegotiationStep(SubsonicEnvelope failed, string version, SubsonicAuthMode mode)
    {
        switch (failed.Code)
        {
            case 30:
                // "Incompatible Subsonic REST protocol version. Server must upgrade." —
                // the server is older than the version we sent; it tells us its own.
                if (!TryParseVersion(failed.ServerVersion, out var server) ||
                    !TryParseVersion(version, out var current) || server >= current)
                    return null;
                var newMode = server < TokenAuthMinVersion ? SubsonicAuthMode.Password : mode;
                return (server.ToString(3), newMode);

            case 41:
                // "Token authentication not supported for LDAP users." — same user,
                // same password, legacy form.
                return mode == SubsonicAuthMode.Token ? (version, SubsonicAuthMode.Password) : null;

            case 40:
                // Pre-1.13 servers ignore t/s entirely and see "no password": only
                // when the server admits to being that old do we retry with p=enc:.
                if (mode == SubsonicAuthMode.Token &&
                    TryParseVersion(failed.ServerVersion, out var old) && old < TokenAuthMinVersion)
                    return (old.ToString(3), SubsonicAuthMode.Password);
                return null;

            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(SourceConnection connection, int offset, int limit, CancellationToken ct = default)
    {
        var url = BuildUrl(connection, "getAlbumList2",
            ("type", "alphabeticalByName"),
            ("size", Math.Clamp(limit, 1, 500).ToString()),
            ("offset", Math.Max(0, offset).ToString()));
        if (url == null) return Array.Empty<ServerAlbum>();

        var envelope = await GetSubsonicRootAsync(url, ct);
        if (envelope.Doc == null) return Array.Empty<ServerAlbum>();
        using (envelope.Doc)
        {
            var albums = new List<ServerAlbum>();
            if (Root(envelope.Doc).TryGetProperty("albumList2", out var list) &&
                list.TryGetProperty("album", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var album in array.EnumerateArray())
                {
                    var mapped = MapAlbum(album);
                    if (mapped != null) albums.Add(mapped);
                }
            }
            return albums;
        }
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(SourceConnection connection, ServerAlbum album, CancellationToken ct = default)
    {
        var url = BuildUrl(connection, "getAlbum", ("id", album.Id));
        if (url == null) return Array.Empty<Track>();

        var envelope = await GetSubsonicRootAsync(url, ct);
        if (envelope.Doc == null) return Array.Empty<Track>();
        using (envelope.Doc)
        {
            var tracks = new List<Track>();
            if (Root(envelope.Doc).TryGetProperty("album", out var albumEl) &&
                albumEl.TryGetProperty("song", out var songs) &&
                songs.ValueKind == JsonValueKind.Array)
            {
                foreach (var song in songs.EnumerateArray())
                    tracks.Add(MapSong(connection, song, album.Name, album.Artist));
            }
            return tracks;
        }
    }

    public async Task<ServerSearchResult> SearchAsync(SourceConnection connection, string query, CancellationToken ct = default)
    {
        var url = BuildUrl(connection, "search3",
            ("query", query),
            ("artistCount", "0"),
            ("albumCount", "24"),
            ("songCount", "50"));
        if (url == null) return ServerSearchResult.Empty;

        var envelope = await GetSubsonicRootAsync(url, ct);
        if (envelope.Doc == null) return ServerSearchResult.Empty;
        using (envelope.Doc)
        {
            var albums = new List<ServerAlbum>();
            var tracks = new List<Track>();
            var artIds = new Dictionary<Guid, string>();
            if (Root(envelope.Doc).TryGetProperty("searchResult3", out var result))
            {
                if (result.TryGetProperty("album", out var albumArray) && albumArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var album in albumArray.EnumerateArray())
                    {
                        var mapped = MapAlbum(album);
                        if (mapped != null) albums.Add(mapped);
                    }
                }
                if (result.TryGetProperty("song", out var songArray) && songArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var song in songArray.EnumerateArray())
                    {
                        var track = MapSong(connection, song, albumName: null, albumArtist: null);
                        tracks.Add(track);
                        if (GetId(song, "coverArt") is { Length: > 0 } coverArt)
                            artIds[track.Id] = coverArt;
                    }
                }
            }
            return new ServerSearchResult { Albums = albums, Tracks = tracks, TrackArtIds = artIds };
        }
    }

    public async Task<byte[]?> GetArtworkAsync(SourceConnection connection, string artId, int maxSize, CancellationToken ct = default)
    {
        var url = BuildUrl(connection, "getCoverArt", ("id", artId), ("size", maxSize.ToString()));
        if (url == null) return null;

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await HttpSafety.ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, ct);
            // Failed getCoverArt calls come back as a 200 JSON/XML envelope — reject non-images.
            return HttpSafety.LooksLikeImage(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The playable URL for a server song id, in the auth form negotiated for this connection.</summary>
    public static string? BuildStreamUrl(SourceConnection connection, string songId)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        return baseUrl == null
            ? null
            : BuildRequestUrl(baseUrl, connection.Username, connection.TokenOrPassword, "stream",
                connection.ApiVersion, connection.AuthMode, ("id", songId));
    }

    /// <summary>Newest-protocol, token-auth form (what a fresh connection starts with).</summary>
    internal static string BuildRequestUrl(string baseUrl, string username, string password, string method, params (string key, string value)[] extra)
        => BuildRequestUrl(baseUrl, username, password, method, DefaultApiVersion, SubsonicAuthMode.Token, extra);

    /// <summary>
    /// /rest/{method}.view URL with the standard u/v/c/f parameters plus either
    /// t/s (t = lowercase-hex md5(password+salt), fresh random salt per call) or the
    /// legacy p=enc:&lt;hex&gt; form.
    /// </summary>
    internal static string BuildRequestUrl(string baseUrl, string username, string password, string method,
        string apiVersion, SubsonicAuthMode mode, params (string key, string value)[] extra)
    {
        var sb = new StringBuilder(baseUrl.TrimEnd('/'))
            .Append("/rest/").Append(method).Append(".view")
            .Append("?u=").Append(Uri.EscapeDataString(username));

        if (mode == SubsonicAuthMode.Password)
        {
            sb.Append("&p=enc:").Append(Convert.ToHexString(Encoding.UTF8.GetBytes(password)).ToLowerInvariant());
        }
        else
        {
            // Crypto-RNG salt: Guid.NewGuid is random in practice but not contractually
            // a CSPRNG, and the salt is the only thing between the md5 token and replay.
            var salt = RandomNumberGenerator.GetHexString(12, lowercase: true);
            var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant();
            sb.Append("&t=").Append(token).Append("&s=").Append(salt);
        }

        sb.Append("&v=").Append(string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion)
          .Append("&c=").Append(ClientName)
          .Append("&f=json");

        foreach (var (key, value) in extra)
            sb.Append('&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));

        return sb.ToString();
    }

    private string? BuildUrl(SourceConnection connection, string method, params (string key, string value)[] extra)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        return baseUrl == null
            ? null
            : BuildRequestUrl(baseUrl, connection.Username, connection.TokenOrPassword, method,
                connection.ApiVersion, connection.AuthMode, extra);
    }

    private static JsonElement Root(JsonDocument doc) => doc.RootElement.GetProperty("subsonic-response");

    /// <summary>One decoded Subsonic response. <see cref="Doc"/> is non-null only for status "ok"; the caller disposes it.</summary>
    internal readonly record struct SubsonicEnvelope(JsonDocument? Doc, MediaServerError Error, string Message, int Code, string? ServerVersion);

    /// <summary>Fetches a Subsonic endpoint and classifies the outcome.</summary>
    private async Task<SubsonicEnvelope> GetSubsonicRootAsync(string url, CancellationToken ct)
    {
        string json;
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new(null, MediaServerError.ServerError, $"Server answered HTTP {(int)response.StatusCode}.", 0, null);
            json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // DNS/refused/TLS/timeout/oversize — classified for the status line.
            var fail = MediaServerConnectResult.FromTransportException(ex);
            return new(null, fail.Error, fail.Message, 0, null);
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var root))
            {
                doc.Dispose();
                return new(null, MediaServerError.ServerError, "Unexpected response — is this a Subsonic-compatible server?", 0, null);
            }

            var serverVersion = GetString(root, "version");
            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return new(doc, MediaServerError.None, string.Empty, 0, serverVersion);

            // status "failed": classify by the documented error codes.
            var code = 0;
            var serverMessage = string.Empty;
            if (root.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var c)) code = c;
                if (errorEl.TryGetProperty("message", out var msgEl)) serverMessage = msgEl.GetString() ?? string.Empty;
            }
            doc.Dispose();
            var (error, message) = ClassifyError(code, serverMessage);
            return new(null, error, message, code, serverVersion);
        }
        catch (JsonException)
        {
            doc?.Dispose();
            return new(null, MediaServerError.ServerError, "Unexpected response — is this a Subsonic-compatible server?", 0, null);
        }
    }

    /// <summary>The documented Subsonic error codes, each with a message a user can act on.</summary>
    internal static (MediaServerError Error, string Message) ClassifyError(int code, string serverMessage)
    {
        var detail = string.IsNullOrWhiteSpace(serverMessage) ? null : SanitizeServerMessage(serverMessage);
        return code switch
        {
            10 => (MediaServerError.ServerError, "The server rejected the request as incomplete" + Tail(detail)),
            20 => (MediaServerError.ServerError, "This server needs a newer version of Noctis" + Tail(detail)),
            30 => (MediaServerError.ServerError, "This server's Subsonic API is too old for Noctis" + Tail(detail)),
            40 => (MediaServerError.AuthFailed, "Wrong username or password."),
            41 => (MediaServerError.AuthFailed, "This server does not allow token login for this user" + Tail(detail)),
            50 => (MediaServerError.AuthFailed, "This account is not allowed to do that on the server" + Tail(detail)),
            60 => (MediaServerError.ServerError, "The server's trial period is over" + Tail(detail)),
            70 => (MediaServerError.ServerError, "Not found on the server" + Tail(detail)),
            _ => (MediaServerError.ServerError, detail ?? $"Server error (code {code})."),
        };

        static string Tail(string? detail) => detail == null ? "." : $": {detail}";
    }

    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        // "1.16.1", "1.15" and the odd "1.16.1-SNAPSHOT" all appear in the wild.
        var core = text.Trim();
        var cut = core.IndexOfAny(new[] { '-', ' ', '+' });
        if (cut > 0) core = core[..cut];
        if (!Version.TryParse(core, out var parsed)) return false;
        version = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>
    /// Server-supplied error text goes straight onto the Settings status line, so a
    /// hostile or broken server must not be able to flood the UI: control characters
    /// (including newlines) collapse to spaces and the length is hard-capped.
    /// </summary>
    internal static string SanitizeServerMessage(string message)
    {
        const int MaxLength = 200;
        var sb = new StringBuilder(Math.Min(message.Length, MaxLength + 1));
        foreach (var ch in message)
        {
            if (sb.Length == MaxLength)
            {
                sb.Append('…');
                break;
            }
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    private static ServerAlbum? MapAlbum(JsonElement album)
    {
        var id = GetId(album, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new ServerAlbum
        {
            Id = id,
            Name = GetString(album, "name") is { Length: > 0 } n ? n : "Unknown Album",
            Artist = GetString(album, "artist") ?? string.Empty,
            Year = GetInt(album, "year"),
            SongCount = GetInt(album, "songCount"),
            Duration = TimeSpan.FromSeconds(Math.Max(0, GetInt(album, "duration"))),
            CoverArtId = GetId(album, "coverArt")
        };
    }

    private Track MapSong(SourceConnection connection, JsonElement song, string? albumName, string? albumArtist)
    {
        var songId = GetId(song, "id") ?? Guid.NewGuid().ToString("N");
        var title = GetString(song, "title") is { Length: > 0 } t ? t : "Unknown Title";
        var album = albumName ?? GetString(song, "album") ?? "Unknown Album";
        var artist = ResolveTrackArtist(song, albumArtist ?? string.Empty);
        artist = MetadataService.EnrichArtistFromTitle(artist, title);
        var resolvedAlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist!;

        var track = new Track
        {
            Id = MediaServerUrl.DeterministicTrackId(connection.Id, songId),
            FilePath = BuildStreamUrl(connection, songId) ?? string.Empty,
            Title = title,
            Artist = artist,
            AlbumArtist = resolvedAlbumArtist,
            Album = album,
            Genre = GetString(song, "genre") ?? string.Empty,
            TrackNumber = GetInt(song, "track"),
            DiscNumber = Math.Max(1, GetInt(song, "discNumber", 1)),
            Year = GetInt(song, "year"),
            Duration = TimeSpan.FromSeconds(Math.Max(0, GetInt(song, "duration"))),
            FileSize = GetLong(song, "size"),
            Bitrate = GetInt(song, "bitRate"),
            SampleRate = GetInt(song, "samplingRate"),
            BitsPerSample = GetInt(song, "bitDepth"),
            Codec = GetString(song, "suffix") ?? string.Empty,
            LastModified = DateTime.UtcNow,
            DateAdded = DateTime.UtcNow,
            SourceType = SourceType.Navidrome,
            SourceTrackId = songId,
            SourceConnectionId = connection.Id.ToString("N")
        };

        track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
        return track;
    }

    /// <summary>Prefers the multi-artist "artists" array over the single "artist" field.</summary>
    private static string ResolveTrackArtist(JsonElement song, string albumArtist)
    {
        if (song.TryGetProperty("artists", out var artistsEl) &&
            artistsEl.ValueKind == JsonValueKind.Array &&
            artistsEl.GetArrayLength() > 0)
        {
            var names = new List<string>();
            foreach (var entry in artistsEl.EnumerateArray())
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            if (names.Count > 0) return string.Join(", ", names);
        }

        return GetString(song, "artist") is { Length: > 0 } a ? a : albumArtist;
    }

    /// <summary>
    /// Ids are opaque strings in the spec, but several servers (and older Subsonic
    /// itself) emit them as JSON numbers. Either form is accepted.
    /// </summary>
    private static string? GetId(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback = 0) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
