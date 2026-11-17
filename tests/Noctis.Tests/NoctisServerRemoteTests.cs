using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's remote for the desktop's player (NoctisServer.Remote.cs) over real Kestrel (plain
/// HTTP, loopback): who may drive it (a signed-in phone of the main account only), that commands
/// need a POST, what the answer carries, and the desktop's adapter over a real PlayerViewModel.
/// </summary>
public class NoctisServerRemoteTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-remote-" + Guid.NewGuid().ToString("N"));
    private ServerUserStore _users = null!;
    private NoctisServer _server = null!;
    private HttpClient _http = null!;
    private readonly FakeRemote _remote = new();

    private sealed class FakeRemote : IServerRemote
    {
        public List<RemoteCommand> Commands { get; } = new();
        public List<int> Snapshots { get; } = new();
        public bool Accept { get; set; } = true;
        public RemoteSnapshot State { get; set; } = new(
            new RemoteTrack(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "Song", "Artist", "Album", 200_000),
            true, 42_000, 200_000, 65, true, RemoteRepeat.All,
            new[] { new RemoteTrack(Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Empty, "Next One", "B", "C", 1000) }, 7);

        public Task<RemoteSnapshot> SnapshotAsync(int upNextLimit)
        {
            Snapshots.Add(upNextLimit);
            return Task.FromResult(State with { UpNext = State.UpNext.Take(upNextLimit).ToList() });
        }

        public Task<bool> ControlAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(Accept);
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _users = new ServerUserStore(Path.Combine(_dir, "users.db"));
        _users.Create("owner@example.com", "correct horse", isAdmin: true);
        _users.Create("guest@example.com", "battery staple");
        _server = new NoctisServer(new EmptyLibrary(), _users, "test", sync: null) { PrivateClientsOnly = true, Remote = _remote };
        await _server.StartAsync(0, certificate: null, bindAddress: System.Net.IPAddress.Loopback);
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
    private static int ErrorCode(JsonElement r) => r.GetProperty("error").GetProperty("code").GetInt32();

    private async Task<string> DeviceKey(string user = "owner@example.com", string password = "correct horse", string deviceId = "phone-0001")
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = user, ["p"] = password, ["deviceId"] = deviceId, ["deviceName"] = "Galaxy S23", ["f"] = "json",
        });
        var res = await _http.PostAsync("rest/noctisSignIn.view", form, TestContext.Current.CancellationToken);
        var r = Envelope(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return r.GetProperty("noctisSignIn").GetProperty("apiKey").GetString()!;
    }

    private async Task<JsonElement> Send(HttpMethod method, string methodName, string? key, IDictionary<string, string>? form = null, string query = "")
    {
        using var req = new HttpRequestMessage(method, $"rest/{methodName}.view?f=json{query}");
        if (key != null) req.Headers.Add(NoctisServer.KeyHeader, key);
        if (form != null) req.Content = new FormUrlEncodedContent(form);
        var res = await _http.SendAsync(req, TestContext.Current.CancellationToken);
        return Envelope(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task State_ForTheMainAccountsPhone_CarriesThePlayer_AndUpNextOnlyWhenAsked()
    {
        var key = await DeviceKey();

        var r = await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, key);
        Assert.Equal("ok", r.GetProperty("status").GetString());
        var s = r.GetProperty("noctisRemote");
        Assert.True(s.GetProperty("playing").GetBoolean());
        Assert.Equal(42_000, s.GetProperty("positionMs").GetInt64());
        Assert.Equal(200_000, s.GetProperty("durationMs").GetInt64());
        Assert.Equal(65, s.GetProperty("volume").GetInt32());
        Assert.True(s.GetProperty("shuffle").GetBoolean());
        Assert.Equal("all", s.GetProperty("repeat").GetString());
        Assert.Equal("tr-11111111111111111111111111111111", s.GetProperty("track").GetProperty("id").GetString());
        Assert.Equal("al-22222222222222222222222222222222", s.GetProperty("track").GetProperty("albumId").GetString());
        Assert.False(s.TryGetProperty("queue", out _));                       // not asked: not sent
        Assert.Equal(0, _remote.Snapshots[^1]);

        var withQueue = (await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, key, query: "&queue=500")).GetProperty("noctisRemote");
        Assert.Equal(NoctisServer.RemoteMaxQueue, _remote.Snapshots[^1]);    // capped
        var queue = withQueue.GetProperty("queue");
        Assert.Equal(7, queue.GetProperty("total").GetInt32());
        var entry = Assert.Single(queue.GetProperty("entry").EnumerateArray());
        Assert.Equal("Next One", entry.GetProperty("title").GetString());
        Assert.False(entry.TryGetProperty("albumId", out _));                 // no album: no id
    }

    [Fact]
    public async Task OnlyASignedInPhoneOfTheMainAccount_MayUseTheRemote()
    {
        // The password (no device key): refused, and nothing reached the player.
        var byPassword = await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, key: null, query: "&u=owner%40example.com&p=correct%20horse");
        Assert.Equal(50, ErrorCode(byPassword));

        // No credentials at all: the usual auth error, before the remote is asked.
        var anonymous = await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, key: null);
        Assert.Equal(10, ErrorCode(anonymous));

        // A wrong key: 40.
        Assert.Equal(40, ErrorCode(await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, "nk_not-a-key")));

        // Another account's phone: refused.
        var guestKey = await DeviceKey("guest@example.com", "battery staple", "phone-0002");
        Assert.Equal(50, ErrorCode(await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, guestKey)));
        Assert.Equal(50, ErrorCode(await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, guestKey,
            new Dictionary<string, string> { ["action"] = "toggle" })));

        Assert.Empty(_remote.Snapshots);
        Assert.Empty(_remote.Commands);
    }

    [Fact]
    public async Task AHostWithoutAPlayer_AnswersError50()
    {
        var key = await DeviceKey();
        _server.Remote = null;
        Assert.Equal(50, ErrorCode(await Send(HttpMethod.Get, NoctisServer.RemoteStateMethod, key)));
    }

    [Fact]
    public async Task Control_NeedsAPost_AndRunsTheParsedCommand()
    {
        var key = await DeviceKey();

        var viaGet = await Send(HttpMethod.Get, NoctisServer.RemoteControlMethod, key, query: "&action=next");
        Assert.Equal("failed", viaGet.GetProperty("status").GetString());
        Assert.Empty(_remote.Commands);

        var r = await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key,
            new Dictionary<string, string> { ["action"] = "seek", ["positionMs"] = "61000", ["queue"] = "5" });
        Assert.Equal("ok", r.GetProperty("status").GetString());
        Assert.Equal(new RemoteCommand(RemoteAction.Seek, PositionMs: 61_000), Assert.Single(_remote.Commands));
        Assert.True(r.GetProperty("noctisRemote").TryGetProperty("queue", out _));   // the answer is the player after it

        await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key, new Dictionary<string, string> { ["action"] = "volume", ["volume"] = "30" });
        await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key, new Dictionary<string, string> { ["action"] = "shuffle", ["enabled"] = "false" });
        await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key, new Dictionary<string, string> { ["action"] = "repeat", ["mode"] = "one" });
        await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key, new Dictionary<string, string>
            { ["action"] = "jump", ["index"] = "2", ["id"] = "tr-33333333333333333333333333333333" });
        Assert.Equal(new[]
        {
            new RemoteCommand(RemoteAction.Volume, Volume: 30),
            new RemoteCommand(RemoteAction.Shuffle, Shuffle: false),
            new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.One),
            new RemoteCommand(RemoteAction.Jump, Index: 2, TrackId: Guid.Parse("33333333-3333-3333-3333-333333333333")),
        }, _remote.Commands.Skip(1));
    }

    [Theory]
    [InlineData("volume", "volume", "101")]
    [InlineData("volume", "volume", "-1")]
    [InlineData("seek", "positionMs", "abc")]
    [InlineData("repeat", "mode", "sometimes")]
    [InlineData("shuffle", "enabled", "yes")]
    [InlineData("jump", "index", "1")]          // no id
    [InlineData("explode", "x", "1")]
    public async Task Control_BadParameters_AreError10_AndNothingRuns(string action, string name, string value)
    {
        var key = await DeviceKey();
        var r = await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key,
            new Dictionary<string, string> { ["action"] = action, [name] = value });
        Assert.Equal(10, ErrorCode(r));
        Assert.Empty(_remote.Commands);
    }

    [Fact]
    public async Task Control_TheDesktopRefusing_IsError70()
    {
        var key = await DeviceKey();
        _remote.Accept = false;
        var r = await Send(HttpMethod.Post, NoctisServer.RemoteControlMethod, key, new Dictionary<string, string>
            { ["action"] = "jump", ["index"] = "0", ["id"] = "tr-33333333333333333333333333333333" });
        Assert.Equal(70, ErrorCode(r));
    }

    // ── The desktop's adapter over its real player ──

    [AvaloniaFact]
    public async Task PlayerServerRemote_DrivesThePlayer_AsTheClicksWould()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var remote = new PlayerServerRemote(player, a => { a(); return Task.CompletedTask; });
        var tracks = Enumerable.Range(0, 4).Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = "T" + i, Artist = "A", Album = "Al", AlbumId = Guid.NewGuid(),
            FilePath = TestPaths.Primary("remote", $"t{i}.flac"), Duration = TimeSpan.FromMinutes(3),
        }).ToList();
        player.ReplaceQueueAndPlay(tracks, 0);

        var s = await remote.SnapshotAsync(2);
        Assert.Equal(tracks[0].Id, s.Track!.Id);
        Assert.Equal(new[] { "T1", "T2" }, s.UpNext.Select(t => t.Title));
        Assert.Equal(3, s.UpNextTotal);
        Assert.Empty((await remote.SnapshotAsync(0)).UpNext);

        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Pause)));
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Pause)));   // already paused: stays
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Toggle)));
        Assert.Equal(PlaybackState.Playing, player.State);

        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Volume, Volume: 140)));
        Assert.Equal(100, player.Volume);

        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.One)));
        Assert.Equal(RepeatMode.One, player.RepeatMode);
        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Repeat, Repeat: RemoteRepeat.Off)));
        Assert.Equal(RepeatMode.Off, player.RepeatMode);

        // A jump to a row that no longer holds the song the phone saw: refused, nothing changes.
        Assert.False(await remote.ControlAsync(new RemoteCommand(RemoteAction.Jump, Index: 1, TrackId: tracks[3].Id)));
        Assert.Equal(tracks[0].Id, player.CurrentTrack!.Id);
        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Jump, Index: 1, TrackId: tracks[2].Id)));
        Assert.Equal(tracks[2].Id, player.CurrentTrack!.Id);

        Assert.True(await remote.ControlAsync(new RemoteCommand(RemoteAction.Next)));
        Assert.Equal(tracks[3].Id, player.CurrentTrack!.Id);
    }

    private sealed class EmptyLibrary : IServerLibrary
    {
        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(new List<Track>(), new List<Album>(), new List<Artist>(), new List<Playlist>()));
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> trackIds, IReadOnlyList<Guid> albumIds, IReadOnlyList<Guid> artistIds, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(false);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(false);
        public Task ApplyTrackStateAsync(Guid trackId, Noctis.Services.Sync.TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, Noctis.Services.Sync.PlaylistSyncState state) => Task.CompletedTask;
    }
}
