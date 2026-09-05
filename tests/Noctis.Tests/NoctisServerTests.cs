using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Noctis.Models;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// End-to-end over real Kestrel on a random loopback port (plain HTTP — TLS is covered by the
/// certificate tests): auth rules, JSON and XML envelopes, browsing, search, star/scrobble
/// side effects, playlists, streaming with range requests, and the id scheme.
/// </summary>
public class NoctisServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-server-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private FakeServerLibrary _lib = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private string _apiKey = "";

    private static readonly Guid AlbumA = Guid.NewGuid(), AlbumB = Guid.NewGuid();
    private static readonly Guid ArtistX = Guid.NewGuid(), ArtistY = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");

        var audio = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(audio, Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray());
        var art = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(art, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });

        var t1 = new Track { Id = Guid.NewGuid(), Title = "Alpha", Artist = "The Xylophones", AlbumArtist = "The Xylophones", Album = "First", AlbumId = AlbumA, FilePath = audio, Duration = TimeSpan.FromSeconds(200), TrackNumber = 1, Year = 2001, Genre = "Rock", FileSize = 5000, Bitrate = 320 };
        var t2 = new Track { Id = Guid.NewGuid(), Title = "Beta", Artist = "The Xylophones", AlbumArtist = "The Xylophones", Album = "First", AlbumId = AlbumA, FilePath = audio, Duration = TimeSpan.FromSeconds(100), TrackNumber = 2, Year = 2001, Genre = "Rock", FileSize = 5000 };
        var t3 = new Track { Id = Guid.NewGuid(), Title = "Gamma", Artist = "Yolanda", AlbumArtist = "Yolanda", Album = "Second", AlbumId = AlbumB, FilePath = audio, Duration = TimeSpan.FromSeconds(50), TrackNumber = 1, Year = 2010, Genre = "Jazz", FileSize = 5000, IsFavorite = true };
        _lib = new FakeServerLibrary(
            new[] { t1, t2, t3 },
            new[]
            {
                new Album { Id = AlbumA, Name = "First", Artist = "The Xylophones", Year = 2001, Genre = "Rock", TrackCount = 2 },
                new Album { Id = AlbumB, Name = "Second", Artist = "Yolanda", Year = 2010, Genre = "Jazz", TrackCount = 1 },
            },
            new[] { new Artist { Id = ArtistX, Name = "The Xylophones", AlbumCount = 1 }, new Artist { Id = ArtistY, Name = "Yolanda", AlbumCount = 1 } },
            new List<Playlist> { new() { Name = "Mix", TrackIds = { t1.Id, t3.Id } } },
            artworkPath: art);

        _server = new NoctisServer(_lib, _users, "test");
        await _server.StartAsync(0, certificate: null);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Task<JsonElement> Get(string method, string query = "", bool auth = true)
        => GetJson($"rest/{method}.view?f=json{(auth ? "&apiKey=" + _apiKey : "")}{(query.Length > 0 ? "&" + query : "")}");

    private async Task<JsonElement> GetJson(string url)
    {
        var json = await _http.GetStringAsync(url);
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
    }

    [Fact]
    public async Task Ping_NeedsNoAuth_AndAdvertisesOpenSubsonic()
    {
        var r = await Get("ping", auth: false);
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal("Noctis", r.GetProperty("type").GetString());
        Assert.True(r.GetProperty("openSubsonic").GetBoolean());
    }

    [Fact]
    public async Task Xml_IsTheDefault_WithTheSubsonicNamespace()
    {
        var xml = await _http.GetStringAsync($"rest/getMusicFolders.view?apiKey={_apiKey}");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://subsonic.org/restapi";
        Assert.Equal("ok", doc.Root!.Attribute("status")!.Value);
        Assert.Equal(ns + "subsonic-response", doc.Root.Name);
        Assert.Equal("Library", doc.Root.Element(ns + "musicFolders")!.Element(ns + "musicFolder")!.Attribute("name")!.Value);
    }

    [Fact]
    public async Task Auth_ApiKey_Password_EncPassword_Work_TokenIsRefused_WrongIsRefused()
    {
        Assert.Equal("ok", (await GetJson($"rest/ping.view?f=json&apiKey={_apiKey}")).GetProperty("status").GetString());
        Assert.Equal("ok", (await GetJson("rest/getLicense.view?f=json&u=alice&p=correct%20horse")).GetProperty("status").GetString());
        var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("correct horse"));
        Assert.Equal("ok", (await GetJson($"rest/getLicense.view?f=json&u=alice&p=enc:{hex}")).GetProperty("status").GetString());

        var token = await GetJson("rest/getLicense.view?f=json&u=alice&t=deadbeef&s=salt");
        Assert.Equal("failed", token.GetProperty("status").GetString());
        Assert.Equal(41, token.GetProperty("error").GetProperty("code").GetInt32());

        var wrong = await GetJson("rest/getLicense.view?f=json&u=alice&p=nope");
        Assert.Equal(40, wrong.GetProperty("error").GetProperty("code").GetInt32());

        var badKey = await GetJson("rest/getLicense.view?f=json&apiKey=nk_garbage");
        Assert.Equal(40, badKey.GetProperty("error").GetProperty("code").GetInt32());

        var none = await GetJson("rest/getLicense.view?f=json");
        Assert.Equal(10, none.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Browse_ArtistsAlbumsSongs_AreLinkedByIds()
    {
        var artists = await Get("getArtists");
        var index = artists.GetProperty("artists").GetProperty("index").EnumerateArray().ToList();
        // "The Xylophones" sorts under X (article ignored), "Yolanda" under Y.
        Assert.Equal(new[] { "X", "Y" }, index.Select(i => i.GetProperty("name").GetString()).ToArray());
        var xId = index[0].GetProperty("artist")[0].GetProperty("id").GetString()!;
        Assert.StartsWith("ar-", xId);

        var artist = await Get("getArtist", $"id={xId}");
        var album = artist.GetProperty("artist").GetProperty("album")[0];
        Assert.Equal("First", album.GetProperty("name").GetString());
        var albumId = album.GetProperty("id").GetString()!;
        Assert.Equal(xId, album.GetProperty("artistId").GetString());

        var full = await Get("getAlbum", $"id={albumId}");
        var songs = full.GetProperty("album").GetProperty("song").EnumerateArray().ToList();
        Assert.Equal(new[] { "Alpha", "Beta" }, songs.Select(s => s.GetProperty("title").GetString()).ToArray());
        Assert.Equal("audio/mpeg", songs[0].GetProperty("contentType").GetString());
        Assert.Equal(albumId, songs[0].GetProperty("coverArt").GetString());

        var one = await Get("getSong", $"id={songs[1].GetProperty("id").GetString()}");
        Assert.Equal("Beta", one.GetProperty("song").GetProperty("title").GetString());

        var missing = await Get("getSong", "id=tr-00000000000000000000000000000000");
        Assert.Equal(70, missing.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Search_Lists_Genres_Starred()
    {
        var search = await Get("search3", "query=gam");
        Assert.Equal("Gamma", search.GetProperty("searchResult3").GetProperty("song")[0].GetProperty("title").GetString());

        var everything = await Get("search3", "query=&songCount=500");
        Assert.Equal(3, everything.GetProperty("searchResult3").GetProperty("song").GetArrayLength());

        var newest = await Get("getAlbumList2", "type=alphabeticalByName&size=1");
        Assert.Equal("First", newest.GetProperty("albumList2").GetProperty("album")[0].GetProperty("name").GetString());

        var genres = await Get("getGenres");
        Assert.Equal(new[] { "Jazz", "Rock" }, genres.GetProperty("genres").GetProperty("genre").EnumerateArray().Select(g => g.GetProperty("value").GetString()).ToArray());

        var starred = await Get("getStarred2");
        Assert.Equal("Gamma", starred.GetProperty("starred2").GetProperty("song")[0].GetProperty("title").GetString());
        Assert.Equal("Second", starred.GetProperty("starred2").GetProperty("album")[0].GetProperty("name").GetString()); // all its tracks are starred
    }

    [Fact]
    public async Task Star_And_Scrobble_ReachTheLibrary()
    {
        var alpha = _lib.Tracks[0];
        var r = await Get("star", $"id=tr-{alpha.Id:N}");
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(new[] { alpha.Id }, _lib.LastStar!.Value.Tracks);
        Assert.True(_lib.LastStar.Value.Starred);

        await Get("unstar", $"albumId=al-{AlbumA:N}");
        Assert.Equal(AlbumA, _lib.LastStar!.Value.Albums.Single());
        Assert.False(_lib.LastStar.Value.Starred);

        await Get("scrobble", $"id=tr-{alpha.Id:N}&submission=true");
        Assert.Equal(new[] { alpha.Id }, _lib.Scrobbled);
        await Get("scrobble", $"id=tr-{alpha.Id:N}&submission=false"); // "now playing" only: not counted
        Assert.Single(_lib.Scrobbled);

        var bad = await Get("star");
        Assert.Equal(10, bad.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Playlists_Read_Create_Update_Delete()
    {
        var lists = await Get("getPlaylists");
        var mix = lists.GetProperty("playlists").GetProperty("playlist")[0];
        Assert.Equal("Mix", mix.GetProperty("name").GetString());
        Assert.Equal(2, mix.GetProperty("songCount").GetInt32());
        Assert.Equal("alice", mix.GetProperty("owner").GetString());

        var detail = await Get("getPlaylist", $"id={mix.GetProperty("id").GetString()}");
        Assert.Equal(2, detail.GetProperty("playlist").GetProperty("entry").GetArrayLength());

        var beta = _lib.Tracks[1];
        var created = await Get("createPlaylist", $"name=Fresh&songId=tr-{beta.Id:N}");
        Assert.Equal("Fresh", created.GetProperty("playlist").GetProperty("name").GetString());
        Assert.Equal("Beta", created.GetProperty("playlist").GetProperty("entry")[0].GetProperty("title").GetString());
        var freshId = created.GetProperty("playlist").GetProperty("id").GetString()!;

        await Get("updatePlaylist", $"playlistId={freshId}&name=Fresher&songIdToAdd=tr-{_lib.Tracks[2].Id:N}&songIndexToRemove=0");
        var fresher = _lib.Playlists.Single(p => p.Name == "Fresher");
        Assert.Equal(new[] { _lib.Tracks[2].Id }, fresher.TrackIds);

        await Get("deletePlaylist", $"id={freshId}");
        Assert.DoesNotContain(_lib.Playlists, p => p.Name == "Fresher");
    }

    [Fact]
    public async Task Stream_ServesTheFile_WithRangeSupport_AndCoverArt()
    {
        var alpha = _lib.Tracks[0];
        var full = await _http.GetAsync($"rest/stream.view?apiKey={_apiKey}&id=tr-{alpha.Id:N}");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("audio/mpeg", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal(5000, (await full.Content.ReadAsByteArrayAsync()).Length);

        var req = new HttpRequestMessage(HttpMethod.Get, $"rest/stream.view?apiKey={_apiKey}&id=tr-{alpha.Id:N}");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
        var partial = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        var bytes = await partial.Content.ReadAsByteArrayAsync();
        Assert.Equal(1000, bytes.Length);
        Assert.Equal((byte)(1000 % 251), bytes[0]);

        var art = await _http.GetAsync($"rest/getCoverArt.view?apiKey={_apiKey}&id=al-{AlbumA:N}");
        Assert.Equal(HttpStatusCode.OK, art.StatusCode);
        Assert.Equal("image/jpeg", art.Content.Headers.ContentType!.MediaType);

        // Never by path: an unknown id is 70, not a filesystem probe.
        var probe = await _http.GetStringAsync($"rest/stream.view?f=json&apiKey={_apiKey}&id=../../etc/passwd");
        Assert.Contains("\"code\":70", probe);

        // Unauthenticated streaming is refused.
        var anon = await _http.GetStringAsync($"rest/stream.view?f=json&id=tr-{alpha.Id:N}");
        Assert.Contains("\"code\":10", anon);
    }

    [Fact]
    public void UserStore_HashesPasswords_AndApiKeysAreOneWay()
    {
        var store = new ServerUserStore(Path.Combine(_dir, "users2.db"));
        store.Create("bob", "password123");
        Assert.NotNull(store.Verify("bob", "password123"));
        Assert.NotNull(store.Verify("BOB", "password123")); // names are case-insensitive
        Assert.Null(store.Verify("bob", "password124"));
        Assert.Throws<ArgumentException>(() => store.Create("carol", "short"));
        Assert.Throws<InvalidOperationException>(() => store.Create("Bob", "another one"));

        var key = store.RegenerateApiKey("bob");
        Assert.StartsWith("nk_", key);
        Assert.Equal("bob", store.ByApiKey(key)!.Name);
        var key2 = store.RegenerateApiKey("bob");
        Assert.Null(store.ByApiKey(key)); // old key dead
        Assert.NotNull(store.ByApiKey(key2));
        store.RevokeApiKey("bob");
        Assert.Null(store.ByApiKey(key2));

        store.ChangePassword("bob", "newpassword!");
        Assert.Null(store.Verify("bob", "password123"));
        Assert.NotNull(store.Verify("bob", "newpassword!"));
        Assert.True(store.Delete("bob"));
        Assert.False(store.Exists("bob"));

        // The database never holds the secret itself. (Pooled connections keep the file open.)
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var raw = File.ReadAllBytes(Path.Combine(_dir, "users2.db"));
        Assert.False(Contains(raw, System.Text.Encoding.UTF8.GetBytes("password123")));
    }

    [Fact]
    public void Certificate_IsCreatedOnce_AndReloaded()
    {
        var dir = Path.Combine(_dir, "cert");
        using var first = ServerCertificate.LoadOrCreate(dir);
        Assert.True(first.HasPrivateKey);
        Assert.Equal("CN=Noctis", first.Subject);
        var fp = ServerCertificate.Fingerprint(first);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fp);
        using var second = ServerCertificate.LoadOrCreate(dir);
        Assert.Equal(fp, ServerCertificate.Fingerprint(second));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
        => Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(i => haystack.Skip(i).Take(needle.Length).SequenceEqual(needle));

    /// <summary>In-memory library with recorded side effects.</summary>
    private sealed class FakeServerLibrary : IServerLibrary
    {
        public List<Track> Tracks { get; }
        public List<Album> Albums { get; }
        public List<Artist> Artists { get; }
        public List<Playlist> Playlists { get; }
        private readonly string _artworkPath;
        public (IReadOnlyList<Guid> Tracks, IReadOnlyList<Guid> Albums, IReadOnlyList<Guid> Artists, bool Starred)? LastStar;
        public List<Guid> Scrobbled { get; } = new();

        public FakeServerLibrary(IEnumerable<Track> tracks, IEnumerable<Album> albums, IEnumerable<Artist> artists, List<Playlist> playlists, string artworkPath)
        {
            Tracks = tracks.ToList(); Albums = albums.ToList(); Artists = artists.ToList(); Playlists = playlists; _artworkPath = artworkPath;
            foreach (var a in Albums) a.Tracks = Tracks.Where(t => t.AlbumId == a.Id).ToList();
        }

        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), Albums.ToList(), Artists.ToList(), Playlists.ToList()));
        public string? ArtworkPath(Guid albumId) => albumId == AlbumA ? _artworkPath : null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred)
        { LastStar = (trackIds, albumIds, artistIds, starred); return Task.CompletedTask; }
        public Task ScrobbleAsync(Guid trackId) { Scrobbled.Add(trackId); return Task.CompletedTask; }
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds)
        { var p = new Playlist { Name = name, TrackIds = trackIds.ToList() }; Playlists.Add(p); return Task.FromResult(p); }
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes)
        {
            var p = Playlists.FirstOrDefault(x => x.Id == id); if (p is null) return Task.FromResult(false);
            if (name is not null) p.Name = name;
            foreach (var i in removeIndexes.OrderByDescending(i => i)) if (i < p.TrackIds.Count) p.TrackIds.RemoveAt(i);
            p.TrackIds.AddRange(add); return Task.FromResult(true);
        }
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(Playlists.RemoveAll(x => x.Id == id) > 0);
    }
}
