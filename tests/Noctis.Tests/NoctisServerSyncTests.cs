using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services.Server;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Account &amp; Sync endpoints over the real Kestrel server: a phone pushes its state, the
/// ledger keeps the newest write, the library adapter applies winners, and pulls hand back
/// everything after a sequence number.
/// </summary>
public class NoctisServerSyncTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-sync-srv-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private FakeLibrary _lib = null!;
    private LibrarySyncService _sync = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private string _apiKey = "";
    private readonly AppSettings _settings = new() { SyncEnabled = true, SyncDeviceId = "desktop-test", SyncDeviceName = "Test PC" };
    private static readonly Guid TrackA = Guid.NewGuid();
    private const int PageSize = 3;

    private sealed class SyncPersistence : TestPersistenceService
    {
        public List<Playlist> Playlists { get; } = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Playlists.ToList());
    }

    private SyncPersistence _persistence = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");
        _persistence = new SyncPersistence();
        _sync = new LibrarySyncService(() => _settings, _persistence) { ChangesPageSize = PageSize };
        _lib = new FakeLibrary(new Track { Id = TrackA, Title = "Alpha", Artist = "X", Album = "A", FilePath = Path.Combine(_dir, "a.mp3"), Duration = TimeSpan.FromSeconds(100) });
        _server = new NoctisServer(_lib, _users, "test", _sync);
        await _server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        _persistence.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<JsonElement> Get(string method, string query = "")
    {
        var json = await _http.GetStringAsync($"rest/{method}.view?f=json&apiKey={_apiKey}{(query.Length > 0 ? "&" + query : "")}");
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
    }

    private Task<JsonElement> Push(object body, string? deviceKey = null)
        => PushRaw(JsonSerializer.Serialize(body), deviceKey);

    private async Task<JsonElement> PushRaw(string body, string? deviceKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"rest/pushNoctisSyncChanges.view?f=json{(deviceKey is null ? "&apiKey=" + _apiKey : "")}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (deviceKey is not null) req.Headers.Add(NoctisServer.KeyHeader, deviceKey);
        var response = await _http.SendAsync(req);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
    }

    private async Task<JsonElement> GetWithKey(string method, string deviceKey, string query = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"rest/{method}.view?f=json{(query.Length > 0 ? "&" + query : "")}");
        req.Headers.Add(NoctisServer.KeyHeader, deviceKey);
        var json = await (await _http.SendAsync(req)).Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
    }

    private static object TrackItem(Guid id, DateTime stamp, object payload) => new { kind = "track", id = id.ToString("N"), updatedUtc = stamp.ToString("O"), payload };

    private static List<JsonElement> PulledItems(JsonElement pull) => pull.GetProperty("noctisSync").GetProperty("items").EnumerateArray().ToList();

    [Fact]
    public async Task Status_ReportsEnabled_AndThisDevice()
    {
        var r = await Get("getNoctisSyncStatus");
        Assert.Equal("ok", r.GetProperty("status").GetString());
        var s = r.GetProperty("noctisSync");
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal("desktop-test", s.GetProperty("device").GetString());
        Assert.Equal("Test PC", s.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Push_AppliesTrackState_AndPullReturnsIt()
    {
        var stamp = DateTime.UtcNow.ToString("O");
        var r = await Push(new
        {
            device = "phone-001", name = "Pixel",
            items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = stamp, payload = new { favorite = true, rating = 4, disliked = false, playCount = 3 } } },
        });
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());

        var applied = Assert.Single(_lib.AppliedTracks);
        Assert.Equal(TrackA, applied.Id);
        Assert.Equal(4, applied.State.Rating);
        Assert.True(applied.State.Favorite);
        Assert.Equal(3, applied.State.PlayCount);

        var pull = await Get("getNoctisSyncChanges", "since=0&device=phone-002&name=Tablet");
        var items = pull.GetProperty("noctisSync").GetProperty("items");
        var item = items.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "track");
        Assert.Equal(TrackA.ToString("N"), item.GetProperty("id").GetString());
        Assert.Equal(4, item.GetProperty("payload").GetProperty("rating").GetInt32());
        Assert.Equal("phone-001", item.GetProperty("device").GetString());
        Assert.True(pull.GetProperty("noctisSync").GetProperty("seq").GetInt64() >= 1);

        // Both devices are now known.
        var status = await Get("getNoctisSyncStatus");
        var names = status.GetProperty("noctisSync").GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()).ToList();
        Assert.Contains("Pixel", names);
        Assert.Contains("Tablet", names);
    }

    [Fact]
    public async Task Push_OlderState_IsIgnored()
    {
        var now = DateTime.UtcNow;
        await Push(new { device = "phone-001", items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = now.ToString("O"), payload = new { favorite = true, rating = 5 } } } });
        var r = await Push(new { device = "phone-002", items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = now.AddMinutes(-10).ToString("O"), payload = new { favorite = false, rating = 1 } } } });
        Assert.Equal(0, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.Single(_lib.AppliedTracks);
        Assert.Equal(5, _lib.AppliedTracks[0].State.Rating);
    }

    [Fact]
    public async Task Push_Playlist_CreatesThenTombstoneDeletes()
    {
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        var r1 = await Push(new { device = "phone-001", items = new[] { new { kind = "playlist", id = id.ToString("N"), updatedUtc = t0.ToString("O"), payload = new { name = "Road trip", description = "", color = "#ff0000", trackIds = new[] { TrackA }, modifiedAt = t0.ToString("O"), deleted = false } } } });
        Assert.Equal(1, r1.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.Contains(_lib.Playlists, p => p.Id == id && p.Name == "Road trip");

        var r2 = await Push(new { device = "phone-001", items = new[] { new { kind = "playlist", id = id.ToString("N"), updatedUtc = t0.AddMinutes(1).ToString("O"), payload = new { name = "", description = "", color = "", trackIds = Array.Empty<Guid>(), modifiedAt = t0.AddMinutes(1).ToString("O"), deleted = true } } } });
        Assert.Equal(1, r2.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.DoesNotContain(_lib.Playlists, p => p.Id == id);
    }

    [Fact]
    public async Task Pull_IncludesDesktopPlaylists_FromPersistence()
    {
        _persistence.Playlists.Add(new Playlist { Name = "Desk mix", TrackIds = { TrackA }, ModifiedAt = DateTime.UtcNow });
        var pull = await Get("getNoctisSyncChanges", "since=0&device=phone-001");
        var playlists = pull.GetProperty("noctisSync").GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "playlist").ToList();
        var mix = Assert.Single(playlists);
        Assert.Equal("Desk mix", mix.GetProperty("payload").GetProperty("name").GetString());
        Assert.Equal("desktop-test", mix.GetProperty("device").GetString());
    }

    [Fact]
    public async Task SyncOff_EndpointsAnswerNotAuthorized()
    {
        _settings.SyncEnabled = false;
        var r = await Get("getNoctisSyncChanges", "since=0&device=phone-001");
        Assert.Equal("failed", r.GetProperty("status").GetString());
        Assert.Equal(50, r.GetProperty("error").GetProperty("code").GetInt32());
        var status = await Get("getNoctisSyncStatus");
        Assert.False(status.GetProperty("noctisSync").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Pull_PagesAfterTheLastDeliveredItem_WithMore_AndALoopGetsEverything()
    {
        var now = DateTime.UtcNow;
        var ids = Enumerable.Range(0, 3 * PageSize + 1).Select(_ => Guid.NewGuid()).ToList();
        var push = await Push(new { device = "phone-001", items = ids.Select((id, i) => TrackItem(id, now.AddSeconds(-i), new { favorite = true, rating = 3 })).ToArray() });
        Assert.Equal(ids.Count, push.GetProperty("noctisSync").GetProperty("applied").GetInt32());

        var seen = new List<string>();
        long since = 0;
        var pages = 0;
        while (true)
        {
            var sync = (await Get("getNoctisSyncChanges", $"since={since}&device=phone-002")).GetProperty("noctisSync");
            var items = sync.GetProperty("items").EnumerateArray().ToList();
            var more = sync.GetProperty("more").GetBoolean();
            pages++;
            Assert.True(items.Count <= PageSize);
            seen.AddRange(items.Select(i => i.GetProperty("id").GetString()!));
            var seq = sync.GetProperty("seq").GetInt64();
            if (more) Assert.Equal(items[^1].GetProperty("seq").GetInt64(), seq); // resume after the last delivered item
            // The device's checkpoint is what it has actually received.
            Assert.Equal(seq, _sync.Devices().Single(d => d.Id == "phone-002").LastSeq);
            since = seq;
            if (!more) break;
            Assert.True(pages < 20);
        }
        Assert.Equal(ids.Select(i => i.ToString("N")).OrderBy(x => x), seen.OrderBy(x => x));
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(pages >= 4);
    }

    [Fact]
    public async Task Pull_CleansTheDeviceName_ACallerGives()
    {
        await Get("getNoctisSyncChanges", "since=0&device=phone-002&name=" + Uri.EscapeDataString("‮Tab​let\u0007 "));
        Assert.Equal("Tablet", _sync.Devices().Single(d => d.Id == "phone-002").Name);
    }

    [Fact]
    public async Task Pull_PagesByPayloadBytes_Too_AndALoopGetsEverything()
    {
        var now = DateTime.UtcNow;
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        await Push(new { device = "phone-001", items = ids.Select((id, i) => TrackItem(id, now.AddSeconds(-i), new { favorite = true, rating = 3 })).ToArray() });

        // Same ledger, a one-byte budget: every page carries exactly one item.
        var tiny = new LibrarySyncService(() => _settings, _persistence) { ChangesPageSize = 100, ChangesPageBytes = 1 };
        var seen = new List<string>();
        long since = 0;
        var pages = 0;
        while (true)
        {
            var page = await tiny.GetChangesAsync(since, "phone-002", null, TestContext.Current.CancellationToken);
            pages++;
            Assert.True(page.Items.Count <= 1);
            seen.AddRange(page.Items.Select(i => i.Id));
            if (page.More) Assert.Equal(page.Items[^1].Seq, page.Seq);
            since = page.Seq;
            if (!page.More) break;
            Assert.True(pages < 20);
        }
        Assert.Equal(ids.Select(i => i.ToString("N")).OrderBy(x => x), seen.OrderBy(x => x));
        Assert.Equal(ids.Count, pages);
    }

    [Fact]
    public async Task Push_FutureStamps_AreClamped_SoTheyCannotWinForever()
    {
        var far = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Push(new { device = "phone-001", items = new[] { TrackItem(TrackA, far, new { favorite = true, rating = 4, lastPlayed = far.ToString("O"), favoritedAt = far.ToString("O") }) } });
        var item = PulledItems(await Get("getNoctisSyncChanges", "since=0&device=phone-002")).Single(i => i.GetProperty("kind").GetString() == "track");
        var limit = DateTime.UtcNow + LibrarySyncService.MaxClockSkew + TimeSpan.FromSeconds(5);
        Assert.True(item.GetProperty("updatedUtc").GetDateTime().ToUniversalTime() <= limit);
        Assert.True(item.GetProperty("payload").GetProperty("lastPlayed").GetDateTime().ToUniversalTime() <= limit);
        Assert.True(item.GetProperty("payload").GetProperty("favoritedAt").GetDateTime().ToUniversalTime() <= limit);
        Assert.True(_lib.AppliedTracks.Single().State.LastPlayed <= limit);

        // A stamp with no zone is UTC, not this computer's local time.
        var id = Guid.NewGuid();
        var stamp = DateTime.UtcNow.AddHours(-3);
        await PushRaw($$$"""{"device":"phone-001","items":[{"kind":"track","id":"{{{id:N}}}","updatedUtc":"{{{stamp:yyyy-MM-ddTHH:mm:ss}}}","payload":{"rating":2}}]}""");
        var local = PulledItems(await Get("getNoctisSyncChanges", "since=0&device=phone-002")).Single(i => i.GetProperty("id").GetString() == id.ToString("N"));
        Assert.Equal(new DateTime(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second, DateTimeKind.Utc),
            local.GetProperty("updatedUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Push_Validates_AndStoresPayloadsReSerialized()
    {
        var now = DateTime.UtcNow;
        var upper = Guid.NewGuid();
        var r = await PushRaw($$$"""
            {"device":"phone-001","items":[
              {"kind":"album","id":"{{{Guid.NewGuid():N}}}","updatedUtc":"{{{now:O}}}","payload":{}},
              {"kind":"track","id":"tr-{{{Guid.NewGuid():N}}}","updatedUtc":"{{{now:O}}}","payload":{"rating":1}},
              {"kind":"track","id":"not-a-guid","updatedUtc":"{{{now:O}}}","payload":{"rating":1}},
              {"kind":"track","id":"{{{Guid.NewGuid():N}}}","updatedUtc":"{{{now:O}}}","payload":{"rating":"five"}},
              {"kind":"playlist","id":"{{{Guid.NewGuid():N}}}","updatedUtc":"{{{now:O}}}","payload":{"name":"{{{new string('n', LibrarySyncService.MaxPlaylistName + 1)}}}","modifiedAt":"{{{now:O}}}"}},
              {"kind":"playlist","id":"{{{Guid.NewGuid():N}}}","updatedUtc":"{{{now:O}}}","payload":{"name":"Bad colour","color":"red","modifiedAt":"{{{now:O}}}"}},
              {"kind":"track","id":"{{{upper.ToString("N").ToUpperInvariant()}}}","updatedUtc":"{{{now:O}}}","payload":{"rating":9,"playCount":2000000000,"extra":"<junk>"}}
            ]}
            """);
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());

        var item = Assert.Single(PulledItems(await Get("getNoctisSyncChanges", "since=0&device=phone-002")));
        Assert.Equal(upper.ToString("N"), item.GetProperty("id").GetString()); // normalised to lower-case "N"
        var payload = item.GetProperty("payload");
        Assert.Equal(5, payload.GetProperty("rating").GetInt32());
        Assert.Equal(LibrarySyncService.MaxPlayCountJump, payload.GetProperty("playCount").GetInt32());
        Assert.False(payload.TryGetProperty("extra", out _)); // re-serialised, never stored raw
    }

    [Fact]
    public async Task Push_TooManyItems_OrTooManyBytes_IsRefusedWhole()
    {
        var now = DateTime.UtcNow;
        var many = Enumerable.Range(0, NoctisServer.MaxSyncPushItems + 1).Select(_ => TrackItem(Guid.NewGuid(), now, new { rating = 1 })).ToArray();
        var r = await Push(new { device = "phone-001", items = many });
        Assert.Equal("failed", r.GetProperty("status").GetString());
        Assert.Empty(_lib.AppliedTracks);

        var huge = await PushRaw($$"""{"device":"phone-001","pad":"{{new string('x', 5 * 1024 * 1024)}}","items":[]}""");
        Assert.Equal("failed", huge.GetProperty("status").GetString());
        Assert.Contains("too large", huge.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task DeviceKey_SyncCalls_AreThatDevice_WhateverTheyClaim()
    {
        var key = _users.IssueDeviceKey("alice", "phone-key-01", "Pixel Key");
        var r = await Push(new
        {
            device = "spoofed-device", name = "Spoof",
            items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = DateTime.UtcNow.ToString("O"), device = "other-spoof", payload = (object)new { rating = 3 } } },
        }, key);
        Assert.Equal(1, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());

        // No device= needed with a device key; any given is ignored.
        var pull = await GetWithKey("getNoctisSyncChanges", key, "since=0&device=spoofed-device");
        Assert.Equal("phone-key-01", PulledItems(pull).Single().GetProperty("device").GetString());
        Assert.Equal("ok", (await GetWithKey("getNoctisSyncChanges", key, "since=0")).GetProperty("status").GetString());
        var devices = _sync.Devices();
        Assert.Contains(devices, d => d.Id == "phone-key-01" && d.Name == "Pixel Key");
        Assert.DoesNotContain(devices, d => d.Id is "spoofed-device" or "other-spoof");
    }

    [Fact]
    public async Task PasswordCallers_CannotNameABadId_AnotherAccountsDevice_OrThisComputer()
    {
        _users.Create("bob", "battery staple", isAdmin: false);
        _users.IssueDeviceKey("bob", "bobs-phone-01", "Bob's phone");
        static int Code(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

        // The sign-in id rule: 8–64 letters, digits, '-' or '_'.
        Assert.Equal(10, Code(await Get("getNoctisSyncChanges", "since=0&device=short")));
        Assert.Equal(10, Code(await Get("getNoctisSyncChanges", "since=0&device=" + Uri.EscapeDataString("phone 001!"))));
        // Another account's phone, or this computer's own ledger id.
        Assert.Equal(10, Code(await Get("getNoctisSyncChanges", "since=0&device=BOBS-PHONE-01")));
        Assert.Equal(10, Code(await Get("getNoctisSyncChanges", "since=0&device=desktop-test")));
        Assert.Equal(10, Code(await Push(new { device = "bobs-phone-01", items = Array.Empty<object>() })));
        var perItem = await Push(new
        {
            device = "phone-001",
            items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = DateTime.UtcNow.ToString("O"), device = "bobs-phone-01", payload = (object)new { rating = 3 } } },
        });
        Assert.Equal(10, Code(perItem));
        Assert.Empty(_lib.AppliedTracks);
        Assert.DoesNotContain(_sync.Devices(), d => d.Id is "short" or "bobs-phone-01" or "BOBS-PHONE-01");

        // A phone signed in to this same account is the caller's to name.
        _users.IssueDeviceKey("alice", "alices-phone-1", "Alice's phone");
        Assert.Equal("ok", (await Get("getNoctisSyncChanges", "since=0&device=alices-phone-1")).GetProperty("status").GetString());
    }

    private sealed class FakeLibrary : IServerLibrary
    {
        public List<Track> Tracks { get; }
        public List<Playlist> Playlists { get; } = new();
        public List<(Guid Id, TrackSyncState State)> AppliedTracks { get; } = new();

        public FakeLibrary(params Track[] tracks) => Tracks = tracks.ToList();

        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), new List<Album>(), new List<Artist>(), Playlists.ToList()));
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) { var p = new Playlist { Name = name, TrackIds = trackIds.ToList() }; Playlists.Add(p); return Task.FromResult(p); }
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(true);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(Playlists.RemoveAll(x => x.Id == id) > 0);
        public Task ApplyTrackStateAsync(Guid trackId, TrackSyncState state) { AppliedTracks.Add((trackId, state)); return Task.CompletedTask; }
        public Task ApplyPlaylistStateAsync(Guid playlistId, PlaylistSyncState state)
        {
            var p = Playlists.FirstOrDefault(x => x.Id == playlistId);
            if (state.Deleted) { if (p is not null) Playlists.Remove(p); }
            else
            {
                if (p is null) { p = new Playlist { Id = playlistId }; Playlists.Add(p); }
                p.Name = state.Name; p.TrackIds = state.TrackIds.ToList(); p.ModifiedAt = state.ModifiedAt;
            }
            return Task.CompletedTask;
        }
    }
}
