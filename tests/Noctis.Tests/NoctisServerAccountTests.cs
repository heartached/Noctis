using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone account protocol over real Kestrel (plain HTTP on loopback): noctisSignIn trades
/// the password for a per-device key, the key (header or apiKey=) works everywhere, sign-out
/// and account changes revoke it, scrobble honours time=, getCoverArt honours size=, and the
/// login brake holds against parallel guesses.
/// </summary>
public class NoctisServerAccountTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-acc-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private FakeLibrary _lib = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private static readonly Guid AlbumA = Guid.NewGuid();
    private Track _track = null!;
    private Func<Guid, string, int, System.Threading.CancellationToken, Task<string?>>? _resizer;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);

        var audio = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(audio, Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray());
        var cover = Path.Combine(_dir, "cover.png");
        using (var bmp = new SKBitmap(900, 600))
        {
            bmp.Erase(new SKColor(200, 40, 40));
            using var img = SKImage.FromBitmap(bmp);
            using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(cover, png.ToArray());
        }
        _track = new Track { Id = Guid.NewGuid(), Title = "Alpha", Artist = "X", AlbumArtist = "X", Album = "First", AlbumId = AlbumA, FilePath = audio, Duration = TimeSpan.FromSeconds(10), FileSize = 3000 };
        _lib = new FakeLibrary(_track, AlbumA, cover);
        _resizer = new ServerCoverResizer(Path.Combine(_dir, "covers")).ResizeAsync;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        _server = new NoctisServer(_lib, _users, "test", sync: null, coverResizer: (a, p, s, ct) => _resizer!(a, p, s, ct)) { PrivateClientsOnly = true };
        await _server.StartAsync(0, certificate: null);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static JsonElement Envelope(string json) => JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");

    private async Task<(JsonElement Body, HttpResponseMessage Response)> SignIn(string password = "correct horse", string user = "alice",
        string deviceId = "phone-0001", string deviceName = "Galaxy S23")
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = user, ["p"] = password, ["deviceId"] = deviceId, ["deviceName"] = deviceName,
            ["f"] = "json", ["c"] = "NoctisAndroid", ["v"] = "1.16.1",
        });
        var res = await _http.PostAsync("rest/noctisSignIn.view", form, TestContext.Current.CancellationToken);
        return (Envelope(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)), res);
    }

    private async Task<string> SignInKey(string deviceId = "phone-0001")
    {
        var (r, _) = await SignIn(deviceId: deviceId);
        Assert.Equal("ok", r.GetProperty("status").GetString());
        return r.GetProperty("noctisSignIn").GetProperty("apiKey").GetString()!;
    }

    private async Task<JsonElement> GetWithHeader(string method, string key, string query = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"rest/{method}.view?f=json{(query.Length > 0 ? "&" + query : "")}");
        req.Headers.Add(NoctisServer.KeyHeader, key);
        var res = await _http.SendAsync(req, TestContext.Current.CancellationToken);
        return Envelope(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static int ErrorCode(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task SignIn_IssuesADeviceKey_ThatWorksForGetUserAndStream()
    {
        var (r, res) = await SignIn();
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.True(res.Headers.CacheControl?.NoStore);
        var s = r.GetProperty("noctisSignIn");
        var key = s.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("nk_", key);
        Assert.Equal("alice", s.GetProperty("user").GetString());
        Assert.False(string.IsNullOrEmpty(s.GetProperty("server").GetString()));
        Assert.False(s.GetProperty("syncEnabled").GetBoolean()); // this server has no sync service

        // Header (the phone's way: no key in any URL) and apiKey= both work.
        Assert.Equal("alice", (await GetWithHeader("getUser", key)).GetProperty("user").GetProperty("username").GetString());
        var viaParam = Envelope(await _http.GetStringAsync($"rest/getUser.view?f=json&apiKey={Uri.EscapeDataString(key)}", TestContext.Current.CancellationToken));
        Assert.Equal("ok", viaParam.GetProperty("status").GetString());

        using var stream = new HttpRequestMessage(HttpMethod.Get, $"rest/stream.view?id=tr-{_track.Id:N}");
        stream.Headers.Add(NoctisServer.KeyHeader, key);
        var audio = await _http.SendAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal(3000, (await audio.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);

        // Only the hash is stored; the device is listed with its name.
        var device = Assert.Single(_users.Devices());
        Assert.Equal(("alice", "phone-0001", "Galaxy S23"), (device.User, device.DeviceId, device.DeviceName));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_dir, "users.db"), System.Text.Encoding.Latin1));
    }

    [Fact]
    public async Task SignIn_AcceptsGetAndEncPassword()
    {
        var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("correct horse"));
        var r = Envelope(await _http.GetStringAsync($"rest/noctisSignIn.view?f=json&u=alice&p=enc:{hex}&deviceId=phone-0002&deviceName=Tab", TestContext.Current.CancellationToken));
        Assert.Equal("ok", r.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SignIn_WrongPassword_AndUnknownUser_AnswerTheSame()
    {
        var (wrong, _) = await SignIn(password: "nope nope");
        var (unknown, _) = await SignIn(user: "mallory");
        Assert.Equal(40, ErrorCode(wrong));
        Assert.Equal(ErrorCode(wrong), ErrorCode(unknown));
        Assert.Equal(wrong.GetProperty("error").GetProperty("message").GetString(), unknown.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(_users.Devices());
    }

    [Theory]
    [InlineData("short", "Phone")]          // deviceId under 8
    [InlineData("has space 123", "Phone")]  // outside [A-Za-z0-9_-]
    [InlineData("phone-0001\n", "Phone")]   // trailing newline
    [InlineData("phone-0001", "")]           // empty name
    [InlineData("phone-0001", "\u0007\u0008")] // only control characters
    public async Task SignIn_RejectsBadDeviceFields(string deviceId, string deviceName)
    {
        var (r, _) = await SignIn(deviceId: deviceId, deviceName: deviceName);
        Assert.Equal(10, ErrorCode(r));
        Assert.Empty(_users.Devices());
    }

    [Fact]
    public async Task SignIn_StripsControlCharacters_AndRefusesAKey()
    {
        await SignIn(deviceName: "Pix\u0007el 8\n");
        Assert.Equal("Pixel 8", Assert.Single(_users.Devices()).DeviceName);
        Assert.Equal(new string('x', 64), NoctisServer.CleanDeviceName(new string('x', 64)));
        Assert.Null(NoctisServer.CleanDeviceName(new string('x', 65)));
        // Format characters too: a right-to-left override, zero-width space/joiner, BOM.
        Assert.Equal("Pixel 8", NoctisServer.CleanDeviceName("‮Pix​el‍ 8﻿ "));
        Assert.Null(NoctisServer.CleanDeviceName("‮​⁦ "));

        var key = _users.RegenerateApiKey("alice");
        var withKey = Envelope(await _http.GetStringAsync($"rest/noctisSignIn.view?f=json&apiKey={key}&deviceId=phone-0003&deviceName=X", TestContext.Current.CancellationToken));
        Assert.Equal(42, ErrorCode(withKey));
    }

    [Fact]
    public async Task SignIn_CountsTowardTheLoginBrake()
    {
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) await SignIn(password: "wrong one");
        var (r, res) = await SignIn();
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal(40, ErrorCode(r));
    }

    [Fact]
    public async Task ParallelWrongPasswords_GetAtMostMaxFailuresGuesses()
    {
        // Check → verify → record used to be three steps, so N parallel requests all passed the
        // check and each got a guess. Attempts are now reserved before the password is verified.
        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => SignIn(password: "wrong one")));
        Assert.Equal(LoginThrottle.MaxFailures, responses.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(24 - LoginThrottle.MaxFailures, responses.Count(r => r.Response.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task SignInAgain_FromTheSameDevice_ReplacesItsKey()
    {
        var first = await SignInKey();
        var second = await SignInKey();
        Assert.NotEqual(first, second);
        Assert.Equal(40, ErrorCode(await GetWithHeader("getUser", first)));
        Assert.Equal("ok", (await GetWithHeader("getUser", second)).GetProperty("status").GetString());
        Assert.Single(_users.Devices());
    }

    [Fact]
    public async Task SignOut_RevokesTheCallingKey_Only()
    {
        var phone = await SignInKey("phone-0001");
        var tablet = await SignInKey("tablet-0001");
        Assert.Equal("ok", (await GetWithHeader("noctisSignOut", phone)).GetProperty("status").GetString());
        Assert.Equal(40, ErrorCode(await GetWithHeader("getUser", phone)));
        Assert.Equal("ok", (await GetWithHeader("getUser", tablet)).GetProperty("status").GetString());

        // The account's own key is not a device: nothing to sign out.
        var account = _users.RegenerateApiKey("alice");
        Assert.Equal(42, ErrorCode(await GetWithHeader("noctisSignOut", account)));
    }

    [Fact]
    public async Task HeaderKey_WinsOverApiKeyParam_ButStillConflictsWithAPassword()
    {
        var key = await SignInKey();
        Assert.Equal("ok", (await GetWithHeader("getUser", key, "apiKey=nk_garbage")).GetProperty("status").GetString());
        Assert.Equal(43, ErrorCode(await GetWithHeader("getUser", key, "u=alice&p=correct%20horse")));
    }

    [Fact]
    public async Task ParkedSignIn_RefusesSignInAndDeviceKeys_ButAccountAuthStillStreams()
    {
        // A phone signed in under an earlier build; this build's desktop parks the account features.
        var deviceKey = await SignInKey();
        var account = _users.RegenerateApiKey("alice");
        _http.Dispose();
        await _server.StopAsync();
        _server = new NoctisServer(_lib, _users, "test", sync: null) { PrivateClientsOnly = true, DeviceSignInEnabled = false };
        await _server.StartAsync(0, certificate: null);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };

        var (r, _) = await SignIn(deviceId: "phone-0003");
        Assert.Equal(0, ErrorCode(r));
        Assert.Contains("Unknown method", r.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, ErrorCode(await GetWithHeader("noctisSignOut", deviceKey)));
        Assert.Equal(40, ErrorCode(await GetWithHeader("getUser", deviceKey)));
        Assert.Single(_users.Devices()); // nothing issued or revoked

        // Password and account-key clients (v1.5.7 behaviour) are untouched.
        Assert.Equal("ok", (await GetWithHeader("getUser", account)).GetProperty("status").GetString());
        var viaPassword = Envelope(await _http.GetStringAsync("rest/getUser.view?f=json&u=alice&p=correct%20horse", TestContext.Current.CancellationToken));
        Assert.Equal("ok", viaPassword.GetProperty("status").GetString());
        using var stream = new HttpRequestMessage(HttpMethod.Get, $"rest/stream.view?id=tr-{_track.Id:N}");
        stream.Headers.Add(NoctisServer.KeyHeader, account);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(stream, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task DeletingTheUser_OrChangingItsPassword_RevokesItsKeys()
    {
        var key = await SignInKey();
        var account = _users.RegenerateApiKey("alice");
        _users.ChangePassword("alice", "battery staple");
        Assert.Equal(40, ErrorCode(await GetWithHeader("getUser", key)));
        Assert.Equal(40, ErrorCode(await GetWithHeader("getUser", account)));
        Assert.Empty(_users.Devices());

        _users.IssueDeviceKey("alice", "phone-0009", "P");
        Assert.True(_users.Delete("alice"));
        Assert.Empty(_users.Devices());
    }

    [Fact]
    public void DeviceKeys_AreCappedPerUser_LeastRecentlyUsedGoesFirst()
    {
        var first = _users.IssueDeviceKey("alice", "device-00", "D0");
        for (var i = 1; i <= ServerUserStore.MaxDevicesPerUser; i++)
            _users.IssueDeviceKey("alice", $"device-{i:00}", $"D{i}");
        Assert.Equal(ServerUserStore.MaxDevicesPerUser, _users.Devices().Count);
        Assert.Null(_users.ByApiKey(first)); // the oldest was signed out
        Assert.DoesNotContain(_users.Devices(), d => d.DeviceId == "device-00");
    }

    [Fact]
    public async Task Scrobble_Time_SetsWhenItWasPlayed_ClampsTheFuture_AndCountsRetriesOnce()
    {
        var key = await SignInKey();
        var other = Guid.NewGuid();
        var played = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
        var future = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        var query = $"submission=true&id=tr-{_track.Id:N}&time={played}&id=tr-{other:N}&time={future}";
        var before = DateTime.UtcNow;
        Assert.Equal("ok", (await GetWithHeader("scrobble", key, query)).GetProperty("status").GetString());

        Assert.Equal(2, _lib.Scrobbles.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(played).UtcDateTime, _lib.Scrobbles[0].Played);
        Assert.InRange(_lib.Scrobbles[1].Played, before.AddSeconds(-1), DateTime.UtcNow); // future → now

        // The phone retries the same batch (its reply was lost): not counted again.
        await GetWithHeader("scrobble", key, query);
        Assert.Equal(2, _lib.Scrobbles.Count);

        // Invalid or missing time → now; no time never dedupes (plain Subsonic clients).
        await GetWithHeader("scrobble", key, $"submission=true&id=tr-{_track.Id:N}&time=abc");
        await GetWithHeader("scrobble", key, $"submission=true&id=tr-{_track.Id:N}");
        await GetWithHeader("scrobble", key, $"submission=true&id=tr-{_track.Id:N}");
        Assert.Equal(5, _lib.Scrobbles.Count);
        Assert.All(_lib.Scrobbles.Skip(2), s => Assert.InRange(s.Played, before.AddSeconds(-1), DateTime.UtcNow));

        // Too many ids: a form POST (a query string this long would exceed Kestrel's request-line limit).
        var fields = Enumerable.Range(0, NoctisServer.MaxScrobbleIds + 1).Select(_ => new KeyValuePair<string, string>("id", $"tr-{_track.Id:N}"))
            .Append(new("submission", "true")).Append(new("f", "json"));
        using var post = new HttpRequestMessage(HttpMethod.Post, "rest/scrobble.view") { Content = new FormUrlEncodedContent(fields) };
        post.Headers.Add(NoctisServer.KeyHeader, key);
        var tooMany = await _http.SendAsync(post, TestContext.Current.CancellationToken);
        Assert.Equal(0, ErrorCode(Envelope(await tooMany.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));
        Assert.Equal(5, _lib.Scrobbles.Count);
    }

    [Fact]
    public void ScrobbleTime_Bounds()
    {
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now, NoctisServer.ScrobbleTime(null, now));
        Assert.Equal(now, NoctisServer.ScrobbleTime(long.MaxValue, now));
        Assert.Equal(now, NoctisServer.ScrobbleTime(0, now)); // 1970: before 2000
        var hourAgo = now.AddHours(-1);
        Assert.Equal(hourAgo, NoctisServer.ScrobbleTime(new DateTimeOffset(hourAgo).ToUnixTimeMilliseconds(), now));
    }

    [Fact]
    public async Task CoverArt_Size_ServesAResizedJpeg_NoSizeServesTheOriginal()
    {
        var key = await SignInKey();
        async Task<HttpResponseMessage> Cover(string query)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"rest/getCoverArt.view?id=al-{AlbumA:N}{query}");
            req.Headers.Add(NoctisServer.KeyHeader, key);
            return await _http.SendAsync(req, TestContext.Current.CancellationToken);
        }

        foreach (var (asked, expected) in new[] { (256, 256), (300, 256), (5000, 1024), (1, 64) })
        {
            var res = await Cover($"&size={asked}");
            Assert.Equal("image/jpeg", res.Content.Headers.ContentType!.MediaType);
            using var bmp = SKBitmap.Decode(await res.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            // 900×600 source: longest side = the snapped size, never upscaled past 900.
            Assert.Equal(Math.Min(expected, 900), bmp.Width);
            var height = (int)Math.Round(Math.Min(expected, 900) * 600 / 900.0);
            Assert.InRange(bmp.Height, height - 1, height + 1);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "covers"), $"{AlbumA:N}-256-*.jpg")); // cached once

        var original = await Cover("");
        Assert.Equal("image/png", original.Content.Headers.ContentType!.MediaType);

        // A cover the resizer refuses is "not found", never the (possibly huge) original.
        _resizer = (_, _, _, _) => Task.FromResult<string?>(null);
        var refused = await Cover("&size=512&f=json");
        Assert.Equal(70, ErrorCode(Envelope(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task CoverResizer_RefusesOversizedSources()
    {
        var big = Path.Combine(_dir, "big.jpg");
        using (var f = File.Create(big)) f.SetLength(ServerCoverResizer.MaxSourceBytes + 1);
        var resizer = new ServerCoverResizer(Path.Combine(_dir, "covers2"));
        Assert.Null(await resizer.ResizeAsync(Guid.NewGuid(), big, 256, TestContext.Current.CancellationToken));
        var junk = Path.Combine(_dir, "junk.jpg");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });
        Assert.Null(await resizer.ResizeAsync(Guid.NewGuid(), junk, 256, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.32.0.9", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("100.64.0.1", true)]     // Tailscale CGNAT
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void PrivateClientPolicy(string address, bool allowed)
        => Assert.Equal(allowed, ClientAddressPolicy.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public async Task AlbumPlayCounts_NearIntMax_DoNotOverflow()
    {
        _track.PlayCount = int.MaxValue;
        _lib.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = "Beta", Artist = "X", AlbumArtist = "X", Album = "First", AlbumId = AlbumA, FilePath = _track.FilePath, PlayCount = int.MaxValue });
        var key = await SignInKey();
        var album = await GetWithHeader("getAlbum", key, $"id=al-{AlbumA:N}");
        Assert.Equal(2L * int.MaxValue, album.GetProperty("album").GetProperty("playCount").GetInt64());
        Assert.Equal("ok", (await GetWithHeader("getAlbumList2", key, "type=frequent")).GetProperty("status").GetString());
    }

    private sealed class FakeLibrary : IServerLibrary
    {
        public List<Track> Tracks { get; }
        private readonly Album _album;
        private readonly string _cover;
        public List<(Guid Id, DateTime Played)> Scrobbles { get; } = new();

        public FakeLibrary(Track track, Guid albumId, string cover)
        {
            Tracks = new List<Track> { track };
            _album = new Album { Id = albumId, Name = "First", Artist = "X", TrackCount = 1 };
            _cover = cover;
        }

        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), new List<Album> { _album }, new List<Artist> { new() { Id = Guid.NewGuid(), Name = "X" } }, new List<Playlist>()));
        public string? ArtworkPath(Guid albumId) => albumId == _album.Id ? _cover : null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => ScrobbleAsync(trackId, DateTime.UtcNow);
        public Task ScrobbleAsync(Guid trackId, DateTime playedUtc) { lock (Scrobbles) Scrobbles.Add((trackId, playedUtc)); return Task.CompletedTask; }
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
        public Task ApplyTrackStateAsync(Guid trackId, Noctis.Services.Sync.TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, Noctis.Services.Sync.PlaylistSyncState state) => Task.CompletedTask;
    }
}
