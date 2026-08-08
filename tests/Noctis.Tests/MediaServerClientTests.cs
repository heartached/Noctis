using System.Net;
using System.Net.Http;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MediaServer;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Contract tests for the media-server clients: URL/token construction, auth header
/// shape, defensive JSON parsing and error classification — all against canned
/// responses through a stubbed HttpMessageHandler, no live servers.
/// </summary>
public class MediaServerClientTests
{
    // ── Subsonic: URL + token auth ──

    [Fact]
    public void Subsonic_RequestUrl_UsesSaltedTokenAuth_NeverThePassword()
    {
        var url = SubsonicClient.BuildRequestUrl("https://music.example.com", "demo", "sesame", "ping", ("x", "1"));

        Assert.StartsWith("https://music.example.com/rest/ping.view?", url);
        Assert.Contains("u=demo", url);
        Assert.Contains("v=1.16.1", url);
        Assert.Contains("c=Noctis", url);
        Assert.Contains("f=json", url);
        Assert.Contains("x=1", url);
        Assert.DoesNotContain("sesame", url);

        // t must be md5(password + salt), lowercase hex, per the Subsonic API spec.
        var query = url.Split('?')[1].Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);
        var expected = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("sesame" + query["s"])))
            .ToLowerInvariant();
        Assert.Equal(expected, query["t"]);
    }

    [Fact]
    public async Task Subsonic_Connect_Success_KeepsPasswordForTokenDerivation()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/rest/ping.view"))
                return Json("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (path.EndsWith("/rest/getAlbumList2.view"))
                return Json("""{"subsonic-response":{"status":"ok","albumList2":{"album":[{"id":"al-1","name":"Blue"}]}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com/", Username = "demo", Type = SourceType.Navidrome };

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.True(result.Success);
        Assert.Equal(MediaServerError.None, result.Error);
        Assert.Equal("sesame", connection.TokenOrPassword);
        Assert.Equal("https://music.example.com", connection.BaseUriOrPath); // normalized, no trailing slash
    }

    [Fact]
    public void Subsonic_RequestUrl_SaltIsFreshLowercaseHex_PerRequest()
    {
        static string SaltOf(string url) => url.Split('?')[1].Split('&')
            .Select(p => p.Split('=', 2))
            .First(p => p[0] == "s")[1];

        var first = SaltOf(SubsonicClient.BuildRequestUrl("https://music.example.com", "demo", "sesame", "ping"));
        var second = SaltOf(SubsonicClient.BuildRequestUrl("https://music.example.com", "demo", "sesame", "ping"));

        // Crypto-RNG salt: 12 lowercase hex chars, and never reused between requests.
        Assert.Matches("^[0-9a-f]{12}$", first);
        Assert.Matches("^[0-9a-f]{12}$", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Subsonic_Connect_HostileServerErrorMessage_IsCappedAndSingleLine()
    {
        // Unknown error code → the server's own message reaches the status line;
        // a hostile server must not be able to flood it or inject line breaks.
        var flood = new string('x', 5000);
        var payload = """{"subsonic-response":{"status":"failed","error":{"code":0,"message":"FLOOD\r\nsecond line"}}}"""
            .Replace("FLOOD", flood);
        var handler = new StubHttpMessageHandler(_ => Json(payload));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo" };

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.ServerError, result.Error);
        Assert.True(result.Message.Length <= 201, $"status text not capped: {result.Message.Length} chars");
        Assert.DoesNotContain("\n", result.Message);
        Assert.StartsWith("xxx", result.Message);
    }

    [Fact]
    public async Task Subsonic_Connect_ErrorCode40_MapsToAuthFailed()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json("""{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password."}}}"""));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo" };

        var result = await client.ConnectAsync(connection, "wrong");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.AuthFailed, result.Error);
        Assert.NotEqual("wrong", connection.TokenOrPassword);
    }

    [Fact]
    public async Task Subsonic_Connect_NetworkFailure_MapsToUnreachable()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("refused"));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo" };

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.Unreachable, result.Error);
    }

    [Fact]
    public async Task Subsonic_Connect_NonSubsonicResponse_MapsToServerError()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""{"unexpected":"payload"}"""));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo" };

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.ServerError, result.Error);
    }

    [Fact]
    public async Task Subsonic_GetAlbums_ParsesAlbumList2()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"subsonic-response":{"status":"ok","albumList2":{"album":[
                {"id":"al-1","name":"Abbey Road","artist":"The Beatles","year":1969,"songCount":17,"duration":2832,"coverArt":"al-1"},
                {"id":"al-2","name":"Blue"},
                {"name":"missing id — must be skipped"}
            ]}}}
            """));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = ConnectedSubsonic();

        var albums = await client.GetAlbumsAsync(connection, 0, 50);

        Assert.Equal(2, albums.Count);
        Assert.Equal("Abbey Road", albums[0].Name);
        Assert.Equal("The Beatles", albums[0].Artist);
        Assert.Equal(1969, albums[0].Year);
        Assert.Equal(17, albums[0].SongCount);
        Assert.Equal("al-1", albums[0].CoverArtId);
        Assert.Equal("Blue", albums[1].Name);
        Assert.Null(albums[1].CoverArtId);
    }

    [Fact]
    public async Task Subsonic_GetAlbumTracks_MapsSongs_WithTokenizedStreamUrl()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"subsonic-response":{"status":"ok","album":{"id":"al-1","name":"Abbey Road","artist":"The Beatles","song":[
                {"id":"s-1","title":"Come Together","artist":"The Beatles","track":1,"discNumber":1,"year":1969,"duration":259,"size":31234567,"bitRate":1024,"suffix":"flac"},
                {"id":"s-2","title":"Something","track":2,"duration":182}
            ]}}}
            """));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = ConnectedSubsonic();
        var album = new ServerAlbum { Id = "al-1", Name = "Abbey Road", Artist = "The Beatles" };

        var tracks = await client.GetAlbumTracksAsync(connection, album);

        Assert.Equal(2, tracks.Count);
        var first = tracks[0];
        Assert.Equal("Come Together", first.Title);
        Assert.Equal("The Beatles", first.Artist);
        Assert.Equal("Abbey Road", first.Album);
        Assert.Equal(1, first.TrackNumber);
        Assert.Equal(TimeSpan.FromSeconds(259), first.Duration);
        Assert.Equal("flac", first.Codec);
        Assert.Equal(SourceType.Navidrome, first.SourceType);
        Assert.Equal("s-1", first.SourceTrackId);
        Assert.True(first.IsRemoteStream);

        // The playable URL: salted-token stream endpoint, never the raw password.
        Assert.StartsWith("https://music.example.com/rest/stream.view?", first.FilePath);
        Assert.Contains("id=s-1", first.FilePath);
        Assert.DoesNotContain("sesame", first.FilePath);

        // Missing artist falls back to the album artist.
        Assert.Equal("The Beatles", tracks[1].Artist);

        // Deterministic ids: re-mapping the same server song yields the same Track.Id.
        var again = await client.GetAlbumTracksAsync(connection, album);
        Assert.Equal(first.Id, again[0].Id);
        Assert.NotEqual(tracks[0].Id, tracks[1].Id);
    }

    // ── Jellyfin: auth header + auth flow ──

    [Fact]
    public void Jellyfin_AuthHeader_HasMediaBrowserShape()
    {
        var connection = new SourceConnection { Type = SourceType.Jellyfin };

        var anonymous = JellyfinClient.BuildAuthHeader(connection, token: null);
        Assert.StartsWith("MediaBrowser ", anonymous);
        Assert.Contains("Client=\"Noctis\"", anonymous);
        Assert.Contains($"DeviceId=\"{connection.Id:N}\"", anonymous);
        Assert.Contains("Version=\"", anonymous);
        Assert.Contains("Device=\"", anonymous);
        Assert.DoesNotContain("Token=", anonymous);

        var authed = JellyfinClient.BuildAuthHeader(connection, "tok-123");
        Assert.Contains("Token=\"tok-123\"", authed);
    }

    [Fact]
    public async Task Jellyfin_Connect_PostsAuthenticateByName_AndKeepsTokenNotPassword()
    {
        var requests = new List<(HttpRequestMessage Request, string? Body)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request, body));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Users/AuthenticateByName"))
                return Json("""{"AccessToken":"tok-123","User":{"Id":"user-1","Name":"demo"}}""");
            if (path.Contains("/Users/user-1/Items"))
                return Json("""{"Items":[{"Id":"a1"}],"TotalRecordCount":42}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://jf.example.com", Username = "demo", Type = SourceType.Jellyfin };

        var result = await client.ConnectAsync(connection, "hunter2");

        Assert.True(result.Success);
        Assert.Equal("tok-123", connection.TokenOrPassword); // token stored…
        Assert.Equal("user-1", connection.UserId);

        var auth = requests[0];
        Assert.Equal(HttpMethod.Post, auth.Request.Method);
        Assert.EndsWith("/Users/AuthenticateByName", auth.Request.RequestUri!.AbsolutePath);
        var header = string.Join(", ", auth.Request.Headers.GetValues("Authorization"));
        Assert.StartsWith("MediaBrowser ", header);
        Assert.DoesNotContain("Token=", header); // no token yet at login
        Assert.Contains("\"Username\":\"demo\"", auth.Body);
        Assert.Contains("\"Pw\":\"hunter2\"", auth.Body);

        // The follow-up music probe must carry the fresh token.
        var probeHeader = string.Join(", ", requests[1].Request.Headers.GetValues("Authorization"));
        Assert.Contains("Token=\"tok-123\"", probeHeader);
    }

    [Fact]
    public async Task Jellyfin_Connect_401_MapsToAuthFailed()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://jf.example.com", Username = "demo", Type = SourceType.Jellyfin };

        var result = await client.ConnectAsync(connection, "wrong");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.AuthFailed, result.Error);
        Assert.NotEqual("wrong", connection.TokenOrPassword);
    }

    [Fact]
    public async Task Jellyfin_Connect_NonJellyfinResponse_MapsToServerError()
    {
        var handler = new StubHttpMessageHandler(_ => Json("<html>totally not jellyfin</html>"));
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://jf.example.com", Username = "demo", Type = SourceType.Jellyfin };

        var result = await client.ConnectAsync(connection, "pw");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.ServerError, result.Error);
    }

    [Fact]
    public async Task Jellyfin_GetAlbums_ParsesItems()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"Items":[
                {"Id":"alb-1","Name":"Abbey Road","AlbumArtist":"The Beatles","ProductionYear":1969,"ChildCount":17,"RunTimeTicks":28320000000,"ImageTags":{"Primary":"tag1"}},
                {"Id":"alb-2","Name":"No Art Yet"}
            ],"TotalRecordCount":2}
            """));
        var client = new JellyfinClient(new HttpClient(handler));

        var albums = await client.GetAlbumsAsync(ConnectedJellyfin(), 0, 50);

        Assert.Equal(2, albums.Count);
        Assert.Equal("Abbey Road", albums[0].Name);
        Assert.Equal("The Beatles", albums[0].Artist);
        Assert.Equal(1969, albums[0].Year);
        Assert.Equal(17, albums[0].SongCount);
        Assert.Equal("alb-1", albums[0].CoverArtId);        // has a Primary image
        Assert.Null(albums[1].CoverArtId);                  // no ImageTags → no art id
        Assert.Equal(TimeSpan.FromTicks(28320000000), albums[0].Duration);
    }

    [Fact]
    public async Task Jellyfin_GetAlbumTracks_BuildsStaticStreamUrl()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"Items":[
                {"Id":"t1","Name":"Come Together","Artists":["The Beatles"],"AlbumArtist":"The Beatles","Album":"Abbey Road","IndexNumber":1,"ParentIndexNumber":1,"ProductionYear":1969,"RunTimeTicks":2590000000,"Container":"flac","AlbumId":"alb-1"}
            ],"TotalRecordCount":1}
            """));
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = ConnectedJellyfin();
        var album = new ServerAlbum { Id = "alb-1", Name = "Abbey Road", Artist = "The Beatles" };

        var tracks = await client.GetAlbumTracksAsync(connection, album);

        var track = Assert.Single(tracks);
        Assert.Equal("Come Together", track.Title);
        Assert.Equal("The Beatles", track.Artist);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(TimeSpan.FromTicks(2590000000), track.Duration);
        Assert.Equal(SourceType.Jellyfin, track.SourceType);
        Assert.True(track.IsRemoteStream);
        // static=true streams the original file; api_key authenticates because the
        // media player can't send the Authorization header.
        Assert.Equal("https://jf.example.com/Audio/t1/stream?static=true&api_key=tok-123", track.FilePath);
    }

    // ── Base-URL transport policy ──

    [Theory]
    [InlineData("https://music.example.com", true)]
    [InlineData("music.example.com", true)]              // scheme-less input assumes https
    [InlineData("http://music.example.com", false)]      // public plain http refused
    [InlineData("http://203.0.113.7", false)]            // public IP plain http refused
    [InlineData("http://192.168.1.10:4533", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("http://172.20.1.2:8096", true)]
    [InlineData("http://127.0.0.1:4533", true)]
    [InlineData("http://localhost:8096", true)]
    [InlineData("http://mynas", true)]                   // single-label LAN name
    [InlineData("http://nas.local", true)]               // mDNS
    [InlineData("", false)]
    [InlineData("ftp://music.example.com", false)]
    public void UrlPolicy_AllowsHttpsAnywhere_AndPlainHttpOnlyOnPrivateHosts(string input, bool allowed)
    {
        var normalized = MediaServerUrl.TryNormalizeBase(input, out var error, out _);

        Assert.Equal(allowed, normalized != null);
        if (!allowed) Assert.NotEqual(MediaServerError.None, error);
    }

    [Theory]
    [InlineData("https://music.example.com/", "https://music.example.com")]
    [InlineData("https://music.example.com/subsonic/", "https://music.example.com/subsonic")]
    public void UrlPolicy_NormalizesTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, MediaServerUrl.TryNormalizeBase(input, out _, out _));
    }

    [Theory]
    [InlineData("10.0.0.157:8096", "http://10.0.0.157:8096")]     // Jellyfin's default port is plain http
    [InlineData("192.168.1.10:4533", "http://192.168.1.10:4533")]
    [InlineData("mynas:8096", "http://mynas:8096")]
    [InlineData("nas.local:8096", "http://nas.local:8096")]
    [InlineData("music.example.com", "https://music.example.com")] // public host still assumes https
    public void UrlPolicy_SchemelessInput_AssumesHttpOnPrivateHosts(string input, string expected)
    {
        Assert.Equal(expected, MediaServerUrl.TryNormalizeBase(input, out _, out _));
    }

    // ── Transport-failure classification (DNS vs refused vs TLS vs timeout) ──

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "address not found")]
    [InlineData(HttpRequestError.ConnectionError, "refused the connection")]
    [InlineData(HttpRequestError.SecureConnectionError, "Secure connection failed")]
    public async Task Jellyfin_Connect_TransportFailure_NamesTheCause(HttpRequestError kind, string expectedFragment)
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException(kind));
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://jf.example.com", Username = "demo", Type = SourceType.Jellyfin };

        var result = await client.ConnectAsync(connection, "pw");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.Unreachable, result.Error);
        Assert.Contains(expectedFragment, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Jellyfin_Connect_ClientTimeout_SaysServerDidNotRespond()
    {
        // HttpClient's own timeout surfaces as TaskCanceledException with the
        // caller's token NOT cancelled — must not be swallowed as the generic
        // "couldn't reach" nor rethrown as a user cancel.
        var handler = new StubHttpMessageHandler(_ =>
            throw new TaskCanceledException("timed out", new TimeoutException()));
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://jf.example.com", Username = "demo", Type = SourceType.Jellyfin };

        var result = await client.ConnectAsync(connection, "pw");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.Unreachable, result.Error);
        Assert.Contains("didn't respond", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Subsonic_Connect_DnsFailure_NamesTheCause()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError));
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo" };

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.Unreachable, result.Error);
        Assert.Contains("address not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Artwork download bounds ──

    [Fact]
    public async Task Artwork_StalledServer_GivesUpAfterTimeout_InsteadOfHangingForever()
    {
        // HttpClient.Timeout does not bound body reads after ResponseHeadersRead,
        // so the service applies its own per-download ceiling; without it a stalled
        // server pinned an artwork-gate slot (and the album's in-flight entry) forever.
        var previous = MediaServerService.ArtworkDownloadTimeout;
        MediaServerService.ArtworkDownloadTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            using var persistence = new TestPersistenceService();
            var service = new MediaServerService(new HttpClient(new StallingHttpMessageHandler()), persistence);
            service.SetActiveConnection(ConnectedSubsonic());
            var album = new ServerAlbum { Id = "al-1", Name = "Abbey Road", Artist = "The Beatles", CoverArtId = "al-1" };

            var task = service.EnsureAlbumArtworkAsync(album);
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(task, done); // gave up instead of hanging
            Assert.Null(await task); // stalled download surfaces as "no art"
        }
        finally
        {
            MediaServerService.ArtworkDownloadTimeout = previous;
        }
    }

    // ── Scheme → FromType decision in the player ──

    [Theory]
    [InlineData("http://192.168.1.10/rest/stream.view?id=1", true)]
    [InlineData("https://jf.example.com/Audio/t1/stream?static=true", true)]
    [InlineData("HTTPS://JF.EXAMPLE.COM/Audio/t1/stream", true)]
    [InlineData(@"C:\Music\track.flac", false)]
    [InlineData("/home/user/music/track.flac", false)]
    [InlineData("httpsomething/otherwise.mp3", false)]
    public void Player_IsRemoteStreamPath_DetectsHttpSchemesOnly(string path, bool remote)
    {
        Assert.Equal(remote, VlcAudioPlayer.IsRemoteStreamPath(path));
    }

    // ── Helpers ──

    private static SourceConnection ConnectedSubsonic() => new()
    {
        BaseUriOrPath = "https://music.example.com",
        Username = "demo",
        TokenOrPassword = "sesame",
        Type = SourceType.Navidrome
    };

    private static SourceConnection ConnectedJellyfin() => new()
    {
        BaseUriOrPath = "https://jf.example.com",
        Username = "demo",
        TokenOrPassword = "tok-123",
        UserId = "user-1",
        Type = SourceType.Jellyfin
    };

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    /// <summary>Never answers; completes only when the request's token is cancelled.</summary>
    private sealed class StallingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
