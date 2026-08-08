using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// Subsonic REST API client (Navidrome, Airsonic, Gonic, …). Speaks API v1.16.1
/// with per-request salted-token auth: t=md5(password+salt), s=salt, so the raw
/// password never travels in a URL (http://www.subsonic.org/pages/api.jsp).
/// The password itself must be kept (encrypted at rest by PersistenceService) to
/// derive fresh tokens; it is never logged.
/// </summary>
public sealed class SubsonicClient : IMediaServerClient
{
    private const string ApiVersion = "1.16.1";
    private const string ClientName = "Noctis";

    private readonly HttpClient _http;

    public SubsonicClient(HttpClient http) => _http = http;

    public SourceType SourceType => SourceType.Navidrome;

    public async Task<MediaServerConnectResult> ConnectAsync(SourceConnection connection, string password, CancellationToken ct = default)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out var urlError, out var urlMessage);
        if (baseUrl == null)
            return MediaServerConnectResult.Fail(urlError, urlMessage);

        connection.BaseUriOrPath = baseUrl;

        var (root, error, message) = await GetSubsonicRootAsync(
            BuildRequestUrl(baseUrl, connection.Username, password, "ping"), ct);
        using (root)
        {
            if (root == null)
                return MediaServerConnectResult.Fail(error, message);
        }

        // Credentials verified: keep the password for per-request token derivation.
        connection.TokenOrPassword = password;
        connection.UserId = string.Empty;

        // Cheap probe so "connected but empty" is called out immediately.
        var albums = await GetAlbumsAsync(connection, 0, 1, ct);
        return albums.Count == 0
            ? MediaServerConnectResult.Ok("Connected — no albums visible on this server yet.")
            : MediaServerConnectResult.Ok();
    }

    public async Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(SourceConnection connection, int offset, int limit, CancellationToken ct = default)
    {
        var url = BuildUrl(connection, "getAlbumList2",
            ("type", "alphabeticalByName"),
            ("size", Math.Clamp(limit, 1, 500).ToString()),
            ("offset", Math.Max(0, offset).ToString()));
        if (url == null) return Array.Empty<ServerAlbum>();

        var (root, _, _) = await GetSubsonicRootAsync(url, ct);
        if (root == null) return Array.Empty<ServerAlbum>();
        using (root)
        {
            var albums = new List<ServerAlbum>();
            if (Root(root).TryGetProperty("albumList2", out var list) &&
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

        var (root, _, _) = await GetSubsonicRootAsync(url, ct);
        if (root == null) return Array.Empty<Track>();
        using (root)
        {
            var tracks = new List<Track>();
            if (Root(root).TryGetProperty("album", out var albumEl) &&
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

        var (root, _, _) = await GetSubsonicRootAsync(url, ct);
        if (root == null) return ServerSearchResult.Empty;
        using (root)
        {
            var albums = new List<ServerAlbum>();
            var tracks = new List<Track>();
            var artIds = new Dictionary<Guid, string>();
            if (Root(root).TryGetProperty("searchResult3", out var result))
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
                        if (GetString(song, "coverArt") is { Length: > 0 } coverArt)
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

    /// <summary>The playable URL for a server song id (salted-token auth embedded, no raw password).</summary>
    public static string? BuildStreamUrl(SourceConnection connection, string songId)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        return baseUrl == null
            ? null
            : BuildRequestUrl(baseUrl, connection.Username, connection.TokenOrPassword, "stream", ("id", songId));
    }

    /// <summary>
    /// /rest/{method}.view URL with the standard u/t/s/v/c/f parameters
    /// (t = lowercase-hex md5(password+salt), fresh random salt per call).
    /// </summary>
    internal static string BuildRequestUrl(string baseUrl, string username, string password, string method, params (string key, string value)[] extra)
    {
        // Crypto-RNG salt: Guid.NewGuid is random in practice but not contractually
        // a CSPRNG, and the salt is the only thing between the md5 token and replay.
        var salt = RandomNumberGenerator.GetHexString(12, lowercase: true);
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant();

        var sb = new StringBuilder(baseUrl.TrimEnd('/'))
            .Append("/rest/").Append(method).Append(".view")
            .Append("?u=").Append(Uri.EscapeDataString(username))
            .Append("&t=").Append(token)
            .Append("&s=").Append(salt)
            .Append("&v=").Append(ApiVersion)
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
            : BuildRequestUrl(baseUrl, connection.Username, connection.TokenOrPassword, method, extra);
    }

    private static JsonElement Root(JsonDocument doc) => doc.RootElement.GetProperty("subsonic-response");

    /// <summary>
    /// Fetches a Subsonic endpoint and classifies the outcome. A non-null document
    /// means the envelope reported status "ok"; the caller must dispose it.
    /// </summary>
    private async Task<(JsonDocument? doc, MediaServerError error, string message)> GetSubsonicRootAsync(string url, CancellationToken ct)
    {
        string json;
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return (null, MediaServerError.ServerError, $"Server answered HTTP {(int)response.StatusCode}.");
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
            return (null, fail.Error, fail.Message);
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subsonic-response", out var root))
            {
                doc.Dispose();
                return (null, MediaServerError.ServerError, "Unexpected response — is this a Subsonic-compatible server?");
            }

            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return (doc, MediaServerError.None, string.Empty);

            // status "failed": classify by the documented error codes.
            var code = 0;
            var serverMessage = string.Empty;
            if (root.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var c)) code = c;
                if (errorEl.TryGetProperty("message", out var msgEl)) serverMessage = msgEl.GetString() ?? string.Empty;
            }
            doc.Dispose();
            return code switch
            {
                40 or 41 => (null, MediaServerError.AuthFailed, "Wrong username or password."),
                _ => (null, MediaServerError.ServerError,
                    string.IsNullOrWhiteSpace(serverMessage) ? $"Server error (code {code})." : SanitizeServerMessage(serverMessage))
            };
        }
        catch (JsonException)
        {
            doc?.Dispose();
            return (null, MediaServerError.ServerError, "Unexpected response — is this a Subsonic-compatible server?");
        }
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
        var id = album.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new ServerAlbum
        {
            Id = id,
            Name = GetString(album, "name") is { Length: > 0 } n ? n : "Unknown Album",
            Artist = GetString(album, "artist") ?? string.Empty,
            Year = GetInt(album, "year"),
            SongCount = GetInt(album, "songCount"),
            Duration = TimeSpan.FromSeconds(Math.Max(0, GetInt(album, "duration"))),
            CoverArtId = GetString(album, "coverArt")
        };
    }

    private Track MapSong(SourceConnection connection, JsonElement song, string? albumName, string? albumArtist)
    {
        var songId = GetString(song, "id") ?? Guid.NewGuid().ToString("N");
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

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback = 0) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
