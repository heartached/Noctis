using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>
/// The built-in Noctis server: an OpenSubsonic-compatible REST API over Kestrel, so any
/// Subsonic client (and the coming Noctis Android app) can browse, stream and sync the
/// desktop library. Started only when the user switches it on in Settings.
///
/// Security model: HTTPS with the install's own certificate (clients pin its fingerprint from
/// the QR); accounts from <see cref="ServerUserStore"/> with hashed passwords; auth per request
/// by API key (<c>apiKey</c>) or username + password (<c>u</c>/<c>p</c>, plain or <c>enc:</c>hex)
/// over TLS. The legacy md5 token scheme (<c>t</c>/<c>s</c>) is refused with error 41 because it
/// needs a recoverable password on the server. Only the library's own files are ever served,
/// addressed by id — never by path.
/// </summary>
public sealed class NoctisServer : IAsyncDisposable
{
    private readonly IServerLibrary _library;
    private readonly ServerUserStore _users;
    private readonly string _serverVersion;
    private readonly LoginThrottle _throttle = new();
    private WebApplication? _app;

    public NoctisServer(IServerLibrary library, ServerUserStore users, string serverVersion)
    {
        _library = library;
        _users = users;
        _serverVersion = serverVersion;
    }

    public bool IsRunning => _app is not null;

    /// <summary>The bound port (differs from the requested one when 0 was passed).</summary>
    public int Port { get; private set; }

    public bool IsHttps { get; private set; }

    /// <summary>Raised (from a worker thread) on every authenticated request; the UI shows "phone connected".</summary>
    public event EventHandler<string>? ClientAuthenticated;

    /// <summary>Starts listening on all interfaces. <paramref name="certificate"/> null = plain HTTP (LAN testing only).</summary>
    public async Task StartAsync(int port, X509Certificate2? certificate, CancellationToken ct = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "Noctis" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024; // form posts only; audio flows the other way
            k.Listen(IPAddress.Any, port, listen =>
            {
                if (certificate is not null) listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        Map(app);
        await app.StartAsync(ct).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Port = address is not null && Uri.TryCreate(address.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var uri) ? uri.Port : port;
        IsHttps = certificate is not null;
        _app = app;
        DebugLogger.Info(DebugLogger.Category.State, "Server", $"listening on {(IsHttps ? "https" : "http")}://*:{Port}");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync().ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
        DebugLogger.Info(DebugLogger.Category.State, "Server", "stopped");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    // ── Routing ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> NoAuthMethods = new(StringComparer.OrdinalIgnoreCase) { "ping", "getOpenSubsonicExtensions" };

    private void Map(WebApplication app)
    {
        // Subsonic: /rest/<method>[.view], GET or form POST, params in query or form.
        app.Map("/rest/{method}", HandleAsync);
        app.MapGet("/", () => Results.Text("Noctis server is running. Point a Subsonic client at this address.", "text/plain"));
    }

    private async Task HandleAsync(HttpContext ctx, string method)
    {
        if (method.EndsWith(".view", StringComparison.OrdinalIgnoreCase)) method = method[..^5];
        var p = await ReadParamsAsync(ctx).ConfigureAwait(false);
        var format = p.Get("f");

        try
        {
            if (!NoAuthMethods.Contains(method))
            {
                // Brute-force brake per remote address: after repeated bad logins every
                // attempt is refused for a while, before credentials are even checked.
                var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (_throttle.IsLocked(client, out var retryAfter))
                {
                    ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                    await WriteAsync(ctx, SubsonicResponse.Error(SubsonicResponse.ErrWrongCredentials,
                        "Too many failed login attempts. Try again later.", format, _serverVersion), 429).ConfigureAwait(false);
                    return;
                }

                var (user, error, errorMessage) = Authenticate(p);
                if (user is null)
                {
                    if (error is SubsonicResponse.ErrWrongCredentials or SubsonicResponse.ErrTokenAuthNotSupported)
                    {
                        if (_throttle.RecordFailure(client))
                            DebugLogger.Warn(DebugLogger.Category.State, "Server", $"login lockout for {client}");
                    }
                    await WriteAsync(ctx, SubsonicResponse.Error(error, errorMessage, format, _serverVersion), 200).ConfigureAwait(false);
                    return;
                }
                _throttle.RecordSuccess(client);
                ClientAuthenticated?.Invoke(this, user.Name);
                ctx.Items["user"] = user;
            }

            if (await TryHandleBinaryAsync(ctx, method, p).ConfigureAwait(false)) return;

            var payload = await DispatchAsync(method, p, ctx).ConfigureAwait(false);
            if (payload is null)
            {
                await WriteAsync(ctx, SubsonicResponse.Error(SubsonicResponse.ErrGeneric, $"Unknown method '{method}'", format, _serverVersion), 200).ConfigureAwait(false);
                return;
            }
            await WriteAsync(ctx, SubsonicResponse.Ok(payload, format, _serverVersion), 200).ConfigureAwait(false);
        }
        catch (SubsonicException ex)
        {
            await WriteAsync(ctx, SubsonicResponse.Error(ex.Code, ex.Message, format, _serverVersion), 200).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "Server", $"{method}: {ex.Message}");
            await WriteAsync(ctx, SubsonicResponse.Error(SubsonicResponse.ErrGeneric, "Server error", format, _serverVersion), 200).ConfigureAwait(false);
        }
    }

    private sealed class SubsonicException : Exception
    {
        public int Code { get; }
        public SubsonicException(int code, string message) : base(message) => Code = code;
    }

    private static SubsonicException Missing(string name) => new(SubsonicResponse.ErrMissingParameter, $"Required parameter '{name}' is missing");
    private static SubsonicException NotFound() => new(SubsonicResponse.ErrNotFound, "The requested data was not found");

    // ── Auth ────────────────────────────────────────────────────────────────

    private (ServerUser? User, int Error, string Message) Authenticate(Params p)
    {
        var apiKey = p.Get("apiKey");
        var u = p.Get("u");
        var pw = p.Get("p");
        var t = p.Get("t");

        if (apiKey is not null)
        {
            if (u is not null || pw is not null || t is not null)
                return (null, 43, "Multiple conflicting authentication mechanisms provided");
            var byKey = _users.ByApiKey(apiKey);
            return byKey is null ? (null, SubsonicResponse.ErrWrongCredentials, "Invalid API key") : (byKey, 0, "");
        }
        if (u is null) return (null, SubsonicResponse.ErrMissingParameter, "Required parameter 'u' is missing");
        if (t is not null || pw is null)
            return (null, SubsonicResponse.ErrTokenAuthNotSupported, "Token authentication not supported. Use an API key, or a password over HTTPS.");

        if (pw.StartsWith("enc:", StringComparison.OrdinalIgnoreCase))
        {
            try { pw = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(pw[4..])); }
            catch (FormatException) { return (null, SubsonicResponse.ErrWrongCredentials, "Wrong username or password"); }
        }
        var user = _users.Verify(u, pw);
        return user is null ? (null, SubsonicResponse.ErrWrongCredentials, "Wrong username or password") : (user, 0, "");
    }

    // ── Binary endpoints: stream / download / getCoverArt ───────────────────

    private async Task<bool> TryHandleBinaryAsync(HttpContext ctx, string method, Params p)
    {
        switch (method.ToLowerInvariant())
        {
            case "stream":
            case "download":
            {
                var id = p.Get("id") ?? throw Missing("id");
                var snap = await _library.SnapshotAsync().ConfigureAwait(false);
                var track = Ids.Track(snap, id) ?? throw NotFound();
                if (!File.Exists(track.FilePath)) throw NotFound();
                var contentType = ContentTypeFor(track.FilePath);
                ctx.Response.Headers.CacheControl = "private, max-age=0";
                if (method.Equals("download", StringComparison.OrdinalIgnoreCase))
                    ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(track.FilePath).Replace("\"", "")}\"";
                await Results.File(track.FilePath, contentType, enableRangeProcessing: true).ExecuteAsync(ctx).ConfigureAwait(false);
                return true;
            }
            case "getcoverart":
            {
                var id = p.Get("id") ?? throw Missing("id");
                var snap = await _library.SnapshotAsync().ConfigureAwait(false);
                var albumId = Ids.CoverAlbumId(snap, id) ?? throw NotFound();
                var path = _library.ArtworkPath(albumId) ?? throw NotFound();
                ctx.Response.Headers.CacheControl = "private, max-age=86400";
                await Results.File(path, ContentTypeFor(path)).ExecuteAsync(ctx).ConfigureAwait(false);
                return true;
            }
        }
        return false;
    }

    internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".m4a" or ".mp4" or ".alac" => "audio/mp4", ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg", ".opus" => "audio/ogg", ".wav" => "audio/wav", ".wma" => "audio/x-ms-wma",
        ".aif" or ".aiff" => "audio/aiff", ".ape" => "audio/x-ape", ".wv" => "audio/x-wavpack", ".dsf" => "audio/x-dsf",
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif",
        _ => "application/octet-stream",
    };

    // ── JSON endpoints ──────────────────────────────────────────────────────

    private async Task<JsonObject?> DispatchAsync(string method, Params p, HttpContext ctx)
    {
        switch (method.ToLowerInvariant())
        {
            case "ping": return new JsonObject();
            case "getlicense": return new JsonObject { ["license"] = new JsonObject { ["valid"] = true } };
            case "getopensubsonicextensions":
                return new JsonObject
                {
                    ["openSubsonicExtensions"] = new JsonArray(
                        new JsonObject { ["name"] = "apiKeyAuthentication", ["versions"] = new JsonArray(1) },
                        new JsonObject { ["name"] = "formPost", ["versions"] = new JsonArray(1) }),
                };
            case "getmusicfolders":
                return new JsonObject { ["musicFolders"] = new JsonObject { ["musicFolder"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Library" }) } };
            case "getuser":
            {
                var user = (ServerUser)ctx.Items["user"]!;
                return new JsonObject { ["user"] = UserObject(user) };
            }
        }

        var snap = await _library.SnapshotAsync().ConfigureAwait(false);
        var view = new LibraryView(snap);

        switch (method.ToLowerInvariant())
        {
            case "getartists":
            case "getindexes":
            {
                var index = view.ArtistIndex();
                var key = method.Equals("getindexes", StringComparison.OrdinalIgnoreCase) ? "indexes" : "artists";
                return new JsonObject { [key] = new JsonObject { ["ignoredArticles"] = "The El La Los Las Le Les", ["index"] = index } };
            }
            case "getartist":
            {
                var artist = view.Artist(p.Get("id") ?? throw Missing("id")) ?? throw NotFound();
                var obj = view.ArtistObject(artist);
                obj["album"] = new JsonArray(view.AlbumsOf(artist).Select(view.AlbumObject).ToArray());
                return new JsonObject { ["artist"] = obj };
            }
            case "getmusicdirectory":
            {
                var id = p.Get("id") ?? throw Missing("id");
                if (view.Artist(id) is { } artist)
                {
                    var children = view.AlbumsOf(artist).Select(a => { var o = view.AlbumObject(a); o["isDir"] = true; o["parent"] = Ids.ArtistId(artist); o["title"] = a.Name; return o; });
                    return new JsonObject { ["directory"] = new JsonObject { ["id"] = id, ["name"] = artist.Name, ["child"] = new JsonArray(children.ToArray()) } };
                }
                if (view.Album(id) is { } album)
                    return new JsonObject { ["directory"] = new JsonObject { ["id"] = id, ["parent"] = view.ArtistIdOf(album), ["name"] = album.Name, ["child"] = new JsonArray(view.SongsOf(album).Select(view.SongObject).ToArray()) } };
                throw NotFound();
            }
            case "getalbum":
            {
                var album = view.Album(p.Get("id") ?? throw Missing("id")) ?? throw NotFound();
                var obj = view.AlbumObject(album);
                obj["song"] = new JsonArray(view.SongsOf(album).Select(view.SongObject).ToArray());
                return new JsonObject { ["album"] = obj };
            }
            case "getsong":
            {
                var track = Ids.Track(snap, p.Get("id") ?? throw Missing("id")) ?? throw NotFound();
                return new JsonObject { ["song"] = view.SongObject(track) };
            }
            case "getalbumlist":
            case "getalbumlist2":
            {
                var type = p.Get("type") ?? throw Missing("type");
                var size = Math.Clamp(p.Int("size", 10), 1, 500);
                var offset = Math.Max(0, p.Int("offset", 0));
                var albums = view.AlbumList(type, p.Get("genre"), p.Int("fromYear", 0), p.Int("toYear", 0)).Skip(offset).Take(size);
                var key = method.Equals("getalbumlist", StringComparison.OrdinalIgnoreCase) ? "albumList" : "albumList2";
                return new JsonObject { [key] = new JsonObject { ["album"] = new JsonArray(albums.Select(view.AlbumObject).ToArray()) } };
            }
            case "getrandomsongs":
            {
                var size = Math.Clamp(p.Int("size", 10), 1, 500);
                var songs = snap.Tracks.OrderBy(_ => Random.Shared.Next()).Take(size);
                return new JsonObject { ["randomSongs"] = new JsonObject { ["song"] = new JsonArray(songs.Select(view.SongObject).ToArray()) } };
            }
            case "getgenres":
            {
                var genres = snap.Tracks.Where(t => !string.IsNullOrWhiteSpace(t.Genre)).GroupBy(t => t.Genre, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new JsonObject { ["value"] = g.Key, ["songCount"] = g.Count(), ["albumCount"] = g.Select(t => t.AlbumId).Distinct().Count() });
                return new JsonObject { ["genres"] = new JsonObject { ["genre"] = new JsonArray(genres.ToArray()) } };
            }
            case "getsongsbygenre":
            {
                var genre = p.Get("genre") ?? throw Missing("genre");
                var songs = snap.Tracks.Where(t => string.Equals(t.Genre, genre, StringComparison.OrdinalIgnoreCase))
                    .Skip(Math.Max(0, p.Int("offset", 0))).Take(Math.Clamp(p.Int("count", 10), 1, 500));
                return new JsonObject { ["songsByGenre"] = new JsonObject { ["song"] = new JsonArray(songs.Select(view.SongObject).ToArray()) } };
            }
            case "search2":
            case "search3":
            {
                var query = (p.Get("query") ?? "").Trim().Trim('"');
                var artists = view.SearchArtists(query).Skip(p.Int("artistOffset", 0)).Take(Math.Clamp(p.Int("artistCount", 20), 0, 500));
                var albums = view.SearchAlbums(query).Skip(p.Int("albumOffset", 0)).Take(Math.Clamp(p.Int("albumCount", 20), 0, 500));
                var songs = view.SearchSongs(query).Skip(p.Int("songOffset", 0)).Take(Math.Clamp(p.Int("songCount", 20), 0, 500));
                var key = method.Equals("search2", StringComparison.OrdinalIgnoreCase) ? "searchResult2" : "searchResult3";
                return new JsonObject
                {
                    [key] = new JsonObject
                    {
                        ["artist"] = new JsonArray(artists.Select(view.ArtistObject).ToArray()),
                        ["album"] = new JsonArray(albums.Select(view.AlbumObject).ToArray()),
                        ["song"] = new JsonArray(songs.Select(view.SongObject).ToArray()),
                    },
                };
            }
            case "getstarred":
            case "getstarred2":
            {
                var key = method.Equals("getstarred", StringComparison.OrdinalIgnoreCase) ? "starred" : "starred2";
                var starredTracks = snap.Tracks.Where(t => t.IsFavorite).ToList();
                var starredAlbums = snap.Albums.Where(a => a.Tracks.Count > 0 && a.Tracks.All(t => t.IsFavorite)).ToList();
                var starredArtists = snap.Artists.Where(a => a.IsFavorite).ToList();
                return new JsonObject
                {
                    [key] = new JsonObject
                    {
                        ["artist"] = new JsonArray(starredArtists.Select(view.ArtistObject).ToArray()),
                        ["album"] = new JsonArray(starredAlbums.Select(view.AlbumObject).ToArray()),
                        ["song"] = new JsonArray(starredTracks.Select(view.SongObject).ToArray()),
                    },
                };
            }
            case "star":
            case "unstar":
            {
                var starred = method.Equals("star", StringComparison.OrdinalIgnoreCase);
                var trackIds = p.All("id").Select(Ids.TrackGuid).Where(g => g.HasValue).Select(g => g!.Value).ToList();
                var albumIds = p.All("albumId").Concat(p.All("id")).Select(Ids.AlbumGuid).Where(g => g.HasValue).Select(g => g!.Value).ToList();
                var artistIds = p.All("artistId").Concat(p.All("id")).Select(Ids.ArtistGuid).Where(g => g.HasValue).Select(g => g!.Value).ToList();
                if (trackIds.Count + albumIds.Count + artistIds.Count == 0) throw Missing("id");
                await _library.SetStarredAsync(trackIds, albumIds, artistIds, starred).ConfigureAwait(false);
                return new JsonObject();
            }
            case "scrobble":
            {
                var submission = !string.Equals(p.Get("submission"), "false", StringComparison.OrdinalIgnoreCase);
                if (submission)
                    foreach (var id in p.All("id"))
                        if (Ids.TrackGuid(id) is { } g) await _library.ScrobbleAsync(g).ConfigureAwait(false);
                return new JsonObject();
            }
            case "getplaylists":
                return new JsonObject { ["playlists"] = new JsonObject { ["playlist"] = new JsonArray(snap.Playlists.Select(pl => view.PlaylistObject(pl, (ServerUser)ctx.Items["user"]!)).ToArray()) } };
            case "getplaylist":
            {
                var playlist = view.Playlist(p.Get("id") ?? throw Missing("id")) ?? throw NotFound();
                var obj = view.PlaylistObject(playlist, (ServerUser)ctx.Items["user"]!);
                obj["entry"] = new JsonArray(view.SongsOf(playlist).Select(view.SongObject).ToArray());
                return new JsonObject { ["playlist"] = obj };
            }
            case "createplaylist":
            {
                var existingId = p.Get("playlistId");
                var songIds = p.All("songId").Select(Ids.TrackGuid).Where(g => g.HasValue).Select(g => g!.Value).ToList();
                Playlist result;
                if (existingId is not null)
                {
                    var existing = view.Playlist(existingId) ?? throw NotFound();
                    // Subsonic semantics: createPlaylist with playlistId REPLACES the track list.
                    await _library.UpdatePlaylistAsync(existing.Id, p.Get("name"), songIds, Enumerable.Range(0, existing.TrackIds.Count).ToList()).ConfigureAwait(false);
                    result = (await _library.SnapshotAsync().ConfigureAwait(false)).Playlists.First(x => x.Id == existing.Id);
                }
                else
                {
                    result = await _library.CreatePlaylistAsync(p.Get("name") ?? throw Missing("name"), songIds).ConfigureAwait(false);
                }
                var fresh = new LibraryView(await _library.SnapshotAsync().ConfigureAwait(false));
                var obj = fresh.PlaylistObject(result, (ServerUser)ctx.Items["user"]!);
                obj["entry"] = new JsonArray(fresh.SongsOf(result).Select(fresh.SongObject).ToArray());
                return new JsonObject { ["playlist"] = obj };
            }
            case "updateplaylist":
            {
                var playlist = view.Playlist(p.Get("playlistId") ?? throw Missing("playlistId")) ?? throw NotFound();
                var add = p.All("songIdToAdd").Select(Ids.TrackGuid).Where(g => g.HasValue).Select(g => g!.Value).ToList();
                var remove = p.All("songIndexToRemove").Select(s => int.TryParse(s, out var i) ? i : -1).Where(i => i >= 0).ToList();
                await _library.UpdatePlaylistAsync(playlist.Id, p.Get("name"), add, remove).ConfigureAwait(false);
                return new JsonObject();
            }
            case "deleteplaylist":
            {
                var playlist = view.Playlist(p.Get("id") ?? throw Missing("id")) ?? throw NotFound();
                await _library.DeletePlaylistAsync(playlist.Id).ConfigureAwait(false);
                return new JsonObject();
            }
            case "getartistinfo":
            case "getartistinfo2":
                return new JsonObject { [method.Equals("getartistinfo", StringComparison.OrdinalIgnoreCase) ? "artistInfo" : "artistInfo2"] = new JsonObject() };
            case "getalbuminfo":
            case "getalbuminfo2":
                return new JsonObject { [method.Equals("getalbuminfo", StringComparison.OrdinalIgnoreCase) ? "albumInfo" : "albumInfo2"] = new JsonObject() };
            case "getscanstatus":
                return new JsonObject { ["scanStatus"] = new JsonObject { ["scanning"] = false, ["count"] = snap.Tracks.Count } };
            case "getplayqueue":
                return new JsonObject();
            case "getlyrics":
                return new JsonObject { ["lyrics"] = new JsonObject() };
            case "getbookmarks":
                return new JsonObject { ["bookmarks"] = new JsonObject() };
            case "getinternetradiostations":
                return new JsonObject { ["internetRadioStations"] = new JsonObject() };
            case "getpodcasts":
                return new JsonObject { ["podcasts"] = new JsonObject() };
        }
        return null;
    }

    private static JsonObject UserObject(ServerUser user) => new()
    {
        ["username"] = user.Name,
        ["scrobblingEnabled"] = true,
        ["adminRole"] = user.IsAdmin,
        ["settingsRole"] = false,
        ["downloadRole"] = true,
        ["uploadRole"] = false,
        ["playlistRole"] = true,
        ["coverArtRole"] = false,
        ["commentRole"] = false,
        ["podcastRole"] = false,
        ["streamRole"] = true,
        ["jukeboxRole"] = false,
        ["shareRole"] = false,
        ["videoConversionRole"] = false,
        ["folder"] = new JsonArray(1),
    };

    // ── Params ──────────────────────────────────────────────────────────────

    private sealed class Params
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
        public void Add(string key, string value) { if (!_values.TryGetValue(key, out var l)) _values[key] = l = new(); l.Add(value); }
        public string? Get(string key) => _values.TryGetValue(key, out var l) && l.Count > 0 ? l[0] : null;
        public IEnumerable<string> All(string key) => _values.TryGetValue(key, out var l) ? l : Enumerable.Empty<string>();
        public int Int(string key, int fallback) => int.TryParse(Get(key), out var i) ? i : fallback;
    }

    private static async Task<Params> ReadParamsAsync(HttpContext ctx)
    {
        var p = new Params();
        foreach (var (k, v) in ctx.Request.Query) foreach (var s in v) if (s is not null) p.Add(k, s);
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            foreach (var (k, v) in form) foreach (var s in v) if (s is not null) p.Add(k, s);
        }
        return p;
    }

    private static async Task WriteAsync(HttpContext ctx, (string Body, string ContentType) response, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = response.ContentType;
        await ctx.Response.WriteAsync(response.Body).ConfigureAwait(false);
    }
}

/// <summary>Subsonic ids for library entities: prefixed GUIDs so a client can never confuse kinds.</summary>
internal static class Ids
{
    public static string TrackId(Guid id) => "tr-" + id.ToString("N");
    public static string AlbumId(Guid id) => "al-" + id.ToString("N");
    public static string ArtistId(Artist a) => "ar-" + a.Id.ToString("N");
    public static string PlaylistId(Guid id) => "pl-" + id.ToString("N");

    public static Guid? TrackGuid(string? id) => Parse(id, "tr-");
    public static Guid? AlbumGuid(string? id) => Parse(id, "al-");
    public static Guid? ArtistGuid(string? id) => Parse(id, "ar-");
    public static Guid? PlaylistGuid(string? id) => Parse(id, "pl-");

    private static Guid? Parse(string? id, string prefix)
        => id is not null && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(id[3..], "N", out var g) ? g : null;

    public static Track? Track(LibrarySnapshot s, string id) => TrackGuid(id) is { } g ? s.Tracks.FirstOrDefault(t => t.Id == g) : null;

    /// <summary>Cover art ids may be an album, a track (→ its album) or a playlist (→ first track's album).</summary>
    public static Guid? CoverAlbumId(LibrarySnapshot s, string id)
    {
        if (AlbumGuid(id) is { } a) return a;
        if (TrackGuid(id) is { } t) return s.Tracks.FirstOrDefault(x => x.Id == t)?.AlbumId;
        if (PlaylistGuid(id) is { } p)
        {
            var first = s.Playlists.FirstOrDefault(x => x.Id == p)?.TrackIds.FirstOrDefault();
            return first is { } f ? s.Tracks.FirstOrDefault(x => x.Id == f)?.AlbumId : null;
        }
        return null;
    }
}

/// <summary>Builds Subsonic JSON objects from one library snapshot (lookups are indexed once per request).</summary>
internal sealed class LibraryView
{
    private readonly LibrarySnapshot _s;
    private readonly Dictionary<Guid, Track> _tracks;
    private readonly Dictionary<Guid, Album> _albums;
    private readonly Dictionary<string, Artist> _artistsByName;
    private readonly ILookup<Guid, Track> _tracksByAlbum;

    public LibraryView(LibrarySnapshot s)
    {
        _s = s;
        _tracks = s.Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        _albums = s.Albums.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        _artistsByName = s.Artists.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _tracksByAlbum = s.Tracks.ToLookup(t => t.AlbumId);
    }

    public Artist? Artist(string id) => Ids.ArtistGuid(id) is { } g ? _s.Artists.FirstOrDefault(a => a.Id == g) : null;
    public Album? Album(string id) => Ids.AlbumGuid(id) is { } g && _albums.TryGetValue(g, out var a) ? a : null;
    public Playlist? Playlist(string id) => Ids.PlaylistGuid(id) is { } g ? _s.Playlists.FirstOrDefault(p => p.Id == g) : null;

    public IEnumerable<Album> AlbumsOf(Artist artist)
        => _s.Albums.Where(a => string.Equals(a.Artist, artist.Name, StringComparison.OrdinalIgnoreCase)
                             || _tracksByAlbum[a.Id].Any(t => string.Equals(t.AlbumArtist, artist.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Year).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Track> SongsOf(Album album)
        => _tracksByAlbum[album.Id].OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Track> SongsOf(Playlist playlist)
        => playlist.TrackIds.Select(id => _tracks.TryGetValue(id, out var t) ? t : null).Where(t => t is not null)!;

    public string? ArtistIdOf(Album album)
        => _artistsByName.TryGetValue(album.Artist ?? "", out var a) ? Ids.ArtistId(a) : null;

    public JsonArray ArtistIndex()
    {
        var groups = _s.Artists
            .OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
            .GroupBy(a => IndexLetter(SortName(a.Name)))
            .OrderBy(g => g.Key == "#" ? "￿" : g.Key, StringComparer.Ordinal);
        return new JsonArray(groups.Select(g => new JsonObject
        {
            ["name"] = g.Key,
            ["artist"] = new JsonArray(g.Select(ArtistObject).ToArray()),
        }).ToArray());
    }

    private static string SortName(string name)
    {
        foreach (var article in new[] { "The ", "El ", "La ", "Los ", "Las ", "Le ", "Les " })
            if (name.StartsWith(article, StringComparison.OrdinalIgnoreCase) && name.Length > article.Length) return name[article.Length..];
        return name;
    }

    private static string IndexLetter(string sortName)
    {
        var c = sortName.Length > 0 ? char.ToUpperInvariant(sortName[0]) : '#';
        return char.IsLetter(c) ? c.ToString() : "#";
    }

    public JsonObject ArtistObject(Artist a)
    {
        var obj = new JsonObject
        {
            ["id"] = Ids.ArtistId(a),
            ["name"] = a.Name,
            ["albumCount"] = a.AlbumCount > 0 ? a.AlbumCount : AlbumsOf(a).Count(),
        };
        var firstAlbum = AlbumsOf(a).FirstOrDefault();
        if (firstAlbum is not null) obj["coverArt"] = Ids.AlbumId(firstAlbum.Id);
        if (a.IsFavorite) obj["starred"] = DateTime.UtcNow.ToString("O");
        return obj;
    }

    public JsonObject AlbumObject(Album a)
    {
        var songs = _tracksByAlbum[a.Id].ToList();
        var obj = new JsonObject
        {
            ["id"] = Ids.AlbumId(a.Id),
            ["name"] = a.Name,
            ["title"] = a.Name,
            ["album"] = a.Name,
            ["artist"] = a.Artist,
            ["isDir"] = true,
            ["coverArt"] = Ids.AlbumId(a.Id),
            ["songCount"] = songs.Count > 0 ? songs.Count : a.TrackCount,
            ["duration"] = (int)(songs.Count > 0 ? songs.Sum(t => t.Duration.TotalSeconds) : a.TotalDuration.TotalSeconds),
            ["playCount"] = songs.Sum(t => t.PlayCount),
            ["created"] = (songs.Count > 0 ? songs.Min(t => t.DateAdded) : DateTime.UtcNow).ToString("O"),
        };
        if (ArtistIdOf(a) is { } artistId) { obj["artistId"] = artistId; obj["parent"] = artistId; }
        if (a.Year > 0) obj["year"] = a.Year;
        if (!string.IsNullOrWhiteSpace(a.Genre) && a.Genre != "Unknown") obj["genre"] = a.Genre;
        if (songs.Count > 0 && songs.All(t => t.IsFavorite)) obj["starred"] = DateTime.UtcNow.ToString("O");
        return obj;
    }

    public JsonObject SongObject(Track t)
    {
        var obj = new JsonObject
        {
            ["id"] = Ids.TrackId(t.Id),
            ["parent"] = Ids.AlbumId(t.AlbumId),
            ["albumId"] = Ids.AlbumId(t.AlbumId),
            ["isDir"] = false,
            ["title"] = t.Title,
            ["album"] = t.Album,
            ["artist"] = t.Artist,
            ["coverArt"] = Ids.AlbumId(t.AlbumId),
            ["duration"] = (int)t.Duration.TotalSeconds,
            ["size"] = t.FileSize,
            ["contentType"] = NoctisServer.ContentTypeFor(t.FilePath),
            ["suffix"] = Path.GetExtension(t.FilePath).TrimStart('.').ToLowerInvariant(),
            ["path"] = $"{Sanitize(t.Artist)}/{Sanitize(t.Album)}/{Path.GetFileName(t.FilePath)}",
            ["type"] = "music",
            ["playCount"] = t.PlayCount,
            ["created"] = t.DateAdded.ToString("O"),
        };
        if (t.TrackNumber > 0) obj["track"] = t.TrackNumber;
        if (t.DiscNumber > 0) obj["discNumber"] = t.DiscNumber;
        if (t.Year > 0) obj["year"] = t.Year;
        if (!string.IsNullOrWhiteSpace(t.Genre)) obj["genre"] = t.Genre;
        if (t.Bitrate > 0) obj["bitRate"] = t.Bitrate;
        if (t.SampleRate > 0) obj["samplingRate"] = t.SampleRate;
        if (t.Rating > 0) obj["userRating"] = t.Rating;
        if (t.IsFavorite) obj["starred"] = DateTime.UtcNow.ToString("O");
        if (_artistsByName.TryGetValue(t.Artist ?? "", out var artist)) obj["artistId"] = Ids.ArtistId(artist);
        return obj;
    }

    public JsonObject PlaylistObject(Playlist p, ServerUser owner)
    {
        var songs = SongsOf(p).ToList();
        var obj = new JsonObject
        {
            ["id"] = Ids.PlaylistId(p.Id),
            ["name"] = p.Name,
            ["songCount"] = songs.Count,
            ["duration"] = (int)songs.Sum(t => t.Duration.TotalSeconds),
            ["public"] = true,
            ["owner"] = owner.Name,
            ["created"] = p.CreatedAt.ToString("O"),
            ["changed"] = p.ModifiedAt.ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(p.Description)) obj["comment"] = p.Description;
        if (songs.Count > 0) obj["coverArt"] = Ids.PlaylistId(p.Id);
        return obj;
    }

    // ── Lists & search ──

    public IEnumerable<Album> AlbumList(string type, string? genre, int fromYear, int toYear)
    {
        IEnumerable<Album> all = _s.Albums;
        return type.ToLowerInvariant() switch
        {
            "random" => all.OrderBy(_ => Random.Shared.Next()),
            "newest" => all.OrderByDescending(a => _tracksByAlbum[a.Id].Select(t => t.DateAdded).DefaultIfEmpty(DateTime.MinValue).Max()),
            "frequent" => all.OrderByDescending(a => _tracksByAlbum[a.Id].Sum(t => t.PlayCount)),
            "recent" => all.OrderByDescending(a => _tracksByAlbum[a.Id].Select(t => t.LastPlayed ?? DateTime.MinValue).DefaultIfEmpty(DateTime.MinValue).Max()),
            "starred" => all.Where(a => _tracksByAlbum[a.Id].Any() && _tracksByAlbum[a.Id].All(t => t.IsFavorite)),
            "alphabeticalbyartist" => all.OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            "byyear" => (fromYear <= toYear
                    ? all.Where(a => a.Year >= fromYear && a.Year <= toYear).OrderBy(a => a.Year)
                    : all.Where(a => a.Year >= toYear && a.Year <= fromYear).OrderByDescending(a => a.Year)),
            "bygenre" => all.Where(a => string.Equals(a.Genre, genre, StringComparison.OrdinalIgnoreCase)
                                     || _tracksByAlbum[a.Id].Any(t => string.Equals(t.Genre, genre, StringComparison.OrdinalIgnoreCase))),
            "highest" => all.OrderByDescending(a => _tracksByAlbum[a.Id].Select(t => t.Rating).DefaultIfEmpty(0).Average()),
            _ => all.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), // alphabeticalByName
        };
    }

    public IEnumerable<Artist> SearchArtists(string q)
        => q.Length == 0 ? _s.Artists.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         : _s.Artists.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Album> SearchAlbums(string q)
        => q.Length == 0 ? _s.Albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         : _s.Albums.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (a.Artist ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Track> SearchSongs(string q)
        => q.Length == 0 ? _s.Tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                         : _s.Tracks.Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                             || t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)
                                             || t.Album.Contains(q, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase);

    private static string Sanitize(string? s) => string.IsNullOrWhiteSpace(s) ? "Unknown" : string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
