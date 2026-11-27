using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;
using Noctis.Services;
using Noctis.Services.MediaServer;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #268: several media servers at once (Navidrome, Ampache's Subsonic API, Jellyfin, …)
/// merged into one album list, each album and song routed to the server it came from; the
/// single-server settings of older builds load unchanged; and Album Roulette spins, shows and
/// plays the servers' albums (plugin API 1.9). Two fake Subsonic servers answer through a
/// stubbed HttpMessageHandler, no network.
/// </summary>
public class MultiMediaServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "multiserver-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── Two fake Subsonic servers ──

    /// <summary>
    /// a.example.com ("Home"): Abbey Road, Kind of Blue. b.example.com ("Friend"): Abbey Road
    /// (the same album), Blue Train. Every request is recorded with its host.
    /// </summary>
    private sealed class TwoServers : HttpMessageHandler
    {
        public readonly ConcurrentQueue<Uri> Requests = new();
        public bool HomeAlbumFails;

        public static readonly (string Id, string Name, string Artist, int Year, string Genre)[] Home =
        {
            ("a-abbey", "Abbey Road", "The Beatles", 1969, "Rock"),
            ("a-kob", "Kind of Blue", "Miles Davis", 1959, "Jazz"),
        };

        public static readonly (string Id, string Name, string Artist, int Year, string Genre)[] Friend =
        {
            ("b-abbey", "abbey road", "THE BEATLES", 1969, "Rock"), // same album, other casing
            ("b-train", "Blue Train", "John Coltrane", 1958, "Jazz"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var method = uri.AbsolutePath.Split('/').Last().Replace(".view", "");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var albums = uri.Host == "a.example.com" ? Home : Friend;
            object body = method switch
            {
                "ping" => new { },
                "getAlbumList2" => new
                {
                    albumList2 = new
                    {
                        album = int.Parse(query["offset"] ?? "0") > 0
                            ? Array.Empty<object>()
                            : albums.Select(a => (object)new { id = a.Id, name = a.Name, artist = a.Artist, year = a.Year, genre = a.Genre, songCount = 2, coverArt = "art-" + a.Id }).ToArray()
                    }
                },
                "getAlbum" when uri.Host == "a.example.com" && HomeAlbumFails => new { error = new { code = 70, message = "not found" } },
                "getAlbum" => new
                {
                    album = new
                    {
                        id = query["id"],
                        song = new[]
                        {
                            new { id = query["id"] + "-1", title = "One", track = 1, duration = 100 },
                            new { id = query["id"] + "-2", title = "Two", track = 2, duration = 120 },
                        }
                    }
                },
                "search3" => new
                {
                    searchResult3 = new
                    {
                        album = albums.Where(a => a.Name.Contains(query["query"]!, StringComparison.OrdinalIgnoreCase))
                            .Select(a => (object)new { id = a.Id, name = a.Name, artist = a.Artist }).ToArray(),
                        song = albums.Where(a => a.Name.Contains(query["query"]!, StringComparison.OrdinalIgnoreCase))
                            .Select(a => (object)new { id = a.Id + "-1", title = "One", album = a.Name, artist = a.Artist, track = 1 }).ToArray(),
                    }
                },
                "getCoverArt" => null!,
                _ => new { error = new { code = 0, message = "unknown" } },
            };
            if (method == "getCoverArt")
            {
                // A PNG signature + a host marker, so the test can tell which server sent it.
                var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Encoding.ASCII.GetBytes(uri.Host)).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
            }
            var failed = method == "getAlbum" && uri.Host == "a.example.com" && HomeAlbumFails;
            var envelope = new Dictionary<string, object> { ["status"] = failed ? "failed" : "ok", ["version"] = "1.16.1" };
            foreach (var p in body.GetType().GetProperties()) envelope[p.Name] = p.GetValue(body)!;
            var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = envelope });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static SourceConnection Server(string host, string flavor, string label = "") => new()
    {
        Id = Guid.NewGuid(),
        Name = flavor,
        Label = label,
        Type = SourceType.Navidrome,
        BaseUriOrPath = $"https://{host}",
        Username = "demo",
        TokenOrPassword = "sesame",
        Enabled = true,
    };

    /// <summary>A persistence stub that really writes covers (the shared one drops them).</summary>
    private sealed class ArtworkPersistence : TestPersistenceService
    {
        public override void SaveArtwork(Guid albumId, byte[] imageData)
        {
            var path = GetArtworkPath(albumId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, imageData);
        }
    }

    private static (MediaServerService Service, TwoServers Http, SourceConnection Home, SourceConnection Friend) TwoServerService(IPersistenceService persistence)
    {
        var http = new TwoServers();
        var service = new MediaServerService(new HttpClient(http), persistence);
        var home = Server("a.example.com", "Navidrome", "Home");
        var friend = Server("b.example.com", "Ampache");
        service.SetConnections(new[] { home, friend });
        return (service, http, home, friend);
    }

    // ── Unified album list ──

    [Fact]
    public async Task TwoServers_MergeIntoOneList_SameAlbumOnceFromTheFirstServer()
    {
        using var persistence = new TestPersistenceService();
        var (service, http, home, friend) = TwoServerService(persistence);

        var all = await service.GetAllAlbumsAsync();

        // Abbey Road is on both: listed once, from the first server, the other as its alternate.
        Assert.Equal(new[] { "Abbey Road", "Blue Train", "Kind of Blue" }, all.Select(a => a.Name));
        var abbey = all[0];
        Assert.Equal(home.Id, abbey.ConnectionId);
        Assert.Equal("Home", abbey.ServerName);
        var alternate = Assert.Single(abbey.Alternates);
        Assert.Equal(friend.Id, alternate.ConnectionId);
        Assert.Equal("b-abbey", alternate.Id);
        var train = all[1];
        Assert.Equal(friend.Id, train.ConnectionId);
        Assert.Equal("Ampache (b.example.com)", train.ServerName); // no name given: preset + host
        Assert.Equal("Jazz", train.Genre);

        // Both servers were read; the list is cached (no second round of requests).
        Assert.Contains(http.Requests, u => u.Host == "a.example.com");
        Assert.Contains(http.Requests, u => u.Host == "b.example.com");
        var count = http.Requests.Count;
        Assert.Same(all, await service.GetAllAlbumsAsync());
        Assert.Equal(count, http.Requests.Count);

        // Pages of the Server section slice the merged list.
        Assert.Equal(new[] { "Blue Train", "Kind of Blue" }, (await service.GetAlbumsAsync(1, 60)).Select(a => a.Name));
        Assert.Same(train, service.FindAlbum(train.Key));
        Assert.Same(alternate, service.FindAlbum(alternate.Key));
    }

    [Fact]
    public async Task DisabledServer_LeavesTheList()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, home, friend) = TwoServerService(persistence);
        friend.Enabled = false;
        service.SetConnections(new[] { home, friend });

        Assert.Equal(new[] { home }, service.Connections);
        var all = await service.GetAllAlbumsAsync();
        Assert.All(all, a => Assert.Equal(home.Id, a.ConnectionId));
        Assert.Empty(all.Single(a => a.Name == "Abbey Road").Alternates);
    }

    [Fact]
    public void Merge_KeepsTwoEditionsOnOneServer_AndNeverMergesUnknownAlbums()
    {
        var server1 = Guid.NewGuid();
        var server2 = Guid.NewGuid();
        ServerAlbum A(string id, string name, string artist, Guid server) => new() { Id = id, Name = name, Artist = artist, ConnectionId = server };

        var merged = MediaServerService.MergeAlbums(new IReadOnlyList<ServerAlbum>[]
        {
            new[] { A("1", "Live", "Band", server1), A("2", "Live", "Band", server1), A("3", "Unknown Album", "", server1) },
            new[] { A("4", "Live", "Band", server2), A("5", "Unknown Album", "", server2) },
        });

        Assert.Equal(2, merged.Count(a => a.Name == "Live")); // two editions on one server stay two
        Assert.Equal(2, merged.Count(a => a.Name == "Unknown Album")); // no identity: never merged
        Assert.Equal("4", Assert.Single(merged.First(a => a.Id == "1").Alternates).Id);
    }

    // ── Per-server routing ──

    [Fact]
    public async Task AlbumTracks_StreamFromTheServerTheAlbumCameFrom()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, home, friend) = TwoServerService(persistence);
        var all = await service.GetAllAlbumsAsync();

        var train = await service.GetAlbumTracksAsync(all.Single(a => a.Name == "Blue Train"));
        Assert.Equal(2, train.Count);
        Assert.All(train, t =>
        {
            Assert.StartsWith("https://b.example.com/rest/stream.view?", t.FilePath);
            Assert.Equal(friend.Id.ToString("N"), t.SourceConnectionId);
            Assert.Equal(SourceType.Navidrome, t.SourceType);
        });

        var kob = await service.GetAlbumTracksAsync(all.Single(a => a.Name == "Kind of Blue"));
        Assert.All(kob, t => Assert.StartsWith("https://a.example.com/rest/stream.view?", t.FilePath));
        Assert.All(kob, t => Assert.Equal(home.Id.ToString("N"), t.SourceConnectionId));

        // Same song id on two servers never collides: track ids are per connection.
        Assert.NotEqual(MediaServerUrl.DeterministicTrackId(home.Id, "x"), MediaServerUrl.DeterministicTrackId(friend.Id, "x"));
    }

    [Fact]
    public async Task AlbumOnBothServers_FallsBackToTheOtherServer_WhenTheFirstFails()
    {
        using var persistence = new TestPersistenceService();
        var (service, http, _, friend) = TwoServerService(persistence);
        var abbey = (await service.GetAllAlbumsAsync()).Single(a => a.Name == "Abbey Road");
        http.HomeAlbumFails = true;

        var tracks = await service.GetAlbumTracksAsync(abbey);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.StartsWith("https://b.example.com/rest/stream.view?", t.FilePath));
        Assert.All(tracks, t => Assert.Equal(friend.Id.ToString("N"), t.SourceConnectionId));
    }

    [Fact]
    public async Task Artwork_ComesFromTheAlbumsOwnServer()
    {
        using var persistence = new ArtworkPersistence();
        var (service, http, _, _) = TwoServerService(persistence);
        var train = (await service.GetAllAlbumsAsync()).Single(a => a.Name == "Blue Train");

        Assert.Null(service.CachedArtworkPath(train));
        var path = await service.EnsureAlbumArtworkAsync(train);

        Assert.NotNull(path);
        Assert.EndsWith("b.example.com", Encoding.ASCII.GetString(File.ReadAllBytes(path!)));
        Assert.Contains(http.Requests, u => u.Host == "b.example.com" && u.AbsolutePath.EndsWith("getCoverArt.view"));
        Assert.DoesNotContain(http.Requests, u => u.Host == "a.example.com" && u.AbsolutePath.EndsWith("getCoverArt.view"));
        Assert.Equal(path, service.CachedArtworkPath(train));
    }

    [Fact]
    public async Task Search_AsksEveryServer_AndListsADuplicateOnce()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, home, friend) = TwoServerService(persistence);

        var result = await service.SearchAsync("abbey");

        var album = Assert.Single(result.Albums);
        Assert.Equal(home.Id, album.ConnectionId);
        Assert.Equal(friend.Id, Assert.Single(album.Alternates).ConnectionId);
        var song = Assert.Single(result.Tracks); // the same song on both servers: once, from the first
        Assert.StartsWith("https://a.example.com/", song.FilePath);

        var blue = await service.SearchAsync("blue");
        Assert.Equal(new[] { "Kind of Blue", "Blue Train" }, blue.Albums.Select(a => a.Name)); // servers' order kept
        Assert.Equal(2, blue.Tracks.Count);
    }

    [Fact]
    public async Task OneServer_StillPagesTheServerDirectly()
    {
        using var persistence = new TestPersistenceService();
        var http = new TwoServers();
        var service = new MediaServerService(new HttpClient(http), persistence);
        var home = Server("a.example.com", "Navidrome");
        service.SetActiveConnection(home);

        var page = await service.GetAlbumsAsync(0, 60);

        Assert.Equal(2, page.Count);
        var request = Assert.Single(http.Requests);
        Assert.Contains("size=60", request.Query); // one page, not the whole-server read
        Assert.All(page, a => Assert.Equal(home.Id, a.ConnectionId));
        // An album from a bare client result (no server stamped) goes to the only server.
        var tracks = await service.GetAlbumTracksAsync(new ServerAlbum { Id = "a-kob", Name = "Kind of Blue", Artist = "Miles Davis" });
        Assert.Equal(2, tracks.Count);
    }

    // ── Settings: several servers, and the migration from one ──

    /// <summary>Records what Settings hands the service.</summary>
    private sealed class RecordingMediaServerService : IMediaServerService
    {
        public IReadOnlyList<SourceConnection> Installed = Array.Empty<SourceConnection>();
        public int Changes;

        public SourceConnection? ActiveConnection => Installed.FirstOrDefault();
        public IReadOnlyList<SourceConnection> Connections => Installed;
        public bool IsConfigured => Installed.Count > 0;
        public event EventHandler? ActiveConnectionChanged { add { } remove { } }

        public Task<(MediaServerConnectResult result, SourceConnection connection)> ConnectAsync(
            SourceType type, string url, string username, string password, Guid? existingId, CancellationToken ct = default)
            => Task.FromResult((MediaServerConnectResult.Ok("Connected"), new SourceConnection
            {
                Id = existingId ?? Guid.NewGuid(),
                Name = type == SourceType.Jellyfin ? "Jellyfin" : "Subsonic",
                Type = type,
                BaseUriOrPath = url.Trim().TrimEnd('/'),
                Username = username,
                TokenOrPassword = password,
                Enabled = true,
            }));

        public void SetActiveConnection(SourceConnection? connection)
            => SetConnections(connection is null ? Array.Empty<SourceConnection>() : new[] { connection });

        public void SetConnections(IEnumerable<SourceConnection> connections)
        {
            Installed = connections.Where(c => c.Enabled).ToArray();
            Changes++;
        }

        public Task<IReadOnlyList<ServerAlbum>> GetAlbumsAsync(int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Track>> GetAlbumTracksAsync(ServerAlbum album, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ServerSearchResult> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> EnsureAlbumArtworkAsync(ServerAlbum album, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private SettingsViewModel NewSettings(RecordingMediaServerService server)
        => new(new PersistenceService(_root), new FakeLibraryService(), new NoOpHistory(), server);

    private sealed class NoOpHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task SingleServerSettings_FromAnOlderBuild_LoadAsTheOnlyServer_AndKeepTheirCredentials()
    {
        // What 1.7.2 saved: one Subsonic connection (flavor in Name, no Label), next to an SMB source.
        var seeded = new AppSettings();
        var oldId = Guid.NewGuid();
        seeded.SourceConnections.Add(new SourceConnection { Type = SourceType.Smb, Name = "NAS", BaseUriOrPath = @"\\nas\music" });
        seeded.SourceConnections.Add(new SourceConnection
        {
            Id = oldId, Name = "Gonic", Type = SourceType.Navidrome, BaseUriOrPath = "https://music.example.com",
            Username = "demo", TokenOrPassword = "sesame", ApiVersion = "1.15.0", AuthMode = SubsonicAuthMode.Password,
        });
        await new PersistenceService(_root).SaveSettingsAsync(seeded);

        var server = new RecordingMediaServerService();
        var vm = NewSettings(server);
        await vm.LoadAsync();

        var entry = Assert.Single(vm.MediaServers);
        Assert.Equal("Gonic", entry.Flavor);
        Assert.Equal("Gonic (music.example.com)", entry.DisplayName);
        Assert.True(entry.IsEnabled);
        Assert.True(vm.IsMediaServerConnected);
        Assert.False(vm.IsMediaServerFormOpen); // the list shows, the form waits for "Add server"
        Assert.Equal(oldId, Assert.Single(server.Installed).Id);

        // An unrelated save keeps it exactly (and the SMB source untouched).
        vm.IncludePrereleaseUpdates = true;
        await vm.SaveAsync();
        var stored = (await new PersistenceService(_root).LoadSettingsAsync()).SourceConnections;
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, c => c.Type == SourceType.Smb);
        var kept = stored.Single(c => c.Type == SourceType.Navidrome);
        Assert.Equal((oldId, "Gonic", "sesame", "1.15.0", SubsonicAuthMode.Password, ""),
            (kept.Id, kept.Name, kept.TokenOrPassword, kept.ApiVersion, kept.AuthMode, kept.Label));
    }

    [AvaloniaFact]
    public async Task AddSecondServer_KeepsTheFirst_ThenDisableAndRemove_Persist()
    {
        var server = new RecordingMediaServerService();
        var vm = NewSettings(server);
        await vm.LoadAsync();
        Assert.True(vm.IsMediaServerFormOpen); // no server yet: the form shows

        vm.MediaServerType = "Navidrome";
        vm.MediaServerUrl = "https://a.example.com";
        vm.MediaServerUsername = "demo";
        vm.MediaServerPassword = "pw1";
        vm.MediaServerName = "Home";
        await vm.ConnectMediaServerCommand.ExecuteAsync(null);
        Assert.Single(vm.MediaServers);
        Assert.False(vm.IsMediaServerFormOpen);
        Assert.Equal("", vm.MediaServerPassword); // never kept in a bound field
        Assert.Equal("", vm.MediaServerUrl);      // the form starts empty for the next server

        vm.AddMediaServerCommand.Execute(null);
        Assert.True(vm.IsMediaServerFormOpen);
        vm.MediaServerType = "Ampache";
        Assert.True(vm.IsAmpacheSelected);
        vm.MediaServerUrl = "https://b.example.com";
        vm.MediaServerUsername = "demo";
        vm.MediaServerPassword = "pw2";
        await vm.ConnectMediaServerCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Home", "Ampache (b.example.com)" }, vm.MediaServers.Select(e => e.DisplayName));
        Assert.Equal(2, server.Installed.Count);
        Assert.False(vm.IsAddingMediaServer);

        // Reconnecting the same server (type, address, account) updates it in place, same id.
        var firstId = vm.MediaServers[0].Connection.Id;
        vm.AddMediaServerCommand.Execute(null);
        vm.MediaServerType = "Navidrome";
        vm.MediaServerUrl = "https://a.example.com/";
        vm.MediaServerUsername = "demo";
        vm.MediaServerPassword = "pw1-new";
        await vm.ConnectMediaServerCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.MediaServers.Count);
        Assert.Equal(firstId, vm.MediaServers[0].Connection.Id);
        Assert.Equal("pw1-new", vm.MediaServers[0].Connection.TokenOrPassword);

        // Both survive a relaunch, in order.
        var reloaded = NewSettings(new RecordingMediaServerService());
        await reloaded.LoadAsync();
        Assert.Equal(new[] { "Navidrome", "Ampache" }, reloaded.MediaServers.Select(e => e.Flavor));

        // Off: out of the service, kept in Settings.
        vm.MediaServers[1].IsEnabled = false;
        Assert.Equal(firstId, Assert.Single(server.Installed).Id);
        await vm.SaveAsync();
        var afterToggle = NewSettings(new RecordingMediaServerService());
        await afterToggle.LoadAsync();
        Assert.False(afterToggle.MediaServers[1].IsEnabled);

        // Remove: gone from the list, the service and the file.
        await vm.RemoveMediaServerCommand.ExecuteAsync(vm.MediaServers[0]);
        Assert.Equal("Ampache", Assert.Single(vm.MediaServers).Flavor);
        Assert.Empty(server.Installed); // the one left is off
        var afterRemove = NewSettings(new RecordingMediaServerService());
        await afterRemove.LoadAsync();
        Assert.Equal("Ampache", Assert.Single(afterRemove.MediaServers).Flavor);
    }

    [Fact]
    public void AmpachePreset_SpeaksSubsonic()
        => Assert.Equal(SourceType.Navidrome, SettingsViewModel.MediaServerOptionToSourceType("Ampache"));

    // ── Album Roulette (plugin API 1.9) ──

    [AvaloniaFact]
    public async Task PluginApi_ListsAndPlaysServerAlbums_FromTheRightServer()
    {
        using var persistence = new ArtworkPersistence();
        var (service, _, _, friend) = TwoServerService(persistence);
        using var box = new PluginSandbox();
        const string id = "dev.test.serveralbums";
        box.Approve((id, new[] { "library.read", "playback.control" }));
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var host = box.NewHost(player, library, appVersion: "1.7.3");
        host.MediaServer = service;
        var script = new ScriptedPlugin();
        box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.9", minAppVersion: "1.6.9",
            permissions: new[] { "library.read", "playback.control" }));

        var albums = await script.Host!.Library.GetServerAlbumsAsync();
        Assert.Equal(new[] { "Abbey Road", "Blue Train", "Kind of Blue" }, albums.Select(a => a.Title));
        var train = albums.Single(a => a.Title == "Blue Train");
        Assert.Equal(("John Coltrane", 1958, "Jazz", "Ampache (b.example.com)"), (train.Artist, train.Year, train.Genre, train.ServerName));

        var art = await script.Host.Library.GetServerAlbumArtworkAsync(train.Id);
        Assert.NotNull(art);
        Assert.True(File.Exists(art));

        Assert.True(await Task.Run(() => script.Host.Playback.PlayServerAlbumAsync(train.Id)));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(player.CurrentTrack);
        Assert.StartsWith("https://b.example.com/rest/stream.view?", player.CurrentTrack!.FilePath);
        Assert.Equal(friend.Id.ToString("N"), player.CurrentTrack.SourceConnectionId);

        Assert.True(await Task.Run(() => script.Host.Playback.QueueServerAlbumAsync(albums.Single(a => a.Title == "Kind of Blue").Id, playNext: false)));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(player.UpNext, t => t.FilePath.StartsWith("https://a.example.com/", StringComparison.Ordinal));

        Assert.False(await script.Host.Playback.PlayServerAlbumAsync("unknown:album"));
    }

    [Fact]
    public async Task OlderHosts_SayApi19IsMissing()
    {
        ILibraryReader reader = new OldReader();
        Assert.Contains("1.9", (await Assert.ThrowsAsync<NotSupportedException>(() => reader.GetServerAlbumsAsync())).Message);
        Assert.True(PluginApi.Minor >= 9); // 1.10 added ShowTrack on top
    }

    private sealed class OldReader : ILibraryReader
    {
        public int TrackCount => 0;
        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50) => Array.Empty<TrackInfo>();
    }

    [AvaloniaFact]
    public async Task Roulette_RealHost_ReadsServerAlbums_ThroughTheApi()
    {
        // Root cause of the report: the plugin only ever read library tracks (Library.GetAll),
        // and server albums are never library tracks. Through API 1.9 the real host sees them.
        using var persistence = new TestPersistenceService();
        var (service, _, _, _) = TwoServerService(persistence);
        using var box = new PluginSandbox();
        const string id = "dev.noctis.plugins.albumroulette";
        box.Approve((id, new[] { "library.read", "playback.control" }));
        var host = box.NewHost(null, new FakeLibraryService(), appVersion: "1.7.3");
        host.MediaServer = service;
        var script = new ScriptedPlugin();
        box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.5", minAppVersion: "1.6.9",
            permissions: new[] { "library.read", "playback.control" }));

        using var roulette = new PluginRouletteHost(script.Host!);
        Assert.Empty(roulette.LoadTracks()); // the library is empty...
        var remote = await Task.Run(roulette.LoadServerAlbums);
        Assert.Equal(3, remote.Count); // ...but the servers' albums are there
        Assert.Contains(remote, r => r.Title == "Blue Train" && r.ServerName == "Ampache (b.example.com)");
    }

    private sealed class ServerRouletteHost : IRouletteHost
    {
        public List<TrackInfo> Tracks = new();
        public List<RemoteAlbum> Remote = new();
        public readonly List<(string Id, bool Shuffle)> PlayedServer = new();
        public readonly List<(string Id, bool PlayNext)> QueuedServer = new();
        public readonly List<string> ArtAsked = new();
        public string? ArtPath;

        public IReadOnlyList<TrackInfo> LoadTracks() => Tracks;
        public IReadOnlyList<RemoteAlbum> LoadServerAlbums() => Remote;
        public Task<string?> ServerAlbumArtworkAsync(string serverAlbumId)
        {
            lock (ArtAsked) ArtAsked.Add(serverAlbumId);
            return Task.FromResult(ArtPath);
        }
        public void PlayServerAlbum(string serverAlbumId, bool shuffle) => PlayedServer.Add((serverAlbumId, shuffle));
        public void QueueServerAlbum(string serverAlbumId, bool playNext) => QueuedServer.Add((serverAlbumId, playNext));
        public IReadOnlyList<string> LyricLines(string trackId) => Array.Empty<string>();
        public void PlayTracks(IReadOnlyList<string> trackIds) => throw new InvalidOperationException("a server album has no library tracks");
        public void QueueTracks(IReadOnlyList<string> trackIds, bool playNext) => throw new InvalidOperationException("a server album has no library tracks");
        public bool ShowAlbum(string albumId) => false;
        public void Notify(string message) { }
        public void Log(string message) { }
        public bool AlbumOfTheDayEnabled => false;
        public int DailyHour => 9;
        public bool SkipCompilations => true;
        public bool ReducedMotion => true;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public DailyState? LoadDaily() => null;
        public void SaveDaily(DailyState state) { }
    }

    [Fact]
    public void Catalog_AddsServerAlbums_ButTheLibraryCopyWins()
    {
        var library = AlbumCatalog.Group(new[]
        {
            AlbumRoulettePluginTests.T("Come Together", "Abbey Road", "The Beatles", 1969, albumArtist: "The Beatles"),
        });
        var all = AlbumCatalog.WithServerAlbums(library, new[]
        {
            new RemoteAlbum("h:1", "abbey road", "the beatles", 1969, "Rock", "Home", null), // also in the library
            new RemoteAlbum("h:2", "Blue Train", "John Coltrane", 1958, "Jazz", "Home", null),
        });

        Assert.Equal(2, all.Count);
        Assert.False(all.Single(a => a.Title == "Abbey Road").IsServer);
        var train = all.Single(a => a.Title == "Blue Train");
        Assert.True(train.IsServer);
        Assert.Equal(("h:2", "Jazz", 1950, ReleaseKind.Album), (train.ServerAlbumId, train.Genre, train.Decade, train.Kind));
        Assert.Empty(train.TrackIds);
    }

    [AvaloniaFact]
    public async Task Roulette_SpinsServerAlbums_ShowsTheirServer_AndPlaysThemThroughTheServer()
    {
        var cover = Path.Combine(_root, "cover.png");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(cover, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var host = new ServerRouletteHost
        {
            Remote = { new RemoteAlbum("srv:train", "Blue Train", "John Coltrane", 1958, "Jazz", "Friend's Ampache", null) },
            ArtPath = cover,
        };
        var view = new RouletteView(host, new Random(1), () => DateTimeOffset.Now);
        view.Reel.ManualFrames = true;

        await view.LoadAsync();

        var album = Assert.Single(view.Matches);
        Assert.True(album.IsServer);
        Assert.Equal("1 album", view.CountText);
        // Its cover is asked of Noctis when it reaches the reel, then drawn from the file.
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("srv:train", host.ArtAsked);
        Assert.Equal(cover, album.ArtworkPath);

        view.ShowOnStage(album);
        Assert.Equal("1958 · Friend's Ampache", view.SubtitleText);
        Click(view, "Play");
        Click(view, "Shuffle");
        Click(view, "Play next");
        Click(view, "Add to queue");
        Assert.Equal(new[] { ("srv:train", false), ("srv:train", true) }, host.PlayedServer);
        Assert.Equal(new[] { ("srv:train", true), ("srv:train", false) }, host.QueuedServer);
        Assert.False(view.OpenAlbumEnabled); // no library album page to open
        view.Release();
    }

    private static void Click(Control root, string name)
        => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>()
            .First(c => Avalonia.Automation.AutomationProperties.GetName(c) == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
