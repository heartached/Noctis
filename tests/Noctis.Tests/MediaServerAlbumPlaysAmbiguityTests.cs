using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.MediaServer;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #268 follow-up: a Noctis play of a server song whose album id is unknown (or no longer
/// listed) falls back to the album's identity (album artist + title). When one server has two
/// albums with that identity (two "Greatest Hits" by one artist), the play used to count for
/// BOTH. Ambiguous now counts for neither; a single match still counts; a known id counts only
/// for its own album. The album id is also kept on more paths (search hits, library sync), so
/// the fallback is needed less. Stubbed HttpMessageHandler, no network.
/// </summary>
public class MediaServerAlbumPlaysAmbiguityTests
{
    private static readonly DateTime T1 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PlayHistoryEvent Play(string? key, ServerAlbum identityOf, int day = 0) => new()
    {
        PlayedAtUtc = T1.AddDays(day),
        ServerAlbumKey = key,
        ServerAlbumIdentity = MediaServerService.AlbumIdentity(identityOf),
    };

    private static (ServerAlbum First, ServerAlbum Second, IReadOnlyList<ServerAlbum> Merged, Guid Server) TwoGreatestHits()
    {
        var server = Guid.NewGuid();
        var first = new ServerAlbum { Id = "gh-1", Name = "Greatest Hits", Artist = "Queen", ConnectionId = server };
        var second = new ServerAlbum { Id = "gh-2", Name = "Greatest Hits", Artist = "Queen", ConnectionId = server };
        var merged = MediaServerService.MergeAlbums(new IReadOnlyList<ServerAlbum>[] { new[] { first, second } });
        Assert.Equal(2, merged.Count); // two albums of one name on one server stay two
        return (first, second, merged, server);
    }

    [Fact]
    public void UnknownAlbumId_TwoSameIdentityAlbumsOnOneServer_CountsForNeither()
    {
        var (first, second, merged, server) = TwoGreatestHits();
        var events = new[]
        {
            Play(null, first),                         // the server never named the album
            Play($"{server:N}:gone", first, 1),        // an album id the server no longer lists
        };

        var plays = NoctisAlbumPlays.Build(events, merged);

        Assert.Equal((0, (DateTimeOffset?)null), plays.For(first));
        Assert.Equal((0, (DateTimeOffset?)null), plays.For(second));
    }

    [Fact]
    public void KnownAlbumId_TwoSameIdentityAlbumsOnOneServer_CountsOnlyForItsAlbum()
    {
        var (first, second, merged, _) = TwoGreatestHits();
        var plays = NoctisAlbumPlays.Build(new[] { Play(second.Key, second) }, merged);

        Assert.Equal((0, (DateTimeOffset?)null), plays.For(first));
        Assert.Equal((1, (DateTimeOffset?)new DateTimeOffset(T1)), plays.For(second));
    }

    [Fact]
    public void UnknownAlbumId_SingleIdentityMatch_StillCounts()
    {
        var server = Guid.NewGuid();
        var only = new ServerAlbum { Id = "x", Name = "Abbey Road", Artist = "The Beatles", ConnectionId = server };
        var other = new ServerAlbum { Id = "y", Name = "Blue Train", Artist = "John Coltrane", ConnectionId = server };
        var merged = MediaServerService.MergeAlbums(new IReadOnlyList<ServerAlbum>[] { new[] { only, other } });

        var plays = NoctisAlbumPlays.Build(new[] { Play(null, only), Play($"{server:N}:gone", only, 1) }, merged);

        Assert.Equal((2, (DateTimeOffset?)new DateTimeOffset(T1.AddDays(1))), plays.For(merged.Single(a => a.Name == "Abbey Road")));
        Assert.Equal((0, (DateTimeOffset?)null), plays.For(merged.Single(a => a.Name == "Blue Train")));
    }

    [Fact]
    public void UnknownAlbumId_AmbiguousOnOneServer_ButUniqueOnThePlayedServer_CountsForThatOne()
    {
        // Server A has two "Greatest Hits"; server B has one, merged as an alternate of A's first.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var a1 = new ServerAlbum { Id = "a1", Name = "Greatest Hits", Artist = "Queen", ConnectionId = a };
        var a2 = new ServerAlbum { Id = "a2", Name = "Greatest Hits", Artist = "Queen", ConnectionId = a };
        var b1 = new ServerAlbum { Id = "b1", Name = "Greatest Hits", Artist = "Queen", ConnectionId = b };
        var merged = MediaServerService.MergeAlbums(new IReadOnlyList<ServerAlbum>[] { new[] { a1, a2 }, new[] { b1 } });
        Assert.Equal(2, merged.Count);
        var withCopy = merged.Single(m => m.Alternates.Count > 0);
        var withoutCopy = merged.Single(m => m.Alternates.Count == 0);

        // A play from server B with a stale album id: B has one such album, so it is that one.
        var fromB = NoctisAlbumPlays.Build(new[] { Play($"{b:N}:stale", b1) }, merged);
        Assert.Equal(1, fromB.For(withCopy).Count);
        Assert.Equal(0, fromB.For(withoutCopy).Count);

        // A play from server A with a stale id, or with no key at all: ambiguous, neither.
        var fromA = NoctisAlbumPlays.Build(new[] { Play($"{a:N}:stale", a1), Play(null, a1, 1) }, merged);
        Assert.Equal(0, fromA.For(withCopy).Count);
        Assert.Equal(0, fromA.For(withoutCopy).Count);
    }

    [Fact]
    public void OldHistoryEntries_StillLoad_AndCountNowhere()
    {
        var path = Path.Combine(Path.GetTempPath(), "noctis-268d-" + Guid.NewGuid().ToString("N"), "play_history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "[{\"TrackId\":\"" + Guid.NewGuid() + "\",\"Title\":\"Old\",\"Artist\":\"Queen\",\"PlayedAtUtc\":\"2025-01-02T03:04:05Z\",\"Skipped\":false}]");
            var e = Assert.Single(new PlayHistoryService(path).Events);
            Assert.Null(e.ServerAlbumKey);
            Assert.Null(e.ServerAlbumIdentity);
            var (first, second, merged, _) = TwoGreatestHits();
            var plays = NoctisAlbumPlays.Build(new[] { e }, merged);
            Assert.Equal(0, plays.For(first).Count);
            Assert.Equal(0, plays.For(second).Count);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    // ── The album id on more paths ──

    [Fact]
    public async Task Subsonic_SearchHit_CarriesItsAlbumId()
    {
        var client = new SubsonicClient(new HttpClient(new Stub(_ => Json("""
            {"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{"song":[
              {"id":"s-9","title":"Bohemian Rhapsody","artist":"Queen","album":"Greatest Hits","albumId":"gh-2","duration":355}
            ]}}}
            """))));
        var connection = new SourceConnection { Id = Guid.NewGuid(), BaseUriOrPath = "https://music.example.com", Username = "demo", TokenOrPassword = "sesame", Type = SourceType.Navidrome };

        var result = await client.SearchAsync(connection, "bohemian");

        var track = Assert.Single(result.Tracks);
        Assert.Equal("gh-2", track.SourceAlbumId);
    }

    [Fact]
    public async Task Jellyfin_SearchHit_CarriesItsAlbumId()
    {
        var client = new JellyfinClient(new HttpClient(new Stub(request =>
            request.RequestUri!.Query.Contains("IncludeItemTypes=Audio")
                ? Json("""{"Items":[{"Id":"t9","Name":"Bohemian Rhapsody","Artists":["Queen"],"AlbumArtist":"Queen","Album":"Greatest Hits","AlbumId":"jf-gh-2","RunTimeTicks":3550000000}],"TotalRecordCount":1}""")
                : Json("""{"Items":[],"TotalRecordCount":0}"""))));
        var connection = new SourceConnection { Id = Guid.NewGuid(), BaseUriOrPath = "https://jf.example.com", Username = "demo", TokenOrPassword = "tok", UserId = "user-1", Type = SourceType.Jellyfin };

        var result = await client.SearchAsync(connection, "bohemian");

        var track = Assert.Single(result.Tracks);
        Assert.Equal("jf-gh-2", track.SourceAlbumId);
    }

    [Fact]
    public async Task LibrarySync_ServerSongs_CarryTheirAlbumId()
    {
        // The library sync (NavidromeMediaSourceConnector) made server songs without the album
        // id, so their plays could only match by identity. Song albumId wins; else the album listed.
        var connector = new NavidromeMediaSourceConnector(new HttpClient(new Stub(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var id = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["id"];
            if (path.EndsWith("getAlbumList2.view"))
                return Json("""{"subsonic-response":{"status":"ok","albumList2":{"album":[{"id":"gh-1","name":"Greatest Hits","artist":"Queen"},{"id":"gh-2","name":"Greatest Hits","artist":"Queen"}]}}}""");
            if (path.EndsWith("getAlbum.view") && id == "gh-1")
                return Json("""{"subsonic-response":{"status":"ok","album":{"id":"gh-1","song":[{"id":"s-1","title":"One","albumId":"gh-1"}]}}}""");
            if (path.EndsWith("getAlbum.view") && id == "gh-2")
                return Json("""{"subsonic-response":{"status":"ok","album":{"id":"gh-2","song":[{"id":"s-2","title":"Two"}]}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
        var connection = new SourceConnection { Id = Guid.NewGuid(), BaseUriOrPath = "http://127.0.0.1:4533", Username = "demo", TokenOrPassword = "sesame", Type = SourceType.Navidrome };

        var tracks = await connector.ScanAsync(connection);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("gh-1", tracks.Single(t => t.Title == "One").SourceAlbumId);
        Assert.Equal("gh-2", tracks.Single(t => t.Title == "Two").SourceAlbumId);
    }

    [Fact]
    public void SourceAlbumId_SurvivesTheTrackJsonRoundTrip_AndOldJsonLoads()
    {
        // library.json keeps whole Track objects (synced server songs the queue resolves on launch).
        var track = new Track { Id = Guid.NewGuid(), Title = "One", SourceType = SourceType.Navidrome, SourceConnectionId = "abc", SourceAlbumId = "gh-1" };
        var back = JsonSerializer.Deserialize<Track>(JsonSerializer.Serialize(track))!;
        Assert.Equal("gh-1", back.SourceAlbumId);

        var local = JsonSerializer.Serialize(new Track { Id = Guid.NewGuid(), Title = "Local" });
        Assert.DoesNotContain("SourceAlbumId", local);
        Assert.Null(JsonSerializer.Deserialize<Track>(local)!.SourceAlbumId);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
