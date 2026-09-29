using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LocalApi;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Local API (WebRemoteServer in LocalApi mode) end to end: a real server on an
/// ephemeral loopback port, a real PlayerViewModel over the fakes, HttpClient on the
/// wire. [AvaloniaFact] so the server's UI-thread marshal has a pumped dispatcher.
/// </summary>
public class LocalApiServerTests
{
    private const string SecretDir = "zz_secret_music_dir";

    private sealed class FakeLyrics : ILocalApiLyricsSource
    {
        public LocalApiLyrics? Value { get; set; }
        public LocalApiLyrics? Snapshot() => Value;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Harness : IDisposable
    {
        public FakeAudioPlayer Audio { get; } = new();
        public FakeLibraryService Library { get; } = new();
        public TestPersistenceService Persistence { get; } = new();
        public FakeLyrics Lyrics { get; } = new();
        public PlayerViewModel Player { get; }
        public WebRemoteServer Server { get; }
        public HttpClient Http { get; }
        public string Token { get; } = LocalApiTokenStore.NewToken();

        public Harness(TimeSpan? heartbeat = null, int maxStreams = 8, int maxBody = 16 * 1024, int authLimit = 10)
        {
            Player = new PlayerViewModel(Audio, Library, Persistence, new FakeAnimatedCoverService());
            Server = new WebRemoteServer(Player, new LocalApiOptions
            {
                Library = Library,
                Persistence = Persistence,
                Lyrics = Lyrics,
                Heartbeat = heartbeat ?? TimeSpan.FromSeconds(15),
                MaxEventStreams = maxStreams,
                MaxBodyBytes = maxBody,
                AuthFailureLimit = authLimit,
                AppVersion = "9.9.9",
            });
            Server.Start(0, Token);
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Server.Port}"), Timeout = TimeSpan.FromSeconds(10) };
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        public Track AddTrack(string title, string artist = "Artist A", string album = "Album A")
        {
            var t = new Track
            {
                Id = Guid.NewGuid(),
                Title = title,
                Artist = artist,
                AlbumArtist = artist,
                Album = album,
                FilePath = TestPaths.Primary(SecretDir, $"{title}.flac"),
                Duration = TimeSpan.FromMinutes(3),
            };
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
            Library.TrackList.Add(t);
            return t;
        }

        public async Task<(HttpStatusCode Status, JsonElement Json, string Raw)> GetAsync(string path)
        {
            using var resp = await Http.GetAsync(path);
            var raw = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
        }

        public async Task<(HttpStatusCode Status, JsonElement Json, string Raw)> PostAsync(string path, string? json = null)
        {
            using var content = new StringContent(json ?? "", Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(path, json == null ? null : content);
            var raw = await resp.Content.ReadAsStringAsync();
            return (resp.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
        }

        public void Dispose()
        {
            Http.Dispose();
            Server.Dispose();
            Persistence.Dispose();
        }
    }

    private static void AssertError(JsonElement json, string code)
    {
        var err = json.GetProperty("error");
        Assert.Equal(code, err.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(err.GetProperty("message").GetString()));
    }

    // ── auth ──

    [AvaloniaFact]
    public async Task MissingOrWrongToken_Is401_WithErrorShape()
    {
        using var h = new Harness();
        using var anon = new HttpClient { BaseAddress = h.Http.BaseAddress };

        using var none = await anon.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        AssertError(JsonDocument.Parse(await none.Content.ReadAsStringAsync()).RootElement, "unauthorized");

        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        using var wrong = await anon.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // A route that doesn't exist still answers 401 first: unauthenticated callers
        // can't map the surface.
        using var probe = await anon.GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
    }

    [AvaloniaFact]
    public async Task BearerHeader_And_QueryToken_BothAuthorize()
    {
        using var h = new Harness();
        var (status, json, _) = await h.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json.GetProperty("apiVersion").GetInt32());

        using var anon = new HttpClient { BaseAddress = h.Http.BaseAddress };
        using var viaQuery = await anon.GetAsync($"/api/v1/status?token={h.Token}");
        Assert.Equal(HttpStatusCode.OK, viaQuery.StatusCode);
    }

    [AvaloniaFact]
    public async Task RepeatedAuthFailures_AreRateLimited()
    {
        using var h = new Harness(authLimit: 3);
        using var anon = new HttpClient { BaseAddress = h.Http.BaseAddress };
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/status?token=bad")).StatusCode);

        using var locked = await anon.GetAsync("/api/v1/status?token=bad");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
        AssertError(JsonDocument.Parse(await locked.Content.ReadAsStringAsync()).RootElement, "rate_limited");
    }

    [AvaloniaFact]
    public async Task SetToken_RejectsTheOldToken()
    {
        using var h = new Harness();
        var fresh = LocalApiTokenStore.NewToken();
        h.Server.SetToken(fresh);

        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Http.GetAsync("/api/v1/status")).StatusCode);
        using var anon = new HttpClient { BaseAddress = h.Http.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/v1/status?token={fresh}")).StatusCode);
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "b", false)]
    [InlineData("", "a", false)]
    [InlineData(null, "a", false)]
    [InlineData("abc", "abcd", false)]
    public void TokenMatches_IsExact(string? candidate, string token, bool expected) =>
        Assert.Equal(expected, WebRemoteServer.TokenMatches(candidate, token));

    // ── loopback only ──

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("::ffff:192.168.1.10", false)]
    public void IsLoopbackAddress_OnlyLoopback(string ip, bool expected) =>
        Assert.Equal(expected, WebRemoteServer.IsLoopbackAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1:9421", true)]
    [InlineData("localhost:9421", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]:9421", true)]
    [InlineData("evil.example:9421", false)]
    [InlineData("192.168.1.10:9421", false)]
    [InlineData("127.0.0.1.evil.example", false)]
    public void IsLoopbackHost_RejectsRebindingNames(string host, bool expected) =>
        Assert.Equal(expected, WebRemoteServer.IsLoopbackHost(host));

    [AvaloniaFact]
    public async Task LocalMode_BindsLoopbackOnly_AndRefusesForeignHostHeaders()
    {
        using var h = new Harness();

        // Not reachable on this machine's LAN address (when it has one).
        var lan = WebRemoteServer.GetLocalAddress();
        if (lan != null && !IPAddress.IsLoopback(IPAddress.Parse(lan)))
        {
            using var probe = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () =>
                await probe.ConnectAsync(IPAddress.Parse(lan), h.Server.Port).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        req.Headers.Host = "evil.example";
        using var resp = await h.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        AssertError(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement, "forbidden_host");
    }

    [AvaloniaFact]
    public async Task Preflight_GetsCorsHeaders_WithoutAToken()
    {
        using var h = new Harness();
        using var anon = new HttpClient { BaseAddress = h.Http.BaseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Options, "/api/v1/now-playing");
        req.Headers.Add("Origin", "null");
        req.Headers.Add("Access-Control-Request-Method", "GET");
        req.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var resp = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("*", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Authorization", resp.Headers.GetValues("Access-Control-Allow-Headers").Single());

        using var get = await h.Http.GetAsync("/api/v1/status");
        Assert.Equal("*", get.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    // ── request hygiene ──

    [AvaloniaFact]
    public async Task WrongMethod_Is405_UnknownRoute_Is404_OversizedBody_Is413()
    {
        using var h = new Harness(maxBody: 64);

        using var getNext = await h.Http.GetAsync("/api/v1/playback/next");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getNext.StatusCode);
        AssertError(JsonDocument.Parse(await getNext.Content.ReadAsStringAsync()).RootElement, "method_not_allowed");

        var (postNow, postNowJson, _) = await h.PostAsync("/api/v1/now-playing", "{}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postNow);
        AssertError(postNowJson, "method_not_allowed");

        var (unknown, unknownJson, _) = await h.GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown);
        AssertError(unknownJson, "not_found");

        var big = "{\"volume\": 10, \"pad\": \"" + new string('x', 200) + "\"}";
        var (tooBig, tooBigJson, _) = await h.PostAsync("/api/v1/playback/volume", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig);
        AssertError(tooBigJson, "payload_too_large");

        var (badJson, badJsonBody, _) = await h.PostAsync("/api/v1/playback/volume", "{nope");
        Assert.Equal(HttpStatusCode.BadRequest, badJson);
        AssertError(badJsonBody, "bad_request");
    }

    // ── endpoint shapes ──

    [AvaloniaFact]
    public async Task Status_And_NowPlaying_Shapes()
    {
        using var h = new Harness();
        var (s0, idle, _) = await h.GetAsync("/api/v1/now-playing");
        Assert.Equal(HttpStatusCode.OK, s0);
        Assert.Equal("stopped", idle.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("track").ValueKind);

        var a = h.AddTrack("First Song", artist: "Kanye West, GLC");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a }, 0);

        var (_, status, _) = await h.GetAsync("/api/v1/status");
        Assert.Equal("Noctis", status.GetProperty("app").GetString());
        Assert.Equal("9.9.9", status.GetProperty("appVersion").GetString());
        Assert.Equal("playing", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("playing").GetBoolean());

        var (_, np, _) = await h.GetAsync("/api/v1/now-playing");
        Assert.Equal("First Song", np.GetProperty("title").GetString());
        Assert.Equal(new[] { "Kanye West", "GLC" }, np.GetProperty("artists").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("Album A", np.GetProperty("album").GetString());
        Assert.Equal("Kanye West, GLC", np.GetProperty("albumArtist").GetString());
        Assert.Equal(180000, np.GetProperty("durationMs").GetInt64());
        Assert.True(np.TryGetProperty("positionMs", out _));
        Assert.Equal("playing", np.GetProperty("state").GetString());
        Assert.False(np.GetProperty("shuffle").GetBoolean());
        Assert.Equal("off", np.GetProperty("repeat").GetString());
        Assert.True(np.TryGetProperty("volume", out _));
        Assert.Equal(a.Id, np.GetProperty("trackId").GetGuid());
        Assert.Equal($"/api/v1/artwork/{a.Id}", np.GetProperty("artworkUrl").GetString());
        Assert.Equal(a.Id, np.GetProperty("track").GetProperty("id").GetGuid());
    }

    [AvaloniaFact]
    public async Task PostControls_DriveThePlayer()
    {
        using var h = new Harness();
        var a = h.AddTrack("A");
        var b = h.AddTrack("B");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);

        var (st, toggled, _) = await h.PostAsync("/api/v1/playback/toggle");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.True(toggled.GetProperty("ok").GetBoolean());
        Assert.Equal("paused", toggled.GetProperty("playback").GetProperty("state").GetString());
        Assert.Equal(PlaybackState.Paused, h.Player.State);

        await h.PostAsync("/api/v1/playback/pause"); // idempotent
        Assert.Equal(PlaybackState.Paused, h.Player.State);
        await h.PostAsync("/api/v1/playback/play");
        Assert.Equal(PlaybackState.Playing, h.Player.State);
        await h.PostAsync("/api/v1/playback/play"); // idempotent
        Assert.Equal(PlaybackState.Playing, h.Player.State);

        await h.PostAsync("/api/v1/playback/volume", "{\"volume\": 30}");
        Assert.Equal(30, h.Player.Volume);
        var (badVol, badVolJson, _) = await h.PostAsync("/api/v1/playback/volume", "{\"volume\": 101}");
        Assert.Equal(HttpStatusCode.BadRequest, badVol);
        AssertError(badVolJson, "bad_request");

        await h.PostAsync("/api/v1/playback/seek", "{\"positionMs\": 60000}");
        Assert.Equal(60, h.Player.Position.TotalSeconds, 0.5);

        await h.PostAsync("/api/v1/playback/shuffle", "{\"enabled\": true}");
        Assert.True(h.Player.IsShuffleEnabled);
        await h.PostAsync("/api/v1/playback/shuffle", "{\"enabled\": true}"); // stays on
        Assert.True(h.Player.IsShuffleEnabled);
        await h.PostAsync("/api/v1/playback/shuffle"); // no body: toggle
        Assert.False(h.Player.IsShuffleEnabled);

        await h.PostAsync("/api/v1/playback/repeat", "{\"mode\": \"one\"}");
        Assert.Equal(RepeatMode.One, h.Player.RepeatMode);
        await h.PostAsync("/api/v1/playback/repeat", "{\"mode\": \"off\"}");
        Assert.Equal(RepeatMode.Off, h.Player.RepeatMode);
        var (badRepeat, _, _) = await h.PostAsync("/api/v1/playback/repeat", "{\"mode\": \"sometimes\"}");
        Assert.Equal(HttpStatusCode.BadRequest, badRepeat);

        await h.PostAsync("/api/v1/playback/next");
        Assert.Equal(b.Id, h.Player.CurrentTrack!.Id);
        await h.PostAsync("/api/v1/playback/previous");
        Assert.Equal(a.Id, h.Player.CurrentTrack!.Id);
    }

    [AvaloniaFact]
    public async Task Seek_WithNothingPlaying_IsConflict()
    {
        using var h = new Harness();
        var (st, json, _) = await h.PostAsync("/api/v1/playback/seek", "{\"positionMs\": 1000}");
        Assert.Equal(HttpStatusCode.Conflict, st);
        AssertError(json, "nothing_playing");
    }

    [AvaloniaFact]
    public async Task Queue_IsCapped_And_QueueAdd_HonoursMode()
    {
        using var h = new Harness();
        var tracks = Enumerable.Range(0, 6).Select(i => h.AddTrack($"T{i}")).ToList();
        h.Player.ReplaceQueueAndPlay(tracks.Take(4).ToList(), 0); // T0 playing, T1..T3 up next

        var (_, q, _) = await h.GetAsync("/api/v1/queue?limit=2");
        Assert.Equal("T0", q.GetProperty("current").GetProperty("title").GetString());
        Assert.Equal(2, q.GetProperty("upNext").GetArrayLength());
        Assert.Equal(3, q.GetProperty("upNextTotal").GetInt32());
        Assert.True(q.GetProperty("truncated").GetBoolean());
        var (badLimit, _, _) = await h.GetAsync($"/api/v1/queue?limit={WebRemoteServer.QueueMaxLimit + 1}");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit);

        var missing = Guid.NewGuid();
        var (st, added, _) = await h.PostAsync("/api/v1/queue/add",
            $"{{\"trackIds\": [\"{tracks[4].Id}\", \"{tracks[5].Id}\", \"{missing}\"], \"mode\": \"next\"}}");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        Assert.Equal(missing, added.GetProperty("notFound")[0].GetGuid());
        Assert.Equal(new[] { "T4", "T5", "T1", "T2", "T3" }, h.Player.UpNext.Select(t => t.Title).ToArray());

        await h.PostAsync("/api/v1/queue/add", $"{{\"trackIds\": [\"{tracks[0].Id}\"]}}"); // default: end
        Assert.Equal("T0", h.Player.UpNext[^1].Title);

        var (bad, badJson, _) = await h.PostAsync("/api/v1/queue/add", "{\"trackIds\": [\"not-a-guid\"]}");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        AssertError(badJson, "bad_request");
    }

    [AvaloniaFact]
    public async Task Search_ReturnsTracksAlbumsArtists_AndIsCapped()
    {
        using var h = new Harness();
        for (var i = 0; i < 60; i++) h.AddTrack($"Midnight {i}", artist: "Night Band", album: "Night Album");
        h.AddTrack("Unrelated", artist: "Someone", album: "Else");
        var albums = (List<Album>)h.Library.Albums;
        albums.Add(new Album { Id = Guid.NewGuid(), Name = "Night Album", Artist = "Night Band", TrackCount = 60 });
        h.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Night Band", TrackCount = 60, AlbumCount = 1 });

        var (st, def, _) = await h.GetAsync("/api/v1/library/search?q=midnight");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(WebRemoteServer.SearchDefaultLimit, def.GetProperty("tracks").GetArrayLength());

        var (_, max, _) = await h.GetAsync($"/api/v1/library/search?q=night&limit={WebRemoteServer.SearchMaxLimit}");
        Assert.Equal(WebRemoteServer.SearchMaxLimit, max.GetProperty("tracks").GetArrayLength());
        Assert.Equal("Night Album", max.GetProperty("albums")[0].GetProperty("name").GetString());
        Assert.Equal("Night Band", max.GetProperty("artists")[0].GetProperty("name").GetString());
        var t0 = max.GetProperty("tracks")[0];
        foreach (var field in new[] { "id", "title", "artists", "album", "albumArtist", "durationMs", "artworkUrl" })
            Assert.True(t0.TryGetProperty(field, out _), field);

        // Multi-word: every word must match somewhere in title / artist / album.
        var (_, multi, _) = await h.GetAsync("/api/v1/library/search?q=midnight%2059%20band");
        Assert.Equal("Midnight 59", multi.GetProperty("tracks")[0].GetProperty("title").GetString());

        var (tooMany, _, _) = await h.GetAsync($"/api/v1/library/search?q=night&limit={WebRemoteServer.SearchMaxLimit + 1}");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany);
        var (noQ, noQJson, _) = await h.GetAsync("/api/v1/library/search");
        Assert.Equal(HttpStatusCode.BadRequest, noQ);
        AssertError(noQJson, "bad_request");
    }

    [AvaloniaFact]
    public async Task Lyrics_ReportsSyncedWordLevelOrUnavailable()
    {
        using var h = new Harness();
        var a = h.AddTrack("Sung");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a }, 0);

        var (_, none, _) = await h.GetAsync("/api/v1/lyrics/current");
        Assert.False(none.GetProperty("available").GetBoolean());

        h.Lyrics.Value = new LocalApiLyrics(a.Id, true, true, new[]
        {
            new LocalApiLyricLine(1000, 3000, "hello world", new[] { new LocalApiLyricWord(1000, 1500, "hello "), new LocalApiLyricWord(1500, 3000, "world") }),
            new LocalApiLyricLine(3000, null, "second", null),
        }, "hello world\nsecond");

        var (_, ly, _) = await h.GetAsync("/api/v1/lyrics/current");
        Assert.True(ly.GetProperty("available").GetBoolean());
        Assert.True(ly.GetProperty("synced").GetBoolean());
        Assert.True(ly.GetProperty("wordLevel").GetBoolean());
        Assert.Equal(2, ly.GetProperty("lines").GetArrayLength());
        Assert.Equal(1000, ly.GetProperty("lines")[0].GetProperty("startMs").GetInt64());
        Assert.Equal("world", ly.GetProperty("lines")[0].GetProperty("words")[1].GetProperty("text").GetString());
        Assert.Equal("hello world\nsecond", ly.GetProperty("text").GetString());

        // Lyrics loaded for a different track (mid-switch) are never served.
        h.Lyrics.Value = h.Lyrics.Value with { TrackId = Guid.NewGuid() };
        var (_, stale, _) = await h.GetAsync("/api/v1/lyrics/current");
        Assert.False(stale.GetProperty("available").GetBoolean());
    }

    [Fact]
    public void LyricsSourceBuild_SeparatesSyncedPlainAndWordLevel()
    {
        var id = Guid.NewGuid();
        var synced = new List<LyricLine>
        {
            new() { Text = "...", Timestamp = TimeSpan.Zero, IsIntroPlaceholder = true },
            new() { Text = "one", Timestamp = TimeSpan.FromSeconds(1) },
            new() { Text = "two", Timestamp = TimeSpan.FromSeconds(2), Words = new[] { new WordTiming { Text = "two", Start = TimeSpan.FromSeconds(2) } } },
        };
        var built = LyricsViewModelLyricsSource.Build(id, synced, new List<LyricLine> { new() { Text = "one" }, new() { Text = "two" } })!;
        Assert.True(built.Synced);
        Assert.True(built.WordLevel);
        Assert.Equal(2, built.Lines.Count);
        Assert.Equal("one\ntwo", built.PlainText);

        var plain = LyricsViewModelLyricsSource.Build(id, new List<LyricLine> { new() { Text = "just text" } }, new List<LyricLine>())!;
        Assert.False(plain.Synced);
        Assert.False(plain.WordLevel);
        Assert.Null(plain.Lines[0].StartMs);

        Assert.Null(LyricsViewModelLyricsSource.Build(id, new List<LyricLine>(), new List<LyricLine>()));
    }

    [AvaloniaFact]
    public async Task Artwork_ServesImageBytes_ForCurrentAndById_And404sOtherwise()
    {
        using var h = new Harness();
        var a = h.AddTrack("Covered");
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var artPath = h.Persistence.GetArtworkPath(a.AlbumId);
        Directory.CreateDirectory(Path.GetDirectoryName(artPath)!);
        await File.WriteAllBytesAsync(artPath, png);

        using (var none = await h.Http.GetAsync("/api/v1/artwork/current"))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        h.Player.ReplaceQueueAndPlay(new List<Track> { a }, 0);
        foreach (var url in new[] { "/api/v1/artwork/current", $"/api/v1/artwork/{a.Id}" })
        {
            using var resp = await h.Http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/png", resp.Content.Headers.ContentType!.MediaType);
            Assert.Equal(png, await resp.Content.ReadAsByteArrayAsync());
        }

        using var unknown = await h.Http.GetAsync($"/api/v1/artwork/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertError(JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement, "no_artwork");
        using var notAnId = await h.Http.GetAsync("/api/v1/artwork/..%2F..%2Fsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);
    }

    [AvaloniaFact]
    public async Task Responses_NeverLeakFilePaths()
    {
        using var h = new Harness();
        var a = h.AddTrack("Leaky");
        a.AlbumArtworkPath = Path.Combine(Path.GetTempPath(), SecretDir, "cover.jpg");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a, h.AddTrack("Next") }, 0);
        h.Lyrics.Value = new LocalApiLyrics(a.Id, false, false, new[] { new LocalApiLyricLine(null, null, "la", null) }, "la");

        foreach (var url in new[] { "/api/v1/status", "/api/v1/now-playing", "/api/v1/queue", "/api/v1/library/search?q=leaky", "/api/v1/lyrics/current" })
        {
            var (st, _, raw) = await h.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, st);
            Assert.DoesNotContain(SecretDir, raw);
            Assert.DoesNotContain(".flac", raw);
            Assert.DoesNotContain("filePath", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── SSE ──

    private static async Task<List<string>> ReadUntilAsync(StreamReader reader, Func<string, bool> stop, TimeSpan timeout)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) throw new EndOfStreamException("stream closed; got: " + string.Join(" | ", lines));
            lines.Add(line);
            if (stop(line)) return lines;
        }
    }

    [AvaloniaFact]
    public async Task Events_EmitTrackChangedAndHeartbeat_ThenCleanUpOnDisconnect()
    {
        using var h = new Harness(heartbeat: TimeSpan.FromMilliseconds(300));
        var a = h.AddTrack("Alpha");
        var b = h.AddTrack("Bravo");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a, b }, 0);

        var resp = await h.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());

        // Initial snapshot: the current track.
        var initial = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("Alpha"), TimeSpan.FromSeconds(5));
        Assert.Contains("event: track-changed", initial);
        await ReadUntilAsync(reader, l => l == "event: state-changed", TimeSpan.FromSeconds(5));
        Assert.Equal(1, h.Server.ActiveEventStreams);

        // A real change is pushed.
        await h.PostAsync("/api/v1/playback/next");
        var changed = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("Bravo"), TimeSpan.FromSeconds(5));
        Assert.Contains("event: track-changed", changed);

        await ReadUntilAsync(reader, l => l.StartsWith(": ping"), TimeSpan.FromSeconds(5));

        reader.Dispose();
        resp.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Server.ActiveEventStreams > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(0, h.Server.ActiveEventStreams);
    }

    [AvaloniaFact]
    public async Task Events_AreCapped_AndClosedWhenTheTokenIsRegenerated()
    {
        using var h = new Harness(maxStreams: 1);
        using var first = await h.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var reader = new StreamReader(await first.Content.ReadAsStreamAsync());
        await ReadUntilAsync(reader, l => l == "event: track-changed", TimeSpan.FromSeconds(5));

        using var second = await h.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        AssertError(JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement, "too_many_streams");

        h.Server.SetToken(LocalApiTokenStore.NewToken());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await reader.ReadLineAsync(cts.Token) != null) { }
        Assert.Equal(0, h.Server.ActiveEventStreams);
    }

    [AvaloniaFact]
    public async Task Events_Position_FirstTickIsSent_ThenThrottled()
    {
        using var h = new Harness();
        var a = h.AddTrack("Ticking");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a }, 0);

        using var resp = await h.Http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadUntilAsync(reader, l => l == "event: state-changed", TimeSpan.FromSeconds(5));

        // The very first position change goes out (a sentinel overflow once swallowed
        // every tick until the first seek), the next ones inside a second don't.
        h.Player.Position = TimeSpan.FromMilliseconds(5000);
        h.Player.Position = TimeSpan.FromMilliseconds(5200);
        h.Player.Position = TimeSpan.FromMilliseconds(5400);
        var lines = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("\"positionMs\":5000"), TimeSpan.FromSeconds(5));
        Assert.Contains("event: position", lines);

        await Task.Delay(1100);
        h.Player.Position = TimeSpan.FromMilliseconds(6600);
        lines = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("positionMs"), TimeSpan.FromSeconds(5));
        Assert.Contains("6600", lines[^1]);
        Assert.DoesNotContain(lines, l => l.Contains("5200") || l.Contains("5400"));
    }

    [AvaloniaFact]
    public async Task Events_LyricsLine_OnlyForSubscribers()
    {
        using var h = new Harness();
        var a = h.AddTrack("Karaoke");
        h.Player.ReplaceQueueAndPlay(new List<Track> { a }, 0);
        h.Lyrics.Value = new LocalApiLyrics(a.Id, true, false, new[]
        {
            new LocalApiLyricLine(0, null, "first line", null),
            new LocalApiLyricLine(90_000, null, "later line", null),
        }, "first line\nlater line");

        using var resp = await h.Http.GetAsync("/api/v1/events?lyrics=1", HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var lines = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("first line"), TimeSpan.FromSeconds(5));
        Assert.Contains("event: lyrics-line", lines);

        await h.PostAsync("/api/v1/playback/seek", "{\"positionMs\": 95000}");
        lines = await ReadUntilAsync(reader, l => l.StartsWith("data:") && l.Contains("later line"), TimeSpan.FromSeconds(5));
        Assert.Contains("event: lyrics-line", lines);
    }
}

public class LocalApiTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "localapi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void LoadOrCreate_WritesA256BitToken_AndKeepsIt()
    {
        var store = new LocalApiTokenStore(_dir);
        var token = store.LoadOrCreateToken();
        Assert.Equal(64, token.Length);
        Assert.True(File.Exists(Path.Combine(_dir, "local-api.json")));
        Assert.Equal(token, new LocalApiTokenStore(_dir).LoadOrCreateToken());
    }

    [Fact]
    public void WriteState_RecordsPortAndUrl_AndRegenerateReplacesTheToken()
    {
        var store = new LocalApiTokenStore(_dir);
        var token = store.LoadOrCreateToken();
        store.WriteState(token, 9421, running: true);

        var json = JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement;
        Assert.Equal(9421, json.GetProperty("port").GetInt32());
        Assert.True(json.GetProperty("running").GetBoolean());
        Assert.Equal("http://127.0.0.1:9421/api/v1", json.GetProperty("baseUrl").GetString());
        Assert.Equal(1, json.GetProperty("apiVersion").GetInt32());
        Assert.Equal(token, json.GetProperty("token").GetString());

        var fresh = store.Regenerate();
        Assert.NotEqual(token, fresh);
        json = JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement;
        Assert.Equal(fresh, json.GetProperty("token").GetString());
        Assert.Equal(9421, json.GetProperty("port").GetInt32()); // state survives a regenerate

        store.WriteState(fresh, null, running: false);
        json = JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement;
        Assert.False(json.GetProperty("running").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("baseUrl").ValueKind);
    }

    [Fact]
    public void CorruptFile_GetsANewToken()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "local-api.json"), "{ not json");
        var token = new LocalApiTokenStore(_dir).LoadOrCreateToken();
        Assert.Equal(64, token.Length);
        Assert.Equal(token, new LocalApiTokenStore(_dir).ReadToken());
    }
}
