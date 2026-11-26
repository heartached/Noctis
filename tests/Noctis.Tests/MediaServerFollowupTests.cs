using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Services.MediaServer;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Follow-ups to GitHub #268 (several media servers):
/// 1. Album Roulette's "Never played" / "Not played in 6 months" matched every server album:
///    the app had no play history for them and the reel took that as never played. Now the
///    servers' own play counts and dates (Navidrome playCount/played, Jellyfin UserData) reach
///    the reel, and an album whose server does not say stays off those two chips.
/// 2. With several servers the first visit to the Server section showed nothing until every
///    server had sent its whole album list. Now the servers are read in parallel and the grid
///    fills in as each page arrives; a slow or failing server holds back only its own albums.
/// Fake Subsonic servers answer through a stubbed HttpMessageHandler, no network.
/// </summary>
public class MediaServerFollowupTests
{
    // ── Fake Subsonic servers: per host albums, an optional gate, an optional failure ──

    private sealed record FakeAlbum(string Id, string Name, string Artist, int? PlayCount = null, string? Played = null);

    private sealed class FakeServers : HttpMessageHandler
    {
        public readonly Dictionary<string, FakeAlbum[]> Albums = new();
        public readonly Dictionary<string, TaskCompletionSource> Gates = new();
        public readonly HashSet<string> Failing = new();
        public readonly HashSet<string> Navidrome = new();
        public readonly ConcurrentQueue<Uri> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            if (Gates.TryGetValue(uri.Host, out var gate)) await gate.Task.WaitAsync(cancellationToken);
            if (Failing.Contains(uri.Host)) throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable);

            var method = uri.AbsolutePath.Split('/').Last().Replace(".view", "");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var envelope = new Dictionary<string, object?> { ["status"] = "ok", ["version"] = "1.16.1" };
            if (Navidrome.Contains(uri.Host))
            {
                envelope["type"] = "navidrome";
                envelope["openSubsonic"] = true;
            }
            if (method == "getAlbumList2")
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
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = envelope });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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

    // ── Bug 1: the servers' play info ──

    [Fact]
    public async Task Navidrome_PlayCountAndDate_MapOntoTheAlbum_AndAMissingCountMeansZero()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Navidrome.Add("nd.example.com");
        http.Albums["nd.example.com"] = new[]
        {
            new FakeAlbum("1", "Abbey Road", "The Beatles", 7, "2024-05-01T12:30:00Z"),
            new FakeAlbum("2", "Blue Train", "John Coltrane"), // Navidrome leaves playCount out at 0
        };
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetActiveConnection(Server("nd.example.com", "Home"));

        var albums = await service.GetAlbumsAsync(0, 60);

        var abbey = albums.Single(a => a.Name == "Abbey Road");
        Assert.Equal(7, abbey.PlayCount);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.Zero), abbey.LastPlayed);
        var train = albums.Single(a => a.Name == "Blue Train");
        Assert.Equal(0, train.PlayCount);
        Assert.Null(train.LastPlayed);
        Assert.True(train.HasPlayInfo);
    }

    [Fact]
    public async Task PlainSubsonic_WithoutPlayCounts_LeavesThemUnknown_ButOneCountOnThePageMakesTheRestZero()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Albums["plain.example.com"] = new[] { new FakeAlbum("1", "Abbey Road", "The Beatles"), new FakeAlbum("2", "Blue Train", "John Coltrane") };
        http.Albums["counts.example.com"] = new[] { new FakeAlbum("1", "Abbey Road", "The Beatles", 2), new FakeAlbum("2", "Blue Train", "John Coltrane") };
        var service = new MediaServerService(new HttpClient(http), persistence);

        service.SetActiveConnection(Server("plain.example.com", "Plain"));
        var plain = await service.GetAlbumsAsync(0, 60);
        Assert.All(plain, a => Assert.False(a.HasPlayInfo));

        service.SetActiveConnection(Server("counts.example.com", "Counts"));
        var counted = await service.GetAlbumsAsync(0, 60);
        Assert.Equal(2, counted.Single(a => a.Name == "Abbey Road").PlayCount);
        Assert.Equal(0, counted.Single(a => a.Name == "Blue Train").PlayCount);
    }

    [Theory]
    [InlineData("""{ "Id": "a", "ChildCount": 10, "UserData": { "PlayCount": 3, "LastPlayedDate": "2025-01-02T03:04:05.0000000Z", "Played": false } }""", 3, "2025-01-02T03:04:05Z")]
    [InlineData("""{ "Id": "a", "ChildCount": 10, "UserData": { "PlayCount": 0, "UnplayedItemCount": 10, "Played": false } }""", 0, null)]
    [InlineData("""{ "Id": "a", "ChildCount": 10, "UserData": { "PlayCount": 0, "UnplayedItemCount": 4, "Played": false } }""", 1, null)]
    [InlineData("""{ "Id": "a", "ChildCount": 10, "UserData": { "PlayCount": 0, "Played": true } }""", 1, null)]
    [InlineData("""{ "Id": "a", "ChildCount": 10, "UserData": { "PlayCount": 0, "Played": false } }""", null, null)]
    [InlineData("""{ "Id": "a", "ChildCount": 10 }""", null, null)]
    public void Jellyfin_UserData_MapsToPlayInfo(string json, int? count, string? date)
    {
        using var doc = JsonDocument.Parse(json);
        var (playCount, lastPlayed) = JellyfinClient.ReadPlayInfo(doc.RootElement);
        Assert.Equal(count, playCount);
        Assert.Equal(date is null ? null : DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture), lastPlayed);
    }

    [Fact]
    public void AnAlbumOnTwoServers_AddsUpItsPlays_AndAServerThatDoesNotSayKeepsNeverUnknown()
    {
        static ServerAlbum Copy(int? count, DateTimeOffset? last = null)
        {
            var album = new ServerAlbum { Id = Guid.NewGuid().ToString("N"), Name = "X", Artist = "Y", LastPlayed = last };
            album.PlayCount = count;
            return album;
        }
        var recent = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var silentFirst = Copy(null);
        silentFirst.Alternates = new[] { Copy(2, recent) };
        Assert.Equal(((int?)2, (DateTimeOffset?)recent), MediaServerService.CombinedPlayInfo(silentFirst));

        var zeroAndSilent = Copy(0);
        zeroAndSilent.Alternates = new[] { Copy(null) };
        Assert.Equal(((int?)null, (DateTimeOffset?)null), MediaServerService.CombinedPlayInfo(zeroAndSilent));

        var bothZero = Copy(0);
        bothZero.Alternates = new[] { Copy(0) };
        Assert.Equal(((int?)0, (DateTimeOffset?)null), MediaServerService.CombinedPlayInfo(bothZero));

        Assert.Equal(((int?)null, (DateTimeOffset?)null), MediaServerService.CombinedPlayInfo(Copy(null)));
    }

    [Fact]
    public void Roulette_PlayChips_UseTheServersPlays_AndLeaveOutAlbumsWithoutAny()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var all = AlbumCatalog.WithServerAlbums(Array.Empty<RouletteAlbum>(), new[]
        {
            new RemoteAlbum("unknown", "Unknown Plays", "A", 2000, "Rock", "Plain", null),
            new RemoteAlbum("never", "Never Played", "B", 2000, "Rock", "Home", null, 0),
            new RemoteAlbum("recent", "Played Last Week", "C", 2000, "Rock", "Home", null, 4, now.AddDays(-7)),
            new RemoteAlbum("old", "Played Two Years Ago", "D", 2000, "Rock", "Home", null, 1, now.AddYears(-2)),
            new RemoteAlbum("undated", "Played Sometime", "E", 2000, "Rock", "Jellyfin", null, 1),
        });
        Assert.Equal(5, all.Count);

        var never = new RouletteFilter { Played = PlayedFilter.NeverPlayed }.Apply(all, now);
        Assert.Equal(new[] { "never" }, never.Select(a => a.ServerAlbumId));

        var notLately = new RouletteFilter { Played = PlayedFilter.NotIn6Months }.Apply(all, now);
        Assert.Equal(new[] { "never", "old" }, notLately.Select(a => a.ServerAlbumId).Order());

        // No chip: every album is on the reel, unknown or not.
        Assert.Equal(5, new RouletteFilter().Apply(all, now).Count);
    }

    [Fact]
    public void Roulette_LibraryAlbums_KeepTheirOldPlayRules()
    {
        var now = DateTimeOffset.Now;
        var library = AlbumCatalog.Group(new[]
        {
            AlbumRoulettePluginTests.T("Song", "Unplayed Album", "Artist", plays: 0),
        });
        Assert.True(library.Single().PlaysKnown);
        Assert.Single(new RouletteFilter { Played = PlayedFilter.NeverPlayed }.Apply(library, now));
        Assert.Single(new RouletteFilter { Played = PlayedFilter.NotIn6Months }.Apply(library, now));
    }

    [AvaloniaFact]
    public async Task PluginApi_HandsTheServersPlaysToTheRoulette()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Navidrome.Add("nd.example.com");
        http.Albums["nd.example.com"] = new[]
        {
            new FakeAlbum("1", "Abbey Road", "The Beatles", 7, "2024-05-01T12:30:00Z"),
            new FakeAlbum("2", "Blue Train", "John Coltrane"),
        };
        http.Albums["plain.example.com"] = new[] { new FakeAlbum("1", "Kind of Blue", "Miles Davis") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetConnections(new[] { Server("nd.example.com", "Home"), Server("plain.example.com", "Plain") });

        using var box = new PluginSandbox();
        const string id = "dev.noctis.plugins.albumroulette";
        box.Approve((id, new[] { "library.read", "playback.control" }));
        var host = box.NewHost(null, new FakeLibraryService(), appVersion: "1.7.3");
        host.MediaServer = service;
        var script = new ScriptedPlugin();
        box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.5", minAppVersion: "1.6.9",
            permissions: new[] { "library.read", "playback.control" }));

        var infos = await script.Host!.Library.GetServerAlbumsAsync();
        Assert.Equal(((int?)7, (DateTimeOffset?)new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.Zero)),
            (infos.Single(a => a.Title == "Abbey Road").PlayCount, infos.Single(a => a.Title == "Abbey Road").LastPlayed));
        Assert.Equal(0, infos.Single(a => a.Title == "Blue Train").PlayCount);
        Assert.Null(infos.Single(a => a.Title == "Kind of Blue").PlayCount);

        using var roulette = new PluginRouletteHost(script.Host);
        var remote = await Task.Run(roulette.LoadServerAlbums);
        Assert.Equal(7, remote.Single(r => r.Title == "Abbey Road").PlayCount);
        Assert.Equal(0, remote.Single(r => r.Title == "Blue Train").PlayCount);
        var kob = remote.Single(r => r.Title == "Kind of Blue");
        Assert.Null(kob.PlayCount);
        Assert.Null(kob.LastPlayed);
    }

    // ── Bug 2: the merged list fills in as the servers answer ──

    /// <summary>A (first in the list) is slow and has Abbey Road; B (second) has Abbey Road and Blue Train.</summary>
    private static (MediaServerService Service, FakeServers Http, TaskCompletionSource GateA, SourceConnection A, SourceConnection B) SlowFirstServer(TestPersistenceService persistence)
    {
        var http = new FakeServers();
        http.Albums["a.example.com"] = new[] { new FakeAlbum("a-abbey", "Abbey Road", "The Beatles"), new FakeAlbum("a-kob", "Kind of Blue", "Miles Davis") };
        http.Albums["b.example.com"] = new[] { new FakeAlbum("b-abbey", "Abbey Road", "The Beatles"), new FakeAlbum("b-train", "Blue Train", "John Coltrane") };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Gates["a.example.com"] = gate;
        var service = new MediaServerService(new HttpClient(http), persistence);
        var a = Server("a.example.com", "A");
        var b = Server("b.example.com", "B");
        service.SetConnections(new[] { a, b });
        return (service, http, gate, a, b);
    }

    [Fact]
    public async Task Progressive_ShowsTheFastServerFirst_ThenSwapsInTheFirstServersCopy()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, gateA, a, b) = SlowFirstServer(persistence);
        var snapshots = new ConcurrentQueue<IReadOnlyList<ServerAlbum>>();

        var load = service.GetAllAlbumsProgressiveAsync(snapshots.Enqueue);

        await Until(() => !snapshots.IsEmpty, "server B's albums");
        Assert.False(load.IsCompleted); // A has not answered
        var early = snapshots.Last();
        Assert.Equal(new[] { "Abbey Road", "Blue Train" }, early.Select(x => x.Name));
        Assert.All(early, x => Assert.Equal(b.Id, x.ConnectionId));
        // Already playable and findable before the whole list is in.
        Assert.Same(early[0], service.FindAlbum(early[0].Key));

        gateA.SetResult();
        var all = await load;

        Assert.Equal(new[] { "Abbey Road", "Blue Train", "Kind of Blue" }, all.Select(x => x.Name));
        var abbey = all[0];
        Assert.Equal(a.Id, abbey.ConnectionId); // the higher server's copy, at the same place
        Assert.Equal(b.Id, Assert.Single(abbey.Alternates).ConnectionId);
        Assert.Empty(early[0].Alternates); // B's copy no longer leads, so it carries none
        Assert.Single(all, x => x.Name == "Abbey Road");
        Assert.Same(all, snapshots.Last());
    }

    [Fact]
    public async Task Progressive_AFailingServer_DoesNotHoldBackTheOthers()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Failing.Add("a.example.com");
        http.Albums["b.example.com"] = new[] { new FakeAlbum("b-train", "Blue Train", "John Coltrane") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        var b = Server("b.example.com", "B");
        service.SetConnections(new[] { Server("a.example.com", "A"), b });

        var all = await service.GetAllAlbumsProgressiveAsync(_ => { });

        Assert.Equal(b.Id, Assert.Single(all).ConnectionId);
    }

    [Fact]
    public async Task Progressive_ReadsTheFirstPageSmall_ThenFullPages_AndCachesTheList()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Albums["a.example.com"] = Enumerable.Range(0, 100).Select(i => new FakeAlbum("a" + i, $"Album {i:000}", "Artist")).ToArray();
        http.Albums["b.example.com"] = new[] { new FakeAlbum("b1", "Other", "Someone") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetConnections(new[] { Server("a.example.com", "A"), Server("b.example.com", "B") });
        var updates = 0;

        var all = await service.GetAllAlbumsProgressiveAsync(_ => Interlocked.Increment(ref updates));

        Assert.Equal(101, all.Count);
        var sizes = http.Requests.Where(r => r.Host == "a.example.com").Select(r => System.Web.HttpUtility.ParseQueryString(r.Query)["size"]).ToArray();
        Assert.Equal(new[] { MediaServerService.FirstListPageSize.ToString(), MediaServerService.FullListPageSize.ToString() }, sizes);
        Assert.True(updates >= 2);

        // Within the cache lifetime: the same list at once, no requests.
        var requests = http.Requests.Count;
        IReadOnlyList<ServerAlbum>? cached = null;
        var again = await service.GetAllAlbumsProgressiveAsync(list => cached = list);
        Assert.Same(all, again);
        Assert.Same(all, cached);
        Assert.Equal(requests, http.Requests.Count);
    }

    [AvaloniaFact]
    public async Task ServerSection_FillsInFromTheFastServer_AndSwapsTheSharedAlbumWithoutDuplicates()
    {
        using var persistence = new TestPersistenceService();
        var (service, _, gateA, a, b) = SlowFirstServer(persistence);
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ServerViewModel(service, player);

        vm.OnNavigatedTo();

        await Until(() => vm.Albums.Count == 2, "server B's albums in the grid");
        Assert.True(vm.IsLoading); // A is still loading
        Assert.Equal(new[] { "Abbey Road", "Blue Train" }, vm.Albums.Select(t => t.Name));
        Assert.All(vm.Albums, t => Assert.Equal(b.Id, t.Album.ConnectionId));
        var trainTile = vm.Albums[1];

        gateA.SetResult();
        await Until(() => !vm.IsLoading, "every server");

        Assert.Equal(new[] { "Abbey Road", "Blue Train", "Kind of Blue" }, vm.Albums.Select(t => t.Name));
        Assert.Equal(a.Id, vm.Albums[0].Album.ConnectionId);
        Assert.Same(trainTile, vm.Albums[1]); // an unchanged album keeps its tile
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasMoreAlbums);
        var rows = vm.AlbumRows.OfType<ServerAlbumRow>().ToList();
        Assert.Equal(3, rows.Sum(r => r.Tiles.Count));
    }

    [AvaloniaFact]
    public async Task ServerSection_OneServerDown_TheOthersAlbumsStillShow()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Failing.Add("a.example.com");
        http.Albums["b.example.com"] = new[] { new FakeAlbum("b-train", "Blue Train", "John Coltrane") };
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetConnections(new[] { Server("a.example.com", "A"), Server("b.example.com", "B") });
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ServerViewModel(service, player);

        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading, "the servers");

        Assert.Equal("Blue Train", Assert.Single(vm.Albums).Name);
        Assert.Equal("", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task ServerSection_ManyAlbums_ShowsAPage_ThenLoadMoreShowsTheNext()
    {
        using var persistence = new TestPersistenceService();
        var http = new FakeServers();
        http.Albums["a.example.com"] = Enumerable.Range(0, 90).Select(i => new FakeAlbum("a" + i, $"Album {i:000}", "Artist")).ToArray();
        http.Albums["b.example.com"] = Enumerable.Range(0, 40).Select(i => new FakeAlbum("b" + i, $"Bxlbum {i:000}", "Other")).ToArray();
        var service = new MediaServerService(new HttpClient(http), persistence);
        service.SetConnections(new[] { Server("a.example.com", "A"), Server("b.example.com", "B") });
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ServerViewModel(service, player);

        vm.OnNavigatedTo();
        await Until(() => !vm.IsLoading, "the servers");
        Assert.Equal(60, vm.Albums.Count);
        Assert.True(vm.HasMoreAlbums);
        Assert.Equal("Album 000", vm.Albums[0].Name);

        await vm.LoadMoreAlbumsCommand.ExecuteAsync(null);
        Assert.Equal(120, vm.Albums.Count);
        await vm.LoadMoreAlbumsCommand.ExecuteAsync(null);
        Assert.Equal(130, vm.Albums.Count);
        Assert.False(vm.HasMoreAlbums);
        Assert.Equal(130, vm.Albums.Select(t => t.Album.Key).Distinct().Count());
    }
}
