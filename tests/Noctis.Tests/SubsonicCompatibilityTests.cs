using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Models;
using Noctis.Services.MediaServer;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Subsonic client against servers that are NOT Navidrome: older API versions
/// (error 30), no token auth (error 41 / pre-1.13), numeric ids, and the full error
/// code table. Canned responses through a stub handler; no live servers.
/// </summary>
public class SubsonicCompatibilityTests
{
    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private static string Ok(string version, string body = "") =>
        "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"" + version + "\"" + (body.Length > 0 ? "," + body : "") + "}}";

    private static string Failed(int code, string version, string message = "") =>
        "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"" + version + "\",\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\"}}}";

    private const string OneAlbum = """
        "albumList2":{"album":[{"id":"al-1","name":"Blue"}]}
        """;

    private static Dictionary<string, string> Query(string url) =>
        url.Split('?')[1].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<string> Urls { get; } = new();
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responder(request));
        }
    }

    private static (SubsonicClient Client, RecordingHandler Handler, SourceConnection Connection) Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var client = new SubsonicClient(new HttpClient(handler));
        var connection = new SourceConnection { BaseUriOrPath = "https://music.example.com", Username = "demo", Type = SourceType.Navidrome };
        return (client, handler, connection);
    }

    // ── Version negotiation (error 30) ──

    [Fact]
    public async Task OlderServer_Error30_RetriesWithTheServersVersion_AndPersistsIt()
    {
        // Airsonic classic: API 1.15.0, rejects a 1.16.1 client with error 30.
        var (client, handler, connection) = Build(req =>
        {
            var q = Query(req.RequestUri!.ToString());
            if (q["v"] != "1.15.0") return Json(Failed(30, "1.15.0", "Incompatible Subsonic REST protocol version. Server must upgrade."));
            return req.RequestUri!.AbsolutePath.EndsWith("/rest/ping.view") ? Json(Ok("1.15.0")) : Json(Ok("1.15.0", OneAlbum));
        });

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.True(result.Success, result.Message);
        Assert.Equal("1.15.0", connection.ApiVersion);
        Assert.Equal(SubsonicAuthMode.Token, connection.AuthMode);
        Assert.Equal("1.16.1", Query(handler.Urls[0])["v"]);
        Assert.Equal("1.15.0", Query(handler.Urls[1])["v"]);
        Assert.All(handler.Urls.Skip(1), u => Assert.Equal("1.15.0", Query(u)["v"]));

        // Everything after connect, including playback URLs, speaks the negotiated version.
        var stream = SubsonicClient.BuildStreamUrl(connection, "s-1")!;
        Assert.Equal("1.15.0", Query(stream)["v"]);
        Assert.Contains("t=", stream);
    }

    [Fact]
    public async Task ModernServer_ReportingLowerVersion_IsSpokenToAtThatVersion_WithOneRoundTrip()
    {
        // Gonic answers ok to anything but reports 1.15.0; we adopt it without a retry.
        var (client, handler, connection) = Build(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/rest/ping.view") ? Json(Ok("1.15.0")) : Json(Ok("1.15.0", OneAlbum)));

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.True(result.Success);
        Assert.Equal("1.15.0", connection.ApiVersion);
        Assert.Single(handler.Urls, u => u.Contains("/rest/ping.view"));
    }

    [Fact]
    public async Task Pre113Server_Error30_FallsBackToPasswordAuth()
    {
        var (client, handler, connection) = Build(req =>
        {
            var q = Query(req.RequestUri!.ToString());
            if (q["v"] != "1.12.0") return Json(Failed(30, "1.12.0"));
            if (!q.ContainsKey("p")) return Json(Failed(40, "1.12.0"));
            return req.RequestUri!.AbsolutePath.EndsWith("/rest/ping.view") ? Json(Ok("1.12.0")) : Json(Ok("1.12.0", OneAlbum));
        });

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.True(result.Success, result.Message);
        Assert.Equal("1.12.0", connection.ApiVersion);
        Assert.Equal(SubsonicAuthMode.Password, connection.AuthMode);
        var stream = SubsonicClient.BuildStreamUrl(connection, "s-1")!;
        var q = Query(stream);
        Assert.Equal("enc:" + Convert.ToHexString(Encoding.UTF8.GetBytes("sesame")).ToLowerInvariant(), q["p"]);
        Assert.False(q.ContainsKey("t"));
        Assert.False(q.ContainsKey("s"));
        Assert.DoesNotContain("sesame", stream);   // hex-obfuscated, never the raw password
    }

    [Fact]
    public async Task ServerThatIsNewerThanUs_Error30IsReportedNotLooped()
    {
        // A server that claims 2.0.0 but still throws 30 is broken; do not spin.
        var (client, handler, connection) = Build(_ => Json(Failed(30, "2.0.0", "nope")));

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.ServerError, result.Error);
        Assert.Contains("too old", result.Message);
        Assert.Single(handler.Urls);
        Assert.Equal("1.16.1", connection.ApiVersion);   // untouched on failure
    }

    // ── Token refused (error 41) ──

    [Fact]
    public async Task LdapUser_Error41_RetriesWithPasswordAuth_SameVersion()
    {
        var (client, handler, connection) = Build(req =>
        {
            var q = Query(req.RequestUri!.ToString());
            if (q.ContainsKey("t")) return Json(Failed(41, "1.16.1", "Token authentication not supported for LDAP users."));
            return req.RequestUri!.AbsolutePath.EndsWith("/rest/ping.view") ? Json(Ok("1.16.1")) : Json(Ok("1.16.1", OneAlbum));
        });

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.True(result.Success, result.Message);
        Assert.Equal(SubsonicAuthMode.Password, connection.AuthMode);
        Assert.Equal("1.16.1", connection.ApiVersion);
        Assert.True(Query(handler.Urls[0]).ContainsKey("t"));
        Assert.True(Query(handler.Urls[1]).ContainsKey("p"));
    }

    [Fact]
    public async Task PasswordAuthAlsoRejected_ReportsAuthFailure_NoInfiniteRetry()
    {
        var (client, handler, connection) = Build(req =>
            Query(req.RequestUri!.ToString()).ContainsKey("t") ? Json(Failed(41, "1.16.1")) : Json(Failed(40, "1.16.1")));

        var result = await client.ConnectAsync(connection, "sesame");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.AuthFailed, result.Error);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task WrongPassword_OnModernServer_DoesNotFallBackToPasswordAuth()
    {
        // Error 40 with a token-capable server means the password is wrong; retrying
        // with p=enc: would only send it in a weaker form for nothing.
        var (client, handler, connection) = Build(_ => Json(Failed(40, "1.16.1", "Wrong username or password.")));

        var result = await client.ConnectAsync(connection, "wrong");

        Assert.False(result.Success);
        Assert.Equal(MediaServerError.AuthFailed, result.Error);
        Assert.Single(handler.Urls);
    }

    // ── Ids ──

    [Fact]
    public async Task NumericIds_AreAcceptedForAlbums_Songs_AndCoverArt()
    {
        var (client, _, connection) = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/rest/getAlbumList2.view"))
                return Json("""{"subsonic-response":{"status":"ok","albumList2":{"album":[{"id":12,"name":"Blue","coverArt":34},{"id":"al-2","name":"Red"}]}}}""");
            if (path.EndsWith("/rest/getAlbum.view"))
                return Json("""{"subsonic-response":{"status":"ok","album":{"id":12,"name":"Blue","song":[{"id":991,"title":"One","duration":10}]}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var albums = await client.GetAlbumsAsync(connection, 0, 10);
        Assert.Equal(new[] { "12", "al-2" }, albums.Select(a => a.Id));
        Assert.Equal("34", albums[0].CoverArtId);

        var tracks = await client.GetAlbumTracksAsync(connection, albums[0]);
        var track = Assert.Single(tracks);
        Assert.Equal("991", track.SourceTrackId);
        Assert.Contains("id=991", track.FilePath);
    }

    // ── Error table ──

    [Theory]
    [InlineData(10, MediaServerError.ServerError, "incomplete")]
    [InlineData(20, MediaServerError.ServerError, "newer version of Noctis")]
    [InlineData(30, MediaServerError.ServerError, "too old")]
    [InlineData(40, MediaServerError.AuthFailed, "Wrong username or password")]
    [InlineData(41, MediaServerError.AuthFailed, "token login")]
    [InlineData(50, MediaServerError.AuthFailed, "not allowed")]
    [InlineData(60, MediaServerError.ServerError, "trial")]
    [InlineData(70, MediaServerError.ServerError, "Not found")]
    [InlineData(99, MediaServerError.ServerError, "code 99")]
    public void ErrorCodes_MapToActionableMessages(int code, MediaServerError expected, string fragment)
    {
        var (error, message) = SubsonicClient.ClassifyError(code, "");
        Assert.Equal(expected, error);
        Assert.Contains(fragment, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerDetail_IsAppendedSanitized()
    {
        var (_, message) = SubsonicClient.ClassifyError(70, "Album\nnot\tfound");
        Assert.Equal("Not found on the server: Album not found", message);
    }

    // ── Version parsing ──

    [Theory]
    [InlineData("1.16.1", "1.16.1")]
    [InlineData("1.15", "1.15.0")]
    [InlineData("1.16.1-SNAPSHOT", "1.16.1")]
    [InlineData(" 1.13.0 ", "1.13.0")]
    public void ServerVersionStrings_Parse(string text, string expected)
    {
        Assert.True(SubsonicClient.TryParseVersion(text, out var v));
        Assert.Equal(expected, v.ToString(3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("navidrome")]
    public void GarbageVersionStrings_DoNotParse(string? text)
        => Assert.False(SubsonicClient.TryParseVersion(text, out _));

    // ── Persistence shape ──

    [Fact]
    public void NegotiatedFields_RoundTripThroughJson_AndDefaultWhenAbsent()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());

        var c = new SourceConnection { ApiVersion = "1.15.0", AuthMode = SubsonicAuthMode.Password };
        var back = JsonSerializer.Deserialize<SourceConnection>(JsonSerializer.Serialize(c, options), options)!;
        Assert.Equal("1.15.0", back.ApiVersion);
        Assert.Equal(SubsonicAuthMode.Password, back.AuthMode);

        // A settings.json written before these fields existed keeps working as before.
        var legacy = JsonSerializer.Deserialize<SourceConnection>("""{"id":"3f2c1b6e-9a0d-4c8f-b1e2-d3c4a5b6c7d8","name":"Navidrome","username":"demo"}""", options)!;
        Assert.Equal(SubsonicClient.DefaultApiVersion, legacy.ApiVersion);
        Assert.Equal(SubsonicAuthMode.Token, legacy.AuthMode);
    }
}
