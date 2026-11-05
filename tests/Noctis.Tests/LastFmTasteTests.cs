using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #177: the connected Last.fm account's charts read back into Noctis. Parsing against
/// canned replies shaped like Last.fm's JSON (no network), the cache and its TTL, the mixes
/// built from library songs only, Discover leaving out what the library has, and Home hiding
/// it all while no account is connected.
/// </summary>
public class LastFmTasteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LastFmTasteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CachePath => Path.Combine(_dir, "cache", "lastfm_taste.json");

    // ── Canned replies ──

    private const string TopTracksJson = """
        {"toptracks":{"track":[
          {"name":"Halo - Remastered 2011","playcount":"42","url":"https://www.last.fm/music/Beyonc%C3%A9/_/Halo","artist":{"name":"BEYONCÉ","url":"https://www.last.fm/music/Beyonc%C3%A9"},"@attr":{"rank":"1"}},
          {"name":"Get Lucky (feat. Pharrell Williams)","playcount":30,"artist":{"name":"Daft Punk"}},
          {"name":"Not In Library","playcount":"20","artist":{"name":"Nobody"}},
          {"name":"","playcount":"1","artist":{"name":"Broken"}}
        ],"@attr":{"user":"tester","page":"1","total":"4"}}}
        """;

    // A one-item list comes back as a bare object.
    private const string LovedJson = """
        {"lovedtracks":{"track":{"name":"Song","artist":{"name":"Band","url":"https://www.last.fm/music/Band"},"date":{"uts":"1700000000"}},"@attr":{"user":"tester"}}}
        """;

    private const string TopArtistsJson = """
        {"topartists":{"artist":[
          {"name":"Band","playcount":"300","url":"https://www.last.fm/music/Band"},
          {"name":"Daft Punk","playcount":"200"},
          {"name":"Missing Artist","playcount":"100","url":"https://www.last.fm/music/Missing+Artist"}
        ]}}
        """;

    private const string TopAlbumsJson = """
        {"topalbums":{"album":[
          {"name":"Random Access Memories (Deluxe Edition)","playcount":"50","artist":{"name":"Daft Punk"}},
          {"name":"Homework","playcount":"40","artist":{"name":"Daft Punk"},"url":"https://www.last.fm/music/Daft+Punk/Homework"}
        ]}}
        """;

    private static string SimilarJson(params (string Name, string Match)[] artists)
        => "{\"similarartists\":{\"artist\":[" +
           string.Join(",", artists.Select(a => $"{{\"name\":\"{a.Name}\",\"match\":\"{a.Match}\",\"url\":\"https://www.last.fm/music/{Uri.EscapeDataString(a.Name)}\"}}")) +
           "]}}";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static string Method(HttpRequestMessage r)
        => System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["method"] ?? "";

    private static HttpResponseMessage Account(HttpRequestMessage r) => Method(r) switch
    {
        "user.gettoptracks" => Reply(TopTracksJson),
        "user.getlovedtracks" => Reply(LovedJson),
        "user.gettopartists" => Reply(TopArtistsJson),
        "user.gettopalbums" => Reply(TopAlbumsJson),
        "artist.getsimilar" => Reply(SimilarJson(("Similar One", "0.9"))),
        _ => Reply("{\"error\":3,\"message\":\"Invalid Method\"}", HttpStatusCode.BadRequest),
    };

    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    private static Track T(string title, string artist, string album = "Album", string? albumArtist = null, int plays = 0)
        => new() { Title = title, Artist = artist, AlbumArtist = albumArtist ?? artist, Album = album, PlayCount = plays,
                   FilePath = $"C:/m/{artist}/{album}/{title}.flac" };

    // ── Parsing ──

    [Fact]
    public void Parse_TopTracks_ReadsNamesCountsAndArtistObjects_SkipsBlankEntries()
    {
        using var doc = JsonDocument.Parse(TopTracksJson);
        var tracks = LastFmTasteClient.ParseTracks(doc.RootElement, "toptracks");
        Assert.Equal(3, tracks.Count);
        Assert.Equal(new LastFmTasteTrack("BEYONCÉ", "Halo - Remastered 2011", 42, "https://www.last.fm/music/Beyonc%C3%A9/_/Halo"), tracks[0]);
        Assert.Equal(30, tracks[1].PlayCount); // numeric playcount as well as string
        Assert.Null(tracks[1].Url);
    }

    [Fact]
    public void Parse_OneItemList_AsBareObject_AndEmptyList_AsString()
    {
        using var loved = JsonDocument.Parse(LovedJson);
        Assert.Single(LastFmTasteClient.ParseTracks(loved.RootElement, "lovedtracks"));

        using var empty = JsonDocument.Parse("{\"lovedtracks\":{\"track\":\"\",\"@attr\":{}}}");
        Assert.Empty(LastFmTasteClient.ParseTracks(empty.RootElement, "lovedtracks"));
    }

    [Fact]
    public void Parse_ArtistsAlbumsAndSimilar()
    {
        using var artists = JsonDocument.Parse(TopArtistsJson);
        Assert.Equal(new[] { "Band", "Daft Punk", "Missing Artist" }, LastFmTasteClient.ParseArtists(artists.RootElement).Select(a => a.Name));

        using var albums = JsonDocument.Parse(TopAlbumsJson);
        var a = LastFmTasteClient.ParseAlbums(albums.RootElement);
        Assert.Equal("Daft Punk", a[1].Artist);
        Assert.Equal("Homework", a[1].Name);

        using var similar = JsonDocument.Parse(SimilarJson(("X", "0.5"), ("Y", "1")));
        var s = LastFmTasteClient.ParseSimilar(similar.RootElement, "Seed");
        Assert.Equal(0.5, s[0].Match);
        Assert.Equal("Seed", s[1].SimilarTo);
    }

    [Fact]
    public void Parse_MissingContainer_IsABadReply()
    {
        using var doc = JsonDocument.Parse("{\"something\":{}}");
        var ex = Assert.Throws<LastFmTasteException>(() => LastFmTasteClient.ParseTracks(doc.RootElement, "toptracks"));
        Assert.Equal(LastFmTasteError.BadReply, ex.Kind);
    }

    // ── Client ──

    [Fact]
    public async Task Fetch_ReadsTheAccountWithTheNameAndApiKeyOnly_NeverASessionKey()
    {
        var handler = new Handler(Account);
        var client = new LastFmTasteClient(new HttpClient(handler), NoDelay);
        var now = DateTimeOffset.UtcNow;

        var s = await client.FetchSnapshotAsync(" tester ", LastFmPeriod.ThreeMonths, now, CancellationToken.None);

        Assert.Equal("tester", s.User);
        Assert.Equal(LastFmPeriod.ThreeMonths, s.Period);
        Assert.Equal(now, s.FetchedUtc);
        Assert.Equal(3, s.TopTracks.Count);
        Assert.Single(s.LovedTracks);
        Assert.Equal(3, s.TopArtists.Count);
        Assert.Equal(2, s.TopAlbums.Count);
        // One artist.getsimilar per top artist (three here, capped at five).
        Assert.Equal(3, s.SimilarArtists.Count);
        Assert.Equal(new[] { "Band", "Daft Punk", "Missing Artist" }, s.SimilarArtists.Select(x => x.SimilarTo));

        Assert.All(handler.Requests, u =>
        {
            Assert.Contains("api_key=", u.Query);
            Assert.DoesNotContain("sk=", u.Query);
            Assert.DoesNotContain("api_sig=", u.Query);
            Assert.StartsWith("https://ws.audioscrobbler.com/2.0/", u.ToString());
        });
        Assert.Contains(handler.Requests, u => u.Query.Contains("method=user.gettoptracks") && u.Query.Contains("period=3month") && u.Query.Contains("user=tester"));
    }

    [Fact]
    public async Task Fetch_InvalidPeriod_FallsBackToTheDefault()
    {
        var handler = new Handler(Account);
        var s = await new LastFmTasteClient(new HttpClient(handler), NoDelay)
            .FetchSnapshotAsync("tester", "forever", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(LastFmPeriod.Default, s.Period);
        Assert.Contains(handler.Requests, u => u.Query.Contains("period=6month"));
    }

    [Theory]
    [InlineData(6, LastFmTasteError.UserNotFound)]
    [InlineData(17, LastFmTasteError.Private)]
    [InlineData(10, LastFmTasteError.BadReply)]
    public async Task Fetch_LastFmErrorCodes_MapToTheirKind(int code, LastFmTasteError kind)
    {
        var handler = new Handler(_ => Reply($"{{\"error\":{code},\"message\":\"x\"}}", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<LastFmTasteException>(() => new LastFmTasteClient(new HttpClient(handler), NoDelay)
            .FetchSnapshotAsync("tester", LastFmPeriod.Default, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Single(handler.Requests); // not retried
    }

    [Fact]
    public async Task Fetch_RateLimitAndServerErrors_AreRetried_ThenSucceed()
    {
        var calls = 0;
        var handler = new Handler(r =>
        {
            if (Method(r) == "user.gettoptracks" && calls++ < 2)
                return calls == 1 ? Reply("{\"error\":29,\"message\":\"Rate limit exceeded\"}") : Reply("", HttpStatusCode.ServiceUnavailable);
            return Account(r);
        });
        var waits = new List<TimeSpan>();
        var client = new LastFmTasteClient(new HttpClient(handler), (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var s = await client.FetchSnapshotAsync("tester", LastFmPeriod.Default, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(3, s.TopTracks.Count);
        Assert.Equal(3, handler.Requests.Count(u => u.Query.Contains("method=user.gettoptracks")));
        Assert.Contains(TimeSpan.FromSeconds(2), waits); // back-off before the second attempt
    }

    [Fact]
    public async Task Fetch_Offline_IsUnreachable_AfterTheRetries()
    {
        var handler = new Handler(_ => throw new HttpRequestException("No such host: https://ws.audioscrobbler.com/2.0/?api_key=SECRET"));
        var ex = await Assert.ThrowsAsync<LastFmTasteException>(() => new LastFmTasteClient(new HttpClient(handler), NoDelay)
            .FetchSnapshotAsync("tester", LastFmPeriod.Default, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(LastFmTasteError.Unreachable, ex.Kind);
        Assert.Equal(LastFmTasteClient.MaxAttempts, handler.Requests.Count);
        Assert.DoesNotContain("api_key", ex.Message); // the request URL never reaches the message
    }

    [Fact]
    public async Task Fetch_OneSimilarSeedFailing_LeavesOnlyThatSeedOut()
    {
        var handler = new Handler(r =>
            Method(r) == "artist.getsimilar" && r.RequestUri!.Query.Contains("artist=Band")
                ? Reply("{\"error\":6,\"message\":\"The artist you supplied could not be found\"}", HttpStatusCode.NotFound)
                : Account(r));
        var s = await new LastFmTasteClient(new HttpClient(handler), NoDelay)
            .FetchSnapshotAsync("tester", LastFmPeriod.Default, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(new[] { "Daft Punk", "Missing Artist" }, s.SimilarArtists.Select(x => x.SimilarTo));
    }

    // ── Service: cache, TTL, failures ──

    private sealed class FakeFetch
    {
        public int Calls;
        public Func<string, LastFmTasteSnapshot>? Next;
        public LastFmTasteError? Fail;

        public Task<LastFmTasteSnapshot> Run(string user, string period, DateTimeOffset at, CancellationToken ct)
        {
            Calls++;
            if (Fail is { } f) throw new LastFmTasteException(f);
            var s = Next?.Invoke(user) ?? new LastFmTasteSnapshot { TopTracks = { new LastFmTasteTrack("A", "Song " + Calls, 1, null) } };
            s.User = user;
            s.Period = period;
            s.FetchedUtc = at;
            return Task.FromResult(s);
        }
    }

    [Fact]
    public async Task Service_ServesTheCacheWithinTheTtl_ThenReadsAgain()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var fetch = new FakeFetch();
        var svc = new LastFmTasteService(CachePath, fetch.Run, () => now, TimeSpan.FromHours(12));

        var first = await svc.GetAsync("tester");
        Assert.False(first.FromCache);
        Assert.Null(first.Error);

        now = now.AddHours(11);
        var second = await svc.GetAsync("Tester"); // the name compares case-insensitively
        Assert.True(second.FromCache);
        Assert.Same(first.Snapshot, second.Snapshot);
        Assert.Equal(1, fetch.Calls);

        now = now.AddHours(2);
        var third = await svc.GetAsync("tester");
        Assert.False(third.FromCache);
        Assert.Equal(2, fetch.Calls);
    }

    [Fact]
    public async Task Service_ForceRefresh_ReadsEvenWhenFresh()
    {
        var fetch = new FakeFetch();
        var svc = new LastFmTasteService(CachePath, fetch.Run);
        await svc.GetAsync("tester");
        var forced = await svc.GetAsync("tester", forceRefresh: true);
        Assert.False(forced.FromCache);
        Assert.Equal(2, fetch.Calls);
    }

    [Fact]
    public async Task Service_PersistsToDisk_AndANewInstanceReadsIt()
    {
        var now = DateTimeOffset.UtcNow;
        var fetch = new FakeFetch();
        await new LastFmTasteService(CachePath, fetch.Run, () => now).GetAsync("tester");
        Assert.True(File.Exists(CachePath));
        Assert.False(File.Exists(CachePath + ".tmp"));

        var again = await new LastFmTasteService(CachePath, fetch.Run, () => now.AddHours(1)).GetAsync("tester");
        Assert.True(again.FromCache);
        Assert.Equal("Song 1", again.Snapshot!.TopTracks[0].Title);
        Assert.Equal(1, fetch.Calls);
    }

    [Fact]
    public async Task Service_AnotherAccount_IsNotServedTheFirstOnesCache()
    {
        var fetch = new FakeFetch();
        var svc = new LastFmTasteService(CachePath, fetch.Run);
        await svc.GetAsync("alice");
        var bob = await svc.GetAsync("bob");
        Assert.Equal("bob", bob.Snapshot!.User);
        Assert.Equal(2, fetch.Calls);
        Assert.Null(await svc.PeekAsync("alice")); // one account cached at a time
    }

    [Fact]
    public async Task Service_FailedRefresh_KeepsTheOldSnapshot_AndCoolsDown()
    {
        var now = DateTimeOffset.UtcNow;
        var fetch = new FakeFetch();
        var svc = new LastFmTasteService(CachePath, fetch.Run, () => now, TimeSpan.FromHours(12));
        var ok = await svc.GetAsync("tester");

        now = now.AddHours(13); // stale
        fetch.Fail = LastFmTasteError.Unreachable;
        var failed = await svc.GetAsync("tester");
        Assert.Same(ok.Snapshot, failed.Snapshot);
        Assert.Equal(LastFmTasteError.Unreachable, failed.Error);
        Assert.Equal(2, fetch.Calls);

        // Home visits during the cooldown cost no request...
        now = now.AddMinutes(5);
        var cooled = await svc.GetAsync("tester");
        Assert.Equal(LastFmTasteError.Unreachable, cooled.Error);
        Assert.Equal(2, fetch.Calls);

        // ...Refresh still asks, and so does a visit after it.
        await svc.GetAsync("tester", forceRefresh: true);
        Assert.Equal(3, fetch.Calls);
        fetch.Fail = null;
        now = now.AddMinutes(11);
        var back = await svc.GetAsync("tester");
        Assert.Null(back.Error);
        Assert.Equal(4, fetch.Calls);
    }

    [Fact]
    public async Task Service_FirstReadFailing_ReturnsNoSnapshotAndTheError()
    {
        var fetch = new FakeFetch { Fail = LastFmTasteError.Private };
        var r = await new LastFmTasteService(CachePath, fetch.Run).GetAsync("tester");
        Assert.Null(r.Snapshot);
        Assert.Equal(LastFmTasteError.Private, r.Error);
    }

    [Fact]
    public async Task Service_NoAccountName_ReadsNothing()
    {
        var fetch = new FakeFetch();
        var r = await new LastFmTasteService(CachePath, fetch.Run).GetAsync("  ");
        Assert.Null(r.Snapshot);
        Assert.Null(r.Error);
        Assert.Equal(0, fetch.Calls);
    }

    [Fact]
    public async Task Service_Clear_ForgetsAndDeletesTheFile()
    {
        var fetch = new FakeFetch();
        var svc = new LastFmTasteService(CachePath, fetch.Run);
        await svc.GetAsync("tester");
        await svc.ClearAsync();
        Assert.False(File.Exists(CachePath));
        Assert.Null(await svc.PeekAsync("tester"));
        await svc.GetAsync("tester");
        Assert.Equal(2, fetch.Calls);
    }

    [Fact]
    public async Task Service_CorruptOrOldCacheFile_IsIgnored()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{ not json");
        var fetch = new FakeFetch();
        var r = await new LastFmTasteService(CachePath, fetch.Run).GetAsync("tester");
        Assert.NotNull(r.Snapshot);
        Assert.Equal(1, fetch.Calls);

        File.WriteAllText(CachePath, "{\"Version\":99,\"User\":\"tester\",\"FetchedUtc\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}");
        var r2 = await new LastFmTasteService(CachePath, fetch.Run).GetAsync("tester");
        Assert.False(r2.FromCache);
        Assert.Equal(2, fetch.Calls);
    }

    // ── Mixes ──

    private static LastFmTasteSnapshot Snapshot() => new()
    {
        User = "tester",
        TopTracks =
        {
            new("BEYONCE", "Halo - Remastered 2011", 42, null),
            new("Daft Punk", "Get Lucky (feat. Pharrell Williams)", 30, null),
            new("Band", "Song (Live)", 25, null),
            new("Nobody", "Not In Library", 20, null),
            new("Beyoncé", "Halo", 10, null), // the same song again: listed once
        },
        LovedTracks = { new("band", "song", 0, null) },
        TopArtists = { new("Band", 300, null), new("Daft Punk", 200, null), new("Missing Artist", 100, null) },
    };

    [Fact]
    public void Mixes_TopSongs_MatchesFoldedNamesInChartOrder_LibraryOnly()
    {
        var halo = T("Halo", "Beyoncé");
        var lucky = T("Get Lucky", "Daft Punk feat. Pharrell Williams", "Random Access Memories");
        var song = T("Song", "Band");
        var set = LastFmTasteMixes.Build(new[] { song, lucky, halo }, Snapshot());

        Assert.Equal(new[] { halo, lucky }, set.TopSongs); // live take never lands on the studio song
        Assert.Equal(new[] { song }, set.Loved);
    }

    [Fact]
    public void Mixes_ArtistMix_DealsRoundRobin_MostPlayedFirst_WithoutTheTopSongs()
    {
        var b1 = T("B1", "Band", plays: 9);
        var b2 = T("B2", "Band", plays: 5);
        var b3 = T("B3", "Band", plays: 1);
        var d1 = T("D1", "Daft Punk", plays: 7);
        var d2 = T("D2", "Other feat. Daft Punk", plays: 3); // a credited name counts
        var unrelated = T("U", "Someone Else", plays: 100);

        var mix = LastFmTasteMixes.BuildArtistMix(new[] { b3, d2, unrelated, b1, d1, b2 },
            new[] { new LastFmTasteArtist("Band", 1, null), new LastFmTasteArtist("Daft Punk", 1, null) },
            exclude: new[] { b2 });

        Assert.Equal(new[] { b1, d1, b3, d2 }, mix);
    }

    [Fact]
    public void Mixes_ArtistMix_OneCopyPerSong()
    {
        var album = T("Hit", "Band", "Album", plays: 2);
        var best = T("Hit", "Band", "Greatest Hits", plays: 1);
        best.IsCompilation = true;
        var mix = LastFmTasteMixes.BuildArtistMix(new[] { best, album }, new[] { new LastFmTasteArtist("Band", 1, null) });
        Assert.Equal(new[] { album }, mix);
    }

    [Fact]
    public void Mixes_CapAtTheMixSize()
    {
        var lib = Enumerable.Range(0, 80).Select(i => T("S" + i, "A")).ToList();
        var snap = new LastFmTasteSnapshot { TopTracks = lib.Select(t => new LastFmTasteTrack("A", t.Title, 1, null)).ToList() };
        Assert.Equal(LastFmTasteMixes.DefaultMixSize, LastFmTasteMixes.Build(lib, snap).TopSongs.Count);
    }

    // ── Discover ──

    [Fact]
    public void Discover_LeavesOutArtistsTheLibraryHas_IncludingCreditedNames()
    {
        var lib = new[]
        {
            T("x", "Band"),
            T("y", "Someone feat. Have Credit"),
            T("z", "Various", albumArtist: "Album Artist Act"),
        };
        var snap = new LastFmTasteSnapshot
        {
            TopArtists = { new("Band", 1, null) },
            SimilarArtists =
            {
                new("BAND", 0.99, null, "Band"),
                new("Have Credit", 0.9, null, "Band"),
                new("Album Artist Act", 0.8, null, "Band"),
                new("New Act", 0.5, null, "Band"),
            },
        };
        var d = LastFmTasteMixes.BuildDiscover(lib, snap);
        Assert.Equal(new[] { "New Act" }, d.Select(x => x.Name));
        Assert.Equal(LastFmDiscoverReason.SimilarTo, d[0].Reason);
        Assert.Equal(new[] { "Band" }, d[0].Because);
    }

    [Fact]
    public void Discover_SimilarArtists_RankBySummedMatch_AndSkipTheUsersOwnTopArtists()
    {
        var snap = new LastFmTasteSnapshot
        {
            TopArtists = { new("Seed A", 1, null), new("Seed B", 1, null) },
            SimilarArtists =
            {
                new("Close To One", 0.95, null, "Seed A"),
                new("Close To Both", 0.6, null, "Seed A"),
                new("close to both", 0.6, null, "Seed B"),
                new("Seed B", 0.9, null, "Seed A"), // their own top artist
            },
        };
        // The seeds are in the library, so they aren't "missing" either.
        var d = LastFmTasteMixes.BuildDiscover(new[] { T("1", "Seed A"), T("2", "Seed B") }, snap);
        Assert.Equal(new[] { "Close To Both", "Close To One" }, d.Select(x => x.Name));
        Assert.Equal(new[] { "Seed A", "Seed B" }, d[0].Because);
    }

    [Fact]
    public void Discover_ListsMissingTopAlbumsAndArtists_NotOnesTheLibraryHas()
    {
        var lib = new[] { T("Get Lucky", "Daft Punk", "Random Access Memories") };
        var d = LastFmTasteMixes.BuildDiscover(lib, new LastFmTasteSnapshot
        {
            TopAlbums =
            {
                new("Daft Punk", "Random Access Memories (Deluxe Edition)", 50, null), // edition of one we have
                new("Daft Punk", "Homework", 40, "https://www.last.fm/music/Daft+Punk/Homework"),
            },
            TopArtists = { new("Daft Punk", 1, null), new("Missing Artist", 1, null) },
        });

        Assert.Equal(2, d.Count);
        Assert.True(d[0].IsAlbum);
        Assert.Equal(("Homework", "Daft Punk", LastFmDiscoverReason.TopAlbumMissing), (d[0].Name, d[0].Artist, d[0].Reason));
        Assert.Equal("https://www.last.fm/music/Daft+Punk/Homework", d[0].Url);
        Assert.Equal(("Missing Artist", LastFmDiscoverReason.TopArtistMissing), (d[1].Name, d[1].Reason));
        Assert.Equal("https://www.last.fm/music/Missing+Artist", d[1].Url);
    }

    [Fact]
    public void Discover_IsCapped_AndEachArtistListedOnce()
    {
        var snap = new LastFmTasteSnapshot
        {
            TopArtists = { new("Seed", 1, null) },
            SimilarArtists = Enumerable.Range(0, 30).Select(i => new LastFmSimilarArtist("Act " + i, 1 - i / 100.0, null, "Seed"))
                .Append(new LastFmSimilarArtist("ACT 0", 0.1, null, "Seed")).ToList(),
        };
        var d = LastFmTasteMixes.BuildDiscover(new[] { T("s", "Seed") }, snap);
        Assert.Equal(LastFmTasteMixes.DefaultDiscoverSize, d.Count);
        Assert.Single(d, x => x.Name.Equals("Act 0", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://www.last.fm/music/Band", "https://www.last.fm/music/Band")]
    [InlineData("https://last.fm/music/Band", "https://last.fm/music/Band")]
    [InlineData("http://www.last.fm/music/Band", "https://www.last.fm/music/AC%2FDC+%26+Co")] // not https
    [InlineData("https://evil.example/last.fm", "https://www.last.fm/music/AC%2FDC+%26+Co")]
    [InlineData("https://notlast.fm/x", "https://www.last.fm/music/AC%2FDC+%26+Co")]
    [InlineData(null, "https://www.last.fm/music/AC%2FDC+%26+Co")]
    public void Links_OnlyLastFmPagesPassThrough(string? apiUrl, string expected)
        => Assert.Equal(expected, LastFmLinks.Artist("AC/DC & Co", apiUrl));

    [Fact]
    public void Links_AlbumPage()
        => Assert.Equal("https://www.last.fm/music/Daft+Punk/Random+Access+Memories", LastFmLinks.Album("Daft Punk", "Random Access Memories"));

    // ── Home ──

    private static (HomeViewModel Home, SettingsViewModel Settings, FakeLibraryService Lib, FakeFetch Fetch) BuildHome(string cachePath)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var fetch = new FakeFetch { Next = _ => Snapshot() };
        var svc = new LastFmTasteService(cachePath, fetch.Run);
        var home = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), settings: settings, lastFmTaste: svc);
        return (home, settings, lib, fetch);
    }

    [AvaloniaFact]
    public async Task Home_NotConnected_ShowsNothing_AndReadsNothing()
    {
        var (home, settings, lib, fetch) = BuildHome(CachePath);
        lib.TrackList.Add(T("Halo", "Beyoncé"));
        settings.LastFmUsername = "tester"; // a stale name, no session

        await home.RefreshLastFmAsync(force: true);

        Assert.False(home.IsLastFmSectionVisible);
        Assert.False(home.IsLastFmDiscoverVisible);
        Assert.Equal(0, home.LastFmTopSongs.Count);
        Assert.Equal(0, fetch.Calls);
    }

    [AvaloniaFact]
    public async Task Home_Connected_FillsTheMixes_AndTheToggleOrSignOutHidesThem()
    {
        var (home, settings, lib, fetch) = BuildHome(CachePath);
        var halo = T("Halo", "Beyoncé");
        var song = T("Song", "Band");
        lib.TrackList.AddRange(new[] { halo, song });
        settings.LastFmUsername = "tester";
        settings.IsLastFmConnected = true;

        await home.RefreshLastFmAsync(force: false);

        Assert.True(home.IsLastFmSectionVisible);
        Assert.Equal(new[] { halo }, home.LastFmTopSongs.Shown);
        Assert.Equal(new[] { song }, home.LastFmLoved.All);
        Assert.True(home.IsLastFmDiscoverVisible); // "Missing Artist", "Similar One"... per the snapshot
        Assert.Contains(home.LastFmDiscover, r => r.Title == "Missing Artist");
        var calls = fetch.Calls;

        settings.LastFmTasteEnabled = false;
        await home.RefreshLastFmAsync(force: false);
        Assert.False(home.IsLastFmSectionVisible);
        Assert.Empty(home.LastFmTopSongs.Shown);

        settings.LastFmTasteEnabled = true;
        await home.RefreshLastFmAsync(force: false);
        Assert.True(home.IsLastFmSectionVisible);
        Assert.Equal(calls, fetch.Calls); // served from the cache, no new read

        settings.IsLastFmConnected = false;
        await home.RefreshLastFmAsync(force: false);
        Assert.False(home.IsLastFmSectionVisible);
        Assert.Empty(home.LastFmDiscover);
    }

    [AvaloniaFact]
    public async Task HomeView_MountsTheLastFmSections_AndAPillPlaysItsWholeMix()
    {
        var app = Avalonia.Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/"))
            { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/"))
            { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }

        var (home, settings, lib, _) = BuildHome(CachePath);
        var halo = T("Halo", "Beyoncé");
        var lucky = T("Get Lucky", "Daft Punk");
        lib.TrackList.AddRange(new[] { halo, lucky });
        settings.LastFmUsername = "tester";
        settings.IsLastFmConnected = true;
        await home.RefreshLastFmAsync(force: false);

        var view = new Noctis.Views.HomeView { DataContext = home };
        var win = new Avalonia.Controls.Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Avalonia.Controls.StackPanel>("LastFmSection")!.IsVisible);
        Assert.True(view.FindControl<Avalonia.Controls.StackPanel>("LastFmDiscoverSection")!.IsVisible);
        var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
            .OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("From your Last.fm", texts);
        Assert.Contains("Your Last.fm Top Songs", texts);
        Assert.Contains("Discover on Last.fm", texts);
        Assert.Contains("Missing Artist", texts);

        // The second pill of Top Songs plays the whole mix from that song.
        var pills = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
            .OfType<Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("home-history-pill") && b.DataContext is Track).ToList();
        var luckyPill = pills.First(p => ReferenceEquals(p.DataContext, lucky));
        Assert.NotNull(luckyPill.Command);
        Assert.True(luckyPill.Command!.CanExecute(luckyPill.CommandParameter));
        win.Close();
    }

    [AvaloniaFact]
    public async Task Home_SnoozedSongs_StayOutOfTheMixes()
    {
        var (home, settings, lib, _) = BuildHome(CachePath);
        var halo = T("Halo", "Beyoncé");
        halo.SnoozedUntil = DateTime.UtcNow.AddDays(30);
        lib.TrackList.Add(halo);
        settings.LastFmUsername = "tester";
        settings.IsLastFmConnected = true;

        await home.RefreshLastFmAsync(force: false);

        Assert.Empty(home.LastFmTopSongs.Shown);
    }
}
