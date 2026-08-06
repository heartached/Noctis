using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MediaServer;

/// <summary>
/// Jellyfin REST client. Authenticates once via POST /Users/AuthenticateByName with
/// the "Authorization: MediaBrowser Client=…, Device=…, DeviceId=…, Version=…" scheme
/// and from then on only the returned AccessToken + UserId are kept — the password is
/// never persisted (https://gist.github.com/nielsvanvelzen/ea047d9028f676185832e51ffaf12a6f).
/// Browsing uses /Users/{userId}/Items queries; playback uses
/// /Audio/{id}/stream?static=true ("the original file will be streamed statically
/// without any encoding" — Jellyfin OpenAPI), authenticated with the api_key query
/// parameter because LibVLC cannot attach request headers.
/// </summary>
public sealed class JellyfinClient : IMediaServerClient
{
    private const string ClientName = "Noctis";

    private static readonly string AppVersion =
        (typeof(JellyfinClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(JellyfinClient).Assembly.GetName().Version?.ToString()
         ?? "1.0").Split('+')[0];

    private readonly HttpClient _http;

    public JellyfinClient(HttpClient http) => _http = http;

    public SourceType SourceType => SourceType.Jellyfin;

    public async Task<MediaServerConnectResult> ConnectAsync(SourceConnection connection, string password, CancellationToken ct = default)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out var urlError, out var urlMessage);
        if (baseUrl == null)
            return MediaServerConnectResult.Fail(urlError, urlMessage);

        connection.BaseUriOrPath = baseUrl;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(connection, token: null));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { Username = connection.Username, Pw = password }),
            Encoding.UTF8, "application/json");

        string body;
        HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            status = response.StatusCode;
            body = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MediaServerConnectResult.Fail(MediaServerError.Unreachable,
                "Couldn't reach the server. Check the URL and that the server is running.");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return MediaServerConnectResult.Fail(MediaServerError.AuthFailed, "Wrong username or password.");
        if (status != HttpStatusCode.OK)
            return MediaServerConnectResult.Fail(MediaServerError.ServerError, $"Server answered HTTP {(int)status}.");

        string? accessToken = null;
        string? userId = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("AccessToken", out var tokenEl))
                accessToken = tokenEl.GetString();
            if (doc.RootElement.TryGetProperty("User", out var userEl) &&
                userEl.TryGetProperty("Id", out var idEl))
                userId = idEl.GetString();
        }
        catch (JsonException)
        {
            // fall through to the null check below
        }

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(userId))
            return MediaServerConnectResult.Fail(MediaServerError.ServerError,
                "Unexpected response — is this a Jellyfin server?");

        // Keep the token + user id; the password is deliberately dropped.
        connection.TokenOrPassword = accessToken;
        connection.UserId = userId;

        // Distinguish "connected" from "connected but no music" (a movie-only server).
        var probe = await QueryItemsAsync(connection,
            "IncludeItemTypes=Audio&Recursive=true&Limit=1", ct);
        return probe is { TotalCount: 0 }
            ? MediaServerConnectResult.Ok("Connected — no music found in this Jellyfin library yet.")
            : MediaServerConnectResult.Ok();
    }

    public async Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(SourceConnection connection, int offset, int limit, CancellationToken ct = default)
    {
        var query = "IncludeItemTypes=MusicAlbum&Recursive=true&SortBy=SortName&SortOrder=Ascending" +
                    $"&StartIndex={Math.Max(0, offset)}&Limit={Math.Clamp(limit, 1, 500)}";
        var result = await QueryItemsAsync(connection, query, ct);
        if (result == null) return Array.Empty<ServerAlbum>();

        var albums = new List<ServerAlbum>();
        foreach (var item in result.Items)
        {
            var mapped = MapAlbum(item);
            if (mapped != null) albums.Add(mapped);
        }
        result.Dispose();
        return albums;
    }

    public async Task<IReadOnlyList<Track>> GetAlbumTracksAsync(SourceConnection connection, ServerAlbum album, CancellationToken ct = default)
    {
        var query = $"ParentId={Uri.EscapeDataString(album.Id)}&IncludeItemTypes=Audio" +
                    "&SortBy=ParentIndexNumber,IndexNumber&SortOrder=Ascending";
        var result = await QueryItemsAsync(connection, query, ct);
        if (result == null) return Array.Empty<Track>();

        var tracks = new List<Track>();
        foreach (var item in result.Items)
            tracks.Add(MapSong(connection, item, album));
        result.Dispose();
        return tracks;
    }

    public async Task<ServerSearchResult> SearchAsync(SourceConnection connection, string query, CancellationToken ct = default)
    {
        var term = Uri.EscapeDataString(query);
        var albumTask = QueryItemsAsync(connection,
            $"searchTerm={term}&IncludeItemTypes=MusicAlbum&Recursive=true&Limit=24", ct);
        var songTask = QueryItemsAsync(connection,
            $"searchTerm={term}&IncludeItemTypes=Audio&Recursive=true&Limit=50", ct);

        var albums = new List<ServerAlbum>();
        var tracks = new List<Track>();
        var artIds = new Dictionary<Guid, string>();

        var albumResult = await albumTask;
        if (albumResult != null)
        {
            foreach (var item in albumResult.Items)
            {
                var mapped = MapAlbum(item);
                if (mapped != null) albums.Add(mapped);
            }
            albumResult.Dispose();
        }

        var songResult = await songTask;
        if (songResult != null)
        {
            foreach (var item in songResult.Items)
            {
                var track = MapSong(connection, item, album: null);
                tracks.Add(track);
                // Song hits carry the parent album's item id; its Primary image is the cover.
                if (GetString(item, "AlbumId") is { Length: > 0 } albumItemId)
                    artIds[track.Id] = albumItemId;
            }
            songResult.Dispose();
        }

        return new ServerSearchResult { Albums = albums, Tracks = tracks, TrackArtIds = artIds };
    }

    public async Task<byte[]?> GetArtworkAsync(SourceConnection connection, string artId, int maxSize, CancellationToken ct = default)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        if (baseUrl == null || string.IsNullOrWhiteSpace(artId)) return null;

        // Fetched through HttpClient with header auth, so no token-bearing image URLs
        // ever reach UI bindings or logs.
        var url = $"{baseUrl}/Items/{Uri.EscapeDataString(artId)}/Images/Primary?maxWidth={maxSize}&quality=90";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(connection, connection.TokenOrPassword));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await HttpSafety.ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, ct);
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

    /// <summary>
    /// Direct-play URL for a Jellyfin audio item: static=true streams the original
    /// file without transcoding; api_key carries the access token because media
    /// players can't send the Authorization header.
    /// </summary>
    public static string? BuildStreamUrl(SourceConnection connection, string itemId)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        if (baseUrl == null || string.IsNullOrWhiteSpace(itemId)) return null;
        return $"{baseUrl}/Audio/{Uri.EscapeDataString(itemId)}/stream?static=true&api_key={Uri.EscapeDataString(connection.TokenOrPassword)}";
    }

    /// <summary>
    /// "MediaBrowser Client=…, Device=…, DeviceId=…, Version=…[, Token=…]" — the
    /// DeviceId is the connection guid so the server sees one stable device per setup.
    /// </summary>
    internal static string BuildAuthHeader(SourceConnection connection, string? token)
    {
        var device = Uri.EscapeDataString(Environment.MachineName is { Length: > 0 } m ? m : "Desktop");
        var sb = new StringBuilder("MediaBrowser Client=\"").Append(ClientName)
            .Append("\", Device=\"").Append(device)
            .Append("\", DeviceId=\"").Append(connection.Id.ToString("N"))
            .Append("\", Version=\"").Append(AppVersion).Append('"');
        if (!string.IsNullOrEmpty(token))
            sb.Append(", Token=\"").Append(token).Append('"');
        return sb.ToString();
    }

    private sealed class ItemsResult : IDisposable
    {
        private readonly JsonDocument _doc;
        public int TotalCount { get; }
        public IEnumerable<JsonElement> Items
        {
            get
            {
                if (_doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                        yield return item;
            }
        }

        public ItemsResult(JsonDocument doc)
        {
            _doc = doc;
            TotalCount = doc.RootElement.TryGetProperty("TotalRecordCount", out var count) &&
                         count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var c)
                ? c
                : -1;
        }

        public void Dispose() => _doc.Dispose();
    }

    /// <summary>GET /Users/{userId}/Items?{query} with header auth; null on any failure.</summary>
    private async Task<ItemsResult?> QueryItemsAsync(SourceConnection connection, string query, CancellationToken ct)
    {
        var baseUrl = MediaServerUrl.TryNormalizeBase(connection.BaseUriOrPath, out _, out _);
        if (baseUrl == null || string.IsNullOrWhiteSpace(connection.UserId)) return null;

        var url = $"{baseUrl}/Users/{Uri.EscapeDataString(connection.UserId)}/Items?{query}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(connection, connection.TokenOrPassword));
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct);
            return new ItemsResult(JsonDocument.Parse(json));
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

    private static ServerAlbum? MapAlbum(JsonElement item)
    {
        var id = GetString(item, "Id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var hasPrimaryImage = item.TryGetProperty("ImageTags", out var tags) &&
                              tags.ValueKind == JsonValueKind.Object &&
                              tags.TryGetProperty("Primary", out _);
        return new ServerAlbum
        {
            Id = id,
            Name = GetString(item, "Name") is { Length: > 0 } n ? n : "Unknown Album",
            Artist = GetString(item, "AlbumArtist") ?? string.Empty,
            Year = GetInt(item, "ProductionYear"),
            SongCount = GetInt(item, "ChildCount"),
            Duration = TimeSpan.FromTicks(Math.Max(0, GetLong(item, "RunTimeTicks"))),
            CoverArtId = hasPrimaryImage ? id : null
        };
    }

    private static Track MapSong(SourceConnection connection, JsonElement item, ServerAlbum? album)
    {
        var itemId = GetString(item, "Id") ?? Guid.NewGuid().ToString("N");
        var title = GetString(item, "Name") is { Length: > 0 } t ? t : "Unknown Title";
        var albumName = GetString(item, "Album") ?? album?.Name ?? "Unknown Album";
        var albumArtist = GetString(item, "AlbumArtist") ?? album?.Artist ?? string.Empty;
        var artist = ResolveArtists(item, albumArtist);
        artist = MetadataService.EnrichArtistFromTitle(artist, title);
        if (string.IsNullOrWhiteSpace(albumArtist)) albumArtist = artist;

        var track = new Track
        {
            Id = MediaServerUrl.DeterministicTrackId(connection.Id, itemId),
            FilePath = BuildStreamUrl(connection, itemId) ?? string.Empty,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = albumName,
            Genre = FirstOfArray(item, "Genres") ?? string.Empty,
            TrackNumber = GetInt(item, "IndexNumber"),
            DiscNumber = Math.Max(1, GetInt(item, "ParentIndexNumber", 1)),
            Year = GetInt(item, "ProductionYear"),
            Duration = TimeSpan.FromTicks(Math.Max(0, GetLong(item, "RunTimeTicks"))),
            Codec = GetString(item, "Container") ?? string.Empty,
            LastModified = DateTime.UtcNow,
            DateAdded = DateTime.UtcNow,
            SourceType = SourceType.Jellyfin,
            SourceTrackId = itemId,
            SourceConnectionId = connection.Id.ToString("N")
        };

        track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
        return track;
    }

    private static string ResolveArtists(JsonElement item, string fallback)
    {
        if (item.TryGetProperty("Artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array &&
            artists.GetArrayLength() > 0)
        {
            var names = new List<string>();
            foreach (var entry in artists.EnumerateArray())
            {
                var name = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            if (names.Count > 0) return string.Join(", ", names);
        }
        return fallback;
    }

    private static string? FirstOfArray(JsonElement item, string name)
    {
        if (item.TryGetProperty(name, out var array) &&
            array.ValueKind == JsonValueKind.Array &&
            array.GetArrayLength() > 0)
        {
            var first = array[0];
            if (first.ValueKind == JsonValueKind.String) return first.GetString();
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name, int fallback = 0) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}
