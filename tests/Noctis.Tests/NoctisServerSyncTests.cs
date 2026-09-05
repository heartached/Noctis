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

    private sealed class SyncPersistence : TestPersistenceService
    {
        public List<Playlist> Playlists { get; } = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Playlists.ToList());
    }

    private SyncPersistence _persistence = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("alice", "correct horse", isAdmin: true);
        _apiKey = _users.RegenerateApiKey("alice");
        _persistence = new SyncPersistence();
        _sync = new LibrarySyncService(() => _settings, _persistence);
        _lib = new FakeLibrary(new Track { Id = TrackA, Title = "Alpha", Artist = "X", Album = "A", FilePath = Path.Combine(_dir, "a.mp3"), Duration = TimeSpan.FromSeconds(100) });
        _server = new NoctisServer(_lib, _users, "test", _sync);
        await _server.StartAsync(0, certificate: null);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async Task DisposeAsync()
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

    private async Task<JsonElement> Push(object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"rest/pushNoctisSyncChanges.view?f=json&apiKey={_apiKey}", content);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
    }

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
            device = "phone-1", name = "Pixel",
            items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = stamp, payload = new { favorite = true, rating = 4, disliked = false, playCount = 3 } } },
        });
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());

        var applied = Assert.Single(_lib.AppliedTracks);
        Assert.Equal(TrackA, applied.Id);
        Assert.Equal(4, applied.State.Rating);
        Assert.True(applied.State.Favorite);
        Assert.Equal(3, applied.State.PlayCount);

        var pull = await Get("getNoctisSyncChanges", "since=0&device=phone-2&name=Tablet");
        var items = pull.GetProperty("noctisSync").GetProperty("items");
        var item = items.EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "track");
        Assert.Equal(TrackA.ToString("N"), item.GetProperty("id").GetString());
        Assert.Equal(4, item.GetProperty("payload").GetProperty("rating").GetInt32());
        Assert.Equal("phone-1", item.GetProperty("device").GetString());
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
        await Push(new { device = "phone-1", items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = now.ToString("O"), payload = new { favorite = true, rating = 5 } } } });
        var r = await Push(new { device = "phone-2", items = new[] { new { kind = "track", id = TrackA.ToString("N"), updatedUtc = now.AddMinutes(-10).ToString("O"), payload = new { favorite = false, rating = 1 } } } });
        Assert.Equal(0, r.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.Single(_lib.AppliedTracks);
        Assert.Equal(5, _lib.AppliedTracks[0].State.Rating);
    }

    [Fact]
    public async Task Push_Playlist_CreatesThenTombstoneDeletes()
    {
        var id = Guid.NewGuid();
        var t0 = DateTime.UtcNow;
        var r1 = await Push(new { device = "phone-1", items = new[] { new { kind = "playlist", id = id.ToString("N"), updatedUtc = t0.ToString("O"), payload = new { name = "Road trip", description = "", color = "#ff0000", trackIds = new[] { TrackA }, modifiedAt = t0.ToString("O"), deleted = false } } } });
        Assert.Equal(1, r1.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.Contains(_lib.Playlists, p => p.Id == id && p.Name == "Road trip");

        var r2 = await Push(new { device = "phone-1", items = new[] { new { kind = "playlist", id = id.ToString("N"), updatedUtc = t0.AddMinutes(1).ToString("O"), payload = new { name = "", description = "", color = "", trackIds = Array.Empty<Guid>(), modifiedAt = t0.AddMinutes(1).ToString("O"), deleted = true } } } });
        Assert.Equal(1, r2.GetProperty("noctisSync").GetProperty("applied").GetInt32());
        Assert.DoesNotContain(_lib.Playlists, p => p.Id == id);
    }

    [Fact]
    public async Task Pull_IncludesDesktopPlaylists_FromPersistence()
    {
        _persistence.Playlists.Add(new Playlist { Name = "Desk mix", TrackIds = { TrackA }, ModifiedAt = DateTime.UtcNow });
        var pull = await Get("getNoctisSyncChanges", "since=0&device=phone-1");
        var playlists = pull.GetProperty("noctisSync").GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "playlist").ToList();
        var mix = Assert.Single(playlists);
        Assert.Equal("Desk mix", mix.GetProperty("payload").GetProperty("name").GetString());
        Assert.Equal("desktop-test", mix.GetProperty("device").GetString());
    }

    [Fact]
    public async Task SyncOff_EndpointsAnswerNotAuthorized()
    {
        _settings.SyncEnabled = false;
        var r = await Get("getNoctisSyncChanges", "since=0&device=phone-1");
        Assert.Equal("failed", r.GetProperty("status").GetString());
        Assert.Equal(50, r.GetProperty("error").GetProperty("code").GetInt32());
        var status = await Get("getNoctisSyncStatus");
        Assert.False(status.GetProperty("noctisSync").GetProperty("enabled").GetBoolean());
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
