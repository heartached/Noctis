using System.Net;
using System.Text.Json;
using Noctis.Mobile.Services;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The album descriptions moved out of the desktop's LastFmService into Core so the phone looks
/// them up the same way: album.getinfo's wiki, cleaned, cached in the desktop's JSON shape and
/// keys. The desktop keeps caching an unanswered lookup as "none" (unchanged behaviour); the
/// phone, often offline, does not, and gives up after its timeout.
/// </summary>
public class LastFmAlbumDescriptionsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LastFmAlbumDescriptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CachePath => Path.Combine(_dir, "cache", "lastfm_album_descriptions.json");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request.RequestUri!);
            return respond(request, ct);
        }
    }

    private static Task<HttpResponseMessage> Json(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private const string Wiki =
        "{\"album\":{\"wiki\":{\"summary\":\"Silk Sonic's debut. <a href=\\\"https://www.last.fm/music/x\\\">Read more on Last.fm</a>.\"," +
        "\"content\":\"Silk Sonic's debut.\\n\\nSecond paragraph. <a href=\\\"https://www.last.fm/music/x\\\">Read more on Last.fm</a>. User-contributed text is available under the Creative Commons By-SA License; additional terms may apply.\"}}}";

    [Fact]
    public async Task Lookup_CleansTheWiki_AndCachesItInTheDesktopsShape()
    {
        var handler = new Handler((_, _) => Json(Wiki));
        var store = new LastFmAlbumDescriptions(new HttpClient(handler), CachePath, rememberFailedLookups: false);

        var full = await store.GetAsync("Silk Sonic", "An Evening with Silk Sonic", preferFullText: true);
        Assert.Equal("Silk Sonic's debut.\n\nSecond paragraph.", full);
        Assert.Contains("method=album.getinfo", handler.Requests[0].Query);
        Assert.Contains("artist=Silk%20Sonic", handler.Requests[0].Query);

        using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
        var entry = doc.RootElement.GetProperty("silk sonic::an evening with silk sonic");
        Assert.Equal("Silk Sonic's debut.", entry.GetProperty("Summary").GetString());
        Assert.True(entry.TryGetProperty("UserOverride", out _));

        // A second store over the same file answers offline.
        var offline = new LastFmAlbumDescriptions(new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline"))), CachePath);
        Assert.Equal(full, await offline.GetAsync("Silk Sonic", "An Evening with Silk Sonic", preferFullText: true));
    }

    [Fact]
    public async Task UnansweredLookup_IsNotCached_OnThePhone_ButIsOnTheDesktop()
    {
        var offline = new Handler((_, _) => throw new HttpRequestException("offline"));
        var phone = new LastFmAlbumDescriptions(new HttpClient(offline), CachePath, rememberFailedLookups: false);
        Assert.Null(await phone.GetAsync("Artist", "Album", preferFullText: true));
        Assert.False(File.Exists(CachePath));

        var desktop = new LastFmAlbumDescriptions(new HttpClient(offline), CachePath);
        Assert.Null(await desktop.GetAsync("Artist", "Album", preferFullText: true));
        Assert.Contains("artist::album", File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task AnswerWithoutAWiki_IsCachedAsNone_OnBoth()
    {
        var handler = new Handler((_, _) => Json("{\"error\":6,\"message\":\"Album not found\"}"));
        var phone = new LastFmAlbumDescriptions(new HttpClient(handler), CachePath, rememberFailedLookups: false);
        Assert.Null(await phone.GetAsync("Artist", "Album", preferFullText: true));
        Assert.Contains("artist::album", File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task UserOverride_Wins()
    {
        var store = new LastFmAlbumDescriptions(new HttpClient(new Handler((_, _) => Json(Wiki))), CachePath);
        await store.SetOverrideAsync("Silk Sonic", "An Evening with Silk Sonic", "Mine.");
        Assert.Equal("Mine.", await store.GetAsync("Silk Sonic", "An Evening with Silk Sonic", preferFullText: true));
    }

    [Fact]
    public void DesktopForwarders_StillClean()
    {
        Assert.Equal(LastFmAlbumDescriptions.CleanAlbumSummary("A <b>b</b>."), LastFmService.CleanAlbumSummary("A <b>b</b>."));
        Assert.Equal("Music.", LastFmService.ScrubOrphanPeriods("Music. ."));
    }

    /// <summary>Last.fm has An Evening with Silk Sonic under "Bruno Mars", not under the album's
    /// full credit: the phone asks the credit first, then its first artist.</summary>
    [Fact]
    public async Task PhoneSource_FallsBackToTheFirstArtist_ForACollaboration()
    {
        var handler = new Handler((request, _) => request.RequestUri!.Query.Contains("artist=Bruno%20Mars&")
            ? Json(Wiki)
            : Json("{\"error\":6,\"message\":\"Album not found\"}"));
        var source = new LastFmAlbumDescriptionSource(new LastFmAlbumDescriptions(new HttpClient(handler), CachePath, rememberFailedLookups: false));
        var text = await source.GetDescriptionAsync("Bruno Mars, Anderson .Paak & Silk Sonic", "An Evening with Silk Sonic", CancellationToken.None);
        Assert.StartsWith("Silk Sonic's debut.", text);
        Assert.Equal(2, handler.Calls);

        // A single artist is asked once.
        Assert.Null(await source.GetDescriptionAsync("Adele", "25", CancellationToken.None));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PhoneSource_GivesUpAfterItsTimeout_WithNull_AndCachesNothing()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), LastFmAlbumDescriptionSource.DefaultTimeout);
        // A server that never answers: the source's own timeout ends it with null.
        var hang = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var source = new LastFmAlbumDescriptionSource(
            new LastFmAlbumDescriptions(new HttpClient(hang), CachePath, rememberFailedLookups: false), TimeSpan.FromMilliseconds(200));
        Assert.Null(await source.GetDescriptionAsync("A", "B", CancellationToken.None));
        Assert.False(File.Exists(CachePath));

        // The page closing (its token) cancels instead.
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetDescriptionAsync("A", "B", closed.Token));

        var failing = new LastFmAlbumDescriptionSource(new LastFmAlbumDescriptions(
            new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline"))), CachePath, rememberFailedLookups: false));
        Assert.Null(await failing.GetDescriptionAsync("A", "B", CancellationToken.None));
    }
}
