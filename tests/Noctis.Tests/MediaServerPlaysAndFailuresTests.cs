using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Services;
using Noctis.Services.MediaServer;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #268, the last two gaps:
/// 1. Plays of server songs inside Noctis did not count for Album Roulette's "Never played" /
///    "Not played in 6 months": the play log kept only the song's id, title and artist. It now
///    keeps the server album too, and the album's plays handed to plugins are the server's plus
///    Noctis's (Noctis never reports its plays to these servers, so nothing counts twice).
/// 2. A server that failed to answer left no trace in the Server section. It is now named under
///    the "Connected to" line with a Retry that asks only that server again.
/// Fake Subsonic servers answer through a stubbed HttpMessageHandler, no network.
/// </summary>
public class MediaServerPlaysAndFailuresTests
{
    public enum Mode { Ok, Throw, Http500, WrongLogin }

    private sealed record FakeAlbum(string Id, string Name, string Artist, int? PlayCount = null, string? Played = null);

    private sealed class FakeServers : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, FakeAlbum[]> Albums = new();
        public readonly ConcurrentDictionary<string, Mode> Modes = new();
        public readonly ConcurrentQueue<Uri> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var mode = Modes.GetValueOrDefault(uri.Host, Mode.Ok);
            if (mode == Mode.Throw) throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable);
            if (mode == Mode.Http500) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var method = uri.AbsolutePath.Split('/').Last().Replace(".view", "");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var envelope = new Dictionary<string, object?> { ["status"] = "ok", ["version"] = "1.16.1", ["type"] = "navidrome", ["openSubsonic"] = true };
            if (mode == Mode.WrongLogin)
            {
                envelope["status"] = "failed";
                envelope["error"] = new { code = 40, message = "Wrong username or password" };
            }
            else if (method == "getAlbumList2")
            {
                var offset = int.Parse(query["offset"] ?? "0");
                var size = int.Parse(query["size"] ?? "500");
                var page = Albums.GetValueOrDefault(uri.Host, Array.Empty<FakeAlbum>()).Skip(offset).Take(size);
                envelope["albumList2"] = new
                {
                    album = page.Select(a =>
                    {
                        var json = new Dictionary<string, object?> { ["id"] = a.Id, ["name"] = a.Name, ["artist"] = a.Artist, ["songCount"] = 1 };
                        if (a.PlayCount is { } c) json["playCount"] = c;
                        if (a.Played is { } p) json["played"] = p;
                        return json;
                    }).ToArray()
                };
            }
            else if (method == "getAlbum")
            {
                var album = Albums.GetValueOrDefault(uri.Host, Array.Empty<FakeAlbum>()).Single(a => a.Id == query["id"]);
                // No albumId on the songs: the album they were listed under names it.
                envelope["album"] = new { id = album.Id, name = album.Name, artist = album.Artist, song = new[] { new { id = album.Id + "-s1", title = "Song", track = 1 } } };
            }
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = envelope });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static SourceConnection Server(string host, string label) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Navidrome",
        Label = label,
        Type = SourceType.Navidrome,
        BaseUriOrPath = $"https://{host}",
        Username = "demo",
        TokenOrPassword = "sesame",
        Enabled = true,
    };

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
            if (Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.RunJobs();
        }
    }

    private static string TempLog() => Path.Combine(Path.GetTempPath(), "noctis-268c-" + Guid.NewGuid().ToString("N"), "play_history.json");

    // ── Gap 1: Noctis's own plays of server songs ──

    [Fact]
    public async Task PlayingAServerSong_LogsItsAlbum_AndTheLogReadsBackWithIt()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Albums["nd.example.com"] = new[] { new FakeAlbum("al-1", "Abbey Road", "The Beatles") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        var nd = Server("nd.example.com", "Home");
        service.SetConnections(new[] { nd });
        var album = Assert.Single(await service.GetAlbumsAsync(0, 60));
        var track = Assert.Single(await service.GetAlbumTracksAsync(album));
        Assert.Equal("al-1", track.SourceAlbumId);

        var path = TempLog();
        try
        {
            var log = new PlayHistoryService(path);
            log.RecordPlay(track);
            log.RecordPlay(new Track { Id = Guid.NewGuid(), Title = "Local", Artist = "Me", Album = "Mine", FilePath = @"C:\m.flac" });
            await log.FlushAsync();

            var events = new PlayHistoryService(path).Events;
            Assert.Equal(2, events.Count);
            Assert.Equal(album.Key, events[0].ServerAlbumKey);
            Assert.Equal(MediaServerService.AlbumIdentity(album), events[0].ServerAlbumIdentity);
            Assert.Null(events[1].ServerAlbumKey); // a library play keeps no server album
            Assert.Null(events[1].ServerAlbumIdentity);
            Assert.DoesNotContain("ServerAlbum", File.ReadAllText(path).Split('}')[1]); // nor writes the fields
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void AnOldPlayLog_WithoutServerAlbums_StillLoads()
    {
        var path = TempLog();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "[{\"TrackId\":\"" + Guid.NewGuid() + "\",\"Title\":\"Old\",\"Artist\":\"A\",\"PlayedAtUtc\":\"2025-01-02T03:04:05Z\",\"Skipped\":false}]");
            var e = Assert.Single(new PlayHistoryService(path).Events);
            Assert.Equal("Old", e.Title);
            Assert.Null(e.ServerAlbumKey);
            Assert.Null(e.ServerAlbumIdentity);
            // An old log has no server plays to add: an album's plays stay the server's.
            var album = new ServerAlbum { Id = "1", Name = "Old", Artist = "A" };
            Assert.Equal((0, (DateTimeOffset?)null), NoctisAlbumPlays.Build(new[] { e }, new[] { album }).For(album));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void NoctisPlays_AddToTheServersCount_AndTheLatestDateWins()
    {
        var server = ((int?)3, (DateTimeOffset?)new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var noctisDate = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(((int?)5, (DateTimeOffset?)noctisDate), MediaServerService.WithNoctisPlays(server, (2, noctisDate)));
        // No Noctis plays: the server's answer as it was, unknown included.
        Assert.Equal(server, MediaServerService.WithNoctisPlays(server, (0, null)));
        Assert.Equal(((int?)null, (DateTimeOffset?)null), MediaServerService.WithNoctisPlays((null, null), (0, null)));
        // The server says nothing: Noctis's plays make it played, with their date.
        Assert.Equal(((int?)1, (DateTimeOffset?)noctisDate), MediaServerService.WithNoctisPlays((null, null), (1, noctisDate)));
    }

    [Fact]
    public void NoctisPlays_CountForTheMergedAlbum_ByKeyOrBySameArtistAndTitle()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var main = new ServerAlbum { Id = "x", Name = "Abbey Road", Artist = "The Beatles", ConnectionId = a };
        var copy = new ServerAlbum { Id = "y", Name = "Abbey Road", Artist = "The Beatles", ConnectionId = b };
        var other = new ServerAlbum { Id = "z", Name = "Blue Train", Artist = "John Coltrane", ConnectionId = a };
        var merged = MediaServerService.MergeAlbums(new IReadOnlyList<ServerAlbum>[] { new[] { main, other }, new[] { copy } });
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = new[]
        {
            new PlayHistoryEvent { PlayedAtUtc = t1, ServerAlbumKey = main.Key, ServerAlbumIdentity = MediaServerService.AlbumIdentity(main) },
            // Played from the second server's copy: still this album.
            new PlayHistoryEvent { PlayedAtUtc = t1.AddDays(1), ServerAlbumKey = copy.Key, ServerAlbumIdentity = MediaServerService.AlbumIdentity(copy) },
            // A key the server no longer lists (rescanned): matched by album artist + title.
            new PlayHistoryEvent { PlayedAtUtc = t1.AddDays(2), ServerAlbumKey = $"{a:N}:gone", ServerAlbumIdentity = MediaServerService.AlbumIdentity(main) },
            new PlayHistoryEvent { PlayedAtUtc = t1.AddDays(3), Title = "a library play" },
        };

        var plays = NoctisAlbumPlays.Build(events, merged);

        var abbey = merged.Single(m => m.Name == "Abbey Road");
        Assert.Equal((3, (DateTimeOffset?)new DateTimeOffset(t1.AddDays(2))), plays.For(abbey));
        Assert.Equal((0, (DateTimeOffset?)null), plays.For(merged.Single(m => m.Name == "Blue Train")));
    }

    [AvaloniaFact]
    public async Task Roulette_SeesNoctisPlays_RecentAndOld_CombinedWithTheServers()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        // Navidrome counts plays: albums without a count are at 0 there (Noctis never reported its plays).
        http.Albums["nd.example.com"] = new[]
        {
            new FakeAlbum("1", "Recently In Noctis", "A"),
            new FakeAlbum("2", "Long Ago In Noctis", "B"),
            new FakeAlbum("3", "Server And Noctis", "C", 2, "2024-01-01T00:00:00Z"),
            new FakeAlbum("4", "Never Anywhere", "D", 0),
        };
        var service = new MediaServerService(new HttpClient(http), persistence);
        var nd = Server("nd.example.com", "Home");
        service.SetConnections(new[] { nd, Server("other.example.com", "Other") });
        var now = DateTimeOffset.UtcNow;
        string Key(string id) => $"{nd.Id:N}:{id}";
        var history = new List<PlayHistoryEvent>
        {
            new() { PlayedAtUtc = now.AddYears(-1).UtcDateTime, ServerAlbumKey = Key("2") },
            new() { PlayedAtUtc = now.AddMonths(-8).UtcDateTime, ServerAlbumKey = Key("3") },
            new() { PlayedAtUtc = now.AddDays(-3).UtcDateTime, ServerAlbumKey = Key("1") },
        };

        using var box = new PluginSandbox();
        const string id = "dev.noctis.plugins.albumroulette";
        box.Approve((id, new[] { "library.read", "playback.control" }));
        var host = box.NewHost(null, new FakeLibraryService(), appVersion: "1.7.3");
        host.MediaServer = service;
        host.PlayHistorySource = () => history;
        var script = new ScriptedPlugin();
        box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.5", minAppVersion: "1.6.9",
            permissions: new[] { "library.read", "playback.control" }));

        var infos = await script.Host!.Library.GetServerAlbumsAsync();
        var both = infos.Single(i => i.Title == "Server And Noctis");
        Assert.Equal(3, both.PlayCount); // 2 on the server + 1 in Noctis
        Assert.Equal(now.AddMonths(-8).UtcDateTime, both.LastPlayed!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(1, infos.Single(i => i.Title == "Recently In Noctis").PlayCount);
        Assert.Equal(0, infos.Single(i => i.Title == "Never Anywhere").PlayCount);

        using var roulette = new PluginRouletteHost(script.Host);
        var all = AlbumCatalog.WithServerAlbums(Array.Empty<RouletteAlbum>(), await Task.Run(roulette.LoadServerAlbums));
        var never = new RouletteFilter { Played = PlayedFilter.NeverPlayed }.Apply(all, now);
        Assert.Equal(new[] { "Never Anywhere" }, never.Select(a => a.Title));
        var notLately = new RouletteFilter { Played = PlayedFilter.NotIn6Months }.Apply(all, now);
        Assert.Equal(new[] { "Long Ago In Noctis", "Never Anywhere", "Server And Noctis" }, notLately.Select(a => a.Title).Order());
    }

    // ── Gap 2: a server that did not answer ──

    private static (MediaServerService Service, FakeServers Http, SourceConnection A, SourceConnection B) TwoServers(TestPersistenceService persistence, Mode aMode)
    {
        var http = new FakeServers();
        http.Modes["a.example.com"] = aMode;
        http.Albums["a.example.com"] = new[] { new FakeAlbum("a-abbey", "Abbey Road", "The Beatles"), new FakeAlbum("a-kob", "Kind of Blue", "Miles Davis") };
        http.Albums["b.example.com"] = new[] { new FakeAlbum("b-abbey", "Abbey Road", "The Beatles"), new FakeAlbum("b-train", "Blue Train", "John Coltrane") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        var a = Server("a.example.com", "Server A");
        var b = Server("b.example.com", "Server B");
        service.SetConnections(new[] { a, b });
        return (service, http, a, b);
    }

    private static ServerViewModel NewSection(IMediaServerService service) =>
        new(service, new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService()));

    [AvaloniaTheory]
    [InlineData(Mode.Throw, "Server.ServerNoAnswer")]
    [InlineData(Mode.Http500, "Server.ServerFailed")]
    [InlineData(Mode.WrongLogin, "Server.ServerSignInFailed")]
    public async Task AServerThatFails_IsNamed_AndRetryBringsItsAlbumsIn(Mode mode, string textKey)
    {
        using var persistence = new TestPersistenceService();
        var (service, http, a, b) = TwoServers(persistence, mode);
        var vm = NewSection(service);

        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading && vm.HasServerFailures, "the servers");

        Assert.Equal(new[] { "Abbey Road", "Blue Train" }, vm.Albums.Select(t => t.Name)); // B's still show
        var failure = Assert.Single(vm.ServerFailures);
        Assert.Equal(a.Id, failure.ConnectionId);
        Assert.Equal(Loc.T(textKey, "Server A"), failure.Text);
        Assert.Contains("Server A", failure.Text);
        Assert.False(string.IsNullOrWhiteSpace(failure.Detail));

        // A answers again; Retry asks only A.
        http.Modes["a.example.com"] = Mode.Ok;
        var bRequests = http.Requests.Count(r => r.Host == "b.example.com");
        await vm.RetryServerCommand.ExecuteAsync(failure);
        await Until(() => !vm.HasServerFailures, "the notice to clear");

        Assert.Equal(new[] { "Abbey Road", "Blue Train", "Kind of Blue" }, vm.Albums.Select(t => t.Name));
        Assert.Equal(a.Id, vm.Albums[0].Album.ConnectionId); // the shared album now comes from A, the first server
        Assert.Equal(b.Id, Assert.Single(vm.Albums[0].Album.Alternates).ConnectionId);
        Assert.Equal(bRequests, http.Requests.Count(r => r.Host == "b.example.com"));
        Assert.Empty(service.Failures);
        // The cached list has A's albums too (Album Roulette, the next visit).
        Assert.Equal(3, (await service.GetAllAlbumsAsync()).Count);
        Assert.Equal(bRequests, http.Requests.Count(r => r.Host == "b.example.com"));
    }

    [AvaloniaFact]
    public async Task RetryWhileTheServerStillFails_KeepsTheNotice_AndTheOthersAlbums()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, a, _) = TwoServers(persistence, Mode.Throw);
        var vm = NewSection(service);
        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading && vm.HasServerFailures, "the servers");

        await vm.RetryServerCommand.ExecuteAsync(vm.ServerFailures[0]);
        if (Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.RunJobs();

        Assert.Equal(a.Id, Assert.Single(vm.ServerFailures).ConnectionId);
        Assert.False(vm.ServerFailures[0].IsRetrying);
        Assert.Equal(new[] { "Abbey Road", "Blue Train" }, vm.Albums.Select(t => t.Name));
    }

    [AvaloniaFact]
    public async Task EveryServerAnswers_NoNotice()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, _, _) = TwoServers(persistence, Mode.Ok);
        var vm = NewSection(service);

        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading, "the servers");
        if (Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, vm.Albums.Count);
        Assert.False(vm.HasServerFailures);
        Assert.Empty(vm.ServerFailures);
        Assert.Empty(service.Failures);
    }

    [AvaloniaFact]
    public async Task OneServerOnly_ThatFails_IsNamedToo_AndRetryReloads()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Modes["a.example.com"] = Mode.Throw;
        http.Albums["a.example.com"] = new[] { new FakeAlbum("a-kob", "Kind of Blue", "Miles Davis") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetConnections(new[] { Server("a.example.com", "Server A") });
        var vm = NewSection(service);

        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading && vm.HasServerFailures, "the server");
        Assert.Equal(Loc.T("Server.ServerNoAnswer", "Server A"), Assert.Single(vm.ServerFailures).Text);

        http.Modes["a.example.com"] = Mode.Ok;
        await vm.RetryServerCommand.ExecuteAsync(vm.ServerFailures[0]);
        await Until(() => !vm.HasServerFailures, "the notice to clear");
        Assert.Equal("Kind of Blue", Assert.Single(vm.Albums).Name);
    }

    [Fact]
    public async Task Jellyfin_ARejectedToken_IsASignInFailure()
    {
        using var persistence = new TestPersistenceService();
        var handler = new StatusHandler(HttpStatusCode.Unauthorized);
        var client = new JellyfinClient(new HttpClient(handler));
        var connection = new SourceConnection { Id = Guid.NewGuid(), Type = SourceType.Jellyfin, BaseUriOrPath = "https://jf.example.com", UserId = "u", TokenOrPassword = "t", Enabled = true };

        var page = await ((IMediaServerClient)client).GetAlbumPageAsync(connection, 0, 60);

        Assert.Empty(page.Albums);
        Assert.Equal(MediaServerError.AuthFailed, page.Error!.Error);
        Assert.Empty(await client.GetAlbumsAsync(connection, 0, 60)); // the old call still folds it into an empty page
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }
}
