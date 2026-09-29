using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Account;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's server client in isolation (a scripted HttpMessageHandler, no network): where the
/// key travels, how server answers map to <see cref="NoctisErrorKind"/>s, the pin check, address
/// rules, and that everything the server sends is validated and capped before use.
/// </summary>
public class NoctisAccountClientTests
{
    private const string Url = "http://127.0.0.1:4533";
    private const string Key = "nk_secret-device-key";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Seen) Seen.Add((request, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string inner, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"" + inner + "}}", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(int code) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":" + code + ",\"message\":\"x\"}}}", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(string contentType, byte[] data)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return r;
    }

    private static NoctisServerClient Client(ScriptedHandler handler, string? key = Key) => new(_ => handler, Url, "", key);

    [Fact]
    public async Task TheKeyTravelsInTheHeaderOnly_NeverInAUrl()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", "acc-client-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("getCoverArt.view") => Bytes("image/jpeg", new byte[] { 0xFF, 0xD8, 1 }),
                var p when p.EndsWith("download.view") => Bytes("audio/flac", new byte[] { 1, 2, 3 }),
                var p when p.EndsWith("getNoctisSyncChanges.view") => Json(",\"noctisSync\":{\"seq\":0,\"items\":[]}"),
                var p when p.EndsWith("pushNoctisSyncChanges.view") => Json(",\"noctisSync\":{\"applied\":1}"),
                _ => Json(",\"scanStatus\":{\"count\":0}"),
            });
            using var client = Client(handler);
            var ct = TestContext.Current.CancellationToken;
            await client.GetSongCountAsync(ct);
            await client.GetSyncChangesAsync(0, "phone-0001", "Phone", ct);
            await client.PushSyncChangesAsync("phone-0001", "Phone", new[] { new PushItem("track", new string('a', 32), new { favorite = true }, DateTime.UtcNow) }, ct);
            await client.ScrobbleAsync(new[] { (Guid.NewGuid(), 1L) }, ct);
            await client.SignOutAsync(ct);
            Assert.True(await client.DownloadCoverAsync(Guid.NewGuid(), System.IO.Path.Combine(dir, "c.jpg"), ct));
            Assert.Equal("audio/flac", await client.DownloadTrackAsync(Guid.NewGuid(), System.IO.Path.Combine(dir, "t.part"), ct));

            Assert.Equal(7, handler.Seen.Count);
            foreach (var (request, body) in handler.Seen)
            {
                Assert.DoesNotContain(Key, request.RequestUri!.ToString());
                Assert.DoesNotContain("apiKey", request.RequestUri!.ToString());
                Assert.Equal(Key, Assert.Single(request.Headers.GetValues(NoctisServerClient.KeyHeader)));
                Assert.DoesNotContain(Key, body ?? string.Empty);
            }

            // Sign-in carries the password (hex-encoded, in the POST body) and no key at all.
            var signIn = new ScriptedHandler(_ => Json(",\"noctisSignIn\":{\"apiKey\":\"nk_new\",\"user\":\"alice\",\"server\":\"PC\",\"syncEnabled\":true}"));
            using var anonymous = Client(signIn, key: null);
            var result = await anonymous.SignInAsync("alice", "correct horse", "phone-0001", "Phone", ct);
            Assert.Equal("nk_new", result.ApiKey);
            Assert.True(result.SyncEnabled);
            var (req, form) = Assert.Single(signIn.Seen);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.False(req.Headers.Contains(NoctisServerClient.KeyHeader));
            Assert.DoesNotContain("correct", req.RequestUri!.ToString());
            Assert.Contains("p=enc%3A" + Convert.ToHexString(Encoding.UTF8.GetBytes("correct horse")), form);
            Assert.Contains("deviceId=phone-0001", form);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Sends <paramref name="head"/>, then nothing ever (until the reader gives up): a
    /// Wi-Fi that dropped mid-download delivers no bytes and no error.</summary>
    internal sealed class StallingStream(byte[] head) : System.IO.Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                head.CopyTo(buffer);
                return head.Length;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    [Fact]
    public async Task ADownloadThatStopsReceiving_FailsAsUnreachable_InsteadOfWaitingForever()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", "acc-stall-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var handler = new ScriptedHandler(_ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(new byte[] { 1, 2, 3 })) };
                r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac");
                return r;
            });
            using var client = new NoctisServerClient(_ => handler, Url, "", Key) { StallTimeout = TimeSpan.FromMilliseconds(300) };

            var ex = await Assert.ThrowsAsync<NoctisServerException>(() =>
                client.DownloadTrackAsync(Guid.NewGuid(), System.IO.Path.Combine(dir, "t.part"), ct).WaitAsync(TimeSpan.FromSeconds(15), ct));

            Assert.Equal(NoctisErrorKind.Unreachable, ex.Kind);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task TheProbeSendsNoCredentials()
    {
        var handler = new ScriptedHandler(_ => Json(""));
        Assert.Equal(string.Empty, await NoctisServerClient.ProbeFingerprintAsync(_ => handler, Url, TestContext.Current.CancellationToken));
        var (request, body) = Assert.Single(handler.Seen);
        Assert.False(request.Headers.Contains(NoctisServerClient.KeyHeader));
        Assert.DoesNotContain("apiKey", request.RequestUri!.Query);
        Assert.DoesNotContain("u=", request.RequestUri!.Query);
        Assert.Null(body);
    }

    [Theory]
    [InlineData(40, false, NoctisErrorKind.SignedOut)]
    [InlineData(40, true, NoctisErrorKind.BadCredentials)]
    [InlineData(41, true, NoctisErrorKind.BadCredentials)]
    [InlineData(50, false, NoctisErrorKind.SyncDisabled)]
    [InlineData(70, false, NoctisErrorKind.Server)]
    [InlineData(42, false, NoctisErrorKind.Server)]
    [InlineData(10, true, NoctisErrorKind.Server)]
    public async Task ServerErrors_MapToKinds(int code, bool signingIn, NoctisErrorKind expected)
    {
        using var client = Client(new ScriptedHandler(_ => Error(code)), key: signingIn ? null : Key);
        var ex = await Assert.ThrowsAsync<NoctisServerException>(() => signingIn
            ? client.SignInAsync("alice", "pw", "phone-0001", "Phone", TestContext.Current.CancellationToken)
            : client.GetSongCountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Lockout_Unreachable_AndGarbage_MapToKinds()
    {
        var ct = TestContext.Current.CancellationToken;
        var locked = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        locked.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
        using (var client = Client(new ScriptedHandler(_ => locked), key: null))
            Assert.Equal(NoctisErrorKind.LockedOut, (await Assert.ThrowsAsync<NoctisServerException>(() => client.SignInAsync("a", "b", "phone-0001", "P", ct))).Kind);

        using (var client = Client(new ScriptedHandler(_ => throw new HttpRequestException("refused https://host/?apiKey=nk_x"))))
            Assert.Equal(NoctisErrorKind.Unreachable, (await Assert.ThrowsAsync<NoctisServerException>(() => client.GetSongCountAsync(ct))).Kind);

        using (var client = Client(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>router login</html>") })))
            Assert.Equal(NoctisErrorKind.Server, (await Assert.ThrowsAsync<NoctisServerException>(() => client.GetSongCountAsync(ct))).Kind);

        using (var client = Client(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("") })))
            Assert.Equal(NoctisErrorKind.Server, (await Assert.ThrowsAsync<NoctisServerException>(() => client.GetSongCountAsync(ct))).Kind);

        // No sign-in without a sign-in answer (an old desktop that lacks noctisSignIn).
        using (var client = Client(new ScriptedHandler(_ => Json("")), key: null))
            Assert.Equal(NoctisErrorKind.Server, (await Assert.ThrowsAsync<NoctisServerException>(() => client.SignInAsync("a", "b", "phone-0001", "P", ct))).Kind);
    }

    [Fact]
    public void PinCheck_AcceptsOnlyThePinnedLeaf_WhateverItsChainSays()
    {
        using var pinnedCert = ServerCertificate.Create();
        using var otherCert = ServerCertificate.Create();
        Func<X509Certificate2, bool>? check = null;
        using var client = new NoctisServerClient(accept => { check = accept; return new ScriptedHandler(_ => Json("")); },
            "https://192.168.1.20:5443", ServerCertificate.Fingerprint(pinnedCert).ToLowerInvariant(), Key);

        Assert.NotNull(check);
        Assert.True(check!(pinnedCert));   // self-signed: no chain would validate, the pin decides
        Assert.False(check(otherCert));
        Assert.Equal(ServerCertificate.Fingerprint(pinnedCert), NoctisServerClient.Fingerprint(pinnedCert));
        Assert.Throws<NoctisServerException>(() => NoctisServerClient.ParseFingerprint("AB:CD"));
        Assert.Throws<NoctisServerException>(() => NoctisServerClient.ParseFingerprint(ServerCertificate.Fingerprint(pinnedCert) + ":ZZ"));
    }

    [Theory]
    [InlineData("192.168.1.20:5443", "https://192.168.1.20:5443")]
    [InlineData("https://Desk.local:5443/", "https://desk.local:5443")]
    [InlineData(" https://desk.local ", "https://desk.local")]
    [InlineData("http://127.0.0.1:4533", "http://127.0.0.1:4533")]
    [InlineData("http://localhost:4533/", "http://localhost:4533")]
    [InlineData("https://[fe80::1]:5443", "https://[fe80::1]:5443")]
    public void ServerAddress_IsNormalised(string input, string expected)
        => Assert.Equal(expected, NoctisServerClient.NormalizeServerUrl(input));

    [Theory]
    [InlineData("")]
    [InlineData("http://192.168.1.20:4533")]          // plain http off loopback
    [InlineData("https://desk.local/rest")]          // a path
    [InlineData("https://desk.local/?x=1")]          // a query
    [InlineData("https://desk.local/#frag")]         // a fragment
    [InlineData("https://user:pw@desk.local")]       // user info
    [InlineData("ftp://desk.local")]
    [InlineData("https://")]
    public void ServerAddress_RejectsAnythingButSchemeHostPort(string input)
        => Assert.Equal(NoctisErrorKind.InvalidAddress, Assert.Throws<NoctisServerException>(() => NoctisServerClient.NormalizeServerUrl(input)).Kind);

    [Fact]
    public void CatalogEntries_AreValidatedAndCapped()
    {
        var longTitle = new string('t', 5000);
        var id = Guid.NewGuid();
        var album = Guid.NewGuid();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "tr-" + id.ToString("N"), albumId = "al-" + album.ToString("N"), title = longTitle, artist = "A", album = "B",
            duration = 200, suffix = "../x", userRating = 9, playCount = -4, starred = "2026-01-01T00:00:00Z", track = 3,
        }));
        var song = NoctisServerClient.ParseSong(doc.RootElement)!;
        Assert.Equal(id, song.Id);
        Assert.Equal(album, song.AlbumId);
        Assert.Equal(500, song.Title.Length);
        Assert.Equal(string.Empty, song.Suffix);    // not a plain extension
        Assert.Equal(5, song.UserRating);
        Assert.Equal(0, song.PlayCount);
        Assert.True(song.Starred);

        foreach (var bad in new[] { "tr-../../etc", "al-" + id.ToString("N"), "tr-" + id.ToString("D"), id.ToString("N"), "tr-" + new string('0', 32) })
        {
            using var d = JsonDocument.Parse(JsonSerializer.Serialize(new { id = bad, title = "x" }));
            Assert.Null(NoctisServerClient.ParseSong(d.RootElement));
        }
    }

    [Fact]
    public void PulledStates_AreClamped()
    {
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        using var track = JsonDocument.Parse("{\"favorite\":true,\"rating\":42,\"disliked\":\"yes\",\"playCount\":-9,\"lastPlayed\":\"2099-01-01T00:00:00Z\",\"favoritedAt\":\"2020-01-01T00:00:00Z\"}");
        var s = NoctisAccountService.ParseTrackState(track.RootElement, now);
        Assert.True(s.Favorite);
        Assert.Equal(5, s.Rating);
        Assert.False(s.Disliked);
        Assert.Equal(0, s.PlayCount);
        Assert.Equal(now, s.LastPlayed);
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), s.FavoritedAt);
    }

    [Fact]
    public void PushChunks_StayWithinTheServersItemAndSizeLimits()
    {
        static PushItem Track(int i) => new(SyncKinds.Track, i.ToString("x32"), new TrackSyncState(true, 3, false, 0, null, null), DateTime.UtcNow);
        Assert.Equal(new[] { 2000, 2000, 500 }, NoctisAccountService.PushChunks(Enumerable.Range(0, 4500).Select(Track), x => x).Select(c => c.Count));

        // Ten full playlists (10000 ids each, ~390 KB) cannot share one 4 MB request.
        static PushItem Big(int i) => new(SyncKinds.Playlist, i.ToString("x32"),
            new PlaylistSyncState("p", "", "", Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).ToList(), DateTime.UtcNow, false), DateTime.UtcNow);
        var chunks = NoctisAccountService.PushChunks(Enumerable.Range(0, 10).Select(Big), x => x).ToList();
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(c.Select(p => p.Payload).ToList(), SyncJson.Options)) < 4 * 1024 * 1024));
        Assert.Equal(10, chunks.Sum(c => c.Count));
    }

    [Fact]
    public async Task Scrobbles_PairEachIdWithItsTime()
    {
        var handler = new ScriptedHandler(_ => Json(""));
        using var client = Client(handler);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await client.ScrobbleAsync(new[] { (a, 1000L), (b, 2000L) }, TestContext.Current.CancellationToken);
        var (_, body) = Assert.Single(handler.Seen);
        Assert.Equal($"submission=true&id=tr-{a:N}&time=1000&id=tr-{b:N}&time=2000", body);
    }

    [Theory]
    [InlineData("noctis-remote://tr-0123456789abcdef0123456789abcdef", true)]
    [InlineData("noctis-remote://tr-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("noctis-remote://tr-0123456789abcdef0123456789abcde", false)]
    [InlineData("noctis-remote://tr-0123456789abcdef0123456789abcdef0", false)]
    [InlineData("noctis-remote://tr-../../x", false)]
    [InlineData("NOCTIS-REMOTE://tr-0123456789abcdef0123456789abcdef", false)]
    [InlineData("noctis-remote://tr-0123456789abcdef0123456789abcde/", false)]
    [InlineData("noctis-remote://tr-0123456789abcdef0123456789abcdeg", false)]
    public void RemotePaths_ParseStrictly(string path, bool valid)
        => Assert.Equal(valid, NoctisRemoteIds.TryParsePath(path, out _));
}
