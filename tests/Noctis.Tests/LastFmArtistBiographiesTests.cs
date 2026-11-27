using System.Net;
using System.Text.Json;
using Noctis.Mobile.Services;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Artist biographies for the phone's artist page, the album descriptions' twin: Last.fm's
/// artist.getinfo bio, cleaned the album text's way, cached by name so a bio seen once reads
/// offline; an unanswered lookup is not cached on the phone; placeholder names are never asked.
/// No real network: every request goes to an in-process handler.
/// </summary>
public class LastFmArtistBiographiesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LastFmArtistBiographiesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CachePath => Path.Combine(_dir, "cache", "lastfm_artist_bios.json");

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

    private const string Bio =
        "{\"artist\":{\"name\":\"Bruno Mars\",\"bio\":{\"summary\":\"Peter Gene Hernandez, known as Bruno Mars, is a singer. <a href=\\\"https://www.last.fm/music/Bruno+Mars\\\">Read more on Last.fm</a>.\"," +
        "\"content\":\"Peter Gene Hernandez, known as Bruno Mars, is a singer.\\n\\nHe was born in Honolulu. <a href=\\\"https://www.last.fm/music/Bruno+Mars\\\">Read more on Last.fm</a>. User-contributed text is available under the Creative Commons By-SA License; additional terms may apply.\"}}}";

    private const string NotFound = "{\"error\":6,\"message\":\"The artist you supplied could not be found\"}";

    /// <summary>Last.fm's empty bio: only its own link, which cleans to nothing.</summary>
    private const string EmptyBio =
        "{\"artist\":{\"name\":\"Tiny Band\",\"bio\":{\"summary\":\" <a href=\\\"https://www.last.fm/music/Tiny+Band\\\">Read more on Last.fm</a>\",\"content\":\"\"}}}";

    [Fact]
    public async Task Lookup_CleansTheBio_LikeTheAlbumText_AndCachesItByName()
    {
        var handler = new Handler((_, _) => Json(Bio));
        var store = new LastFmArtistBiographies(new HttpClient(handler), CachePath, rememberFailedLookups: false);

        var full = await store.GetAsync("Bruno Mars", preferFullText: true);
        Assert.Equal("Peter Gene Hernandez, known as Bruno Mars, is a singer.\n\nHe was born in Honolulu.", full);
        Assert.Equal("Peter Gene Hernandez, known as Bruno Mars, is a singer.", await store.GetAsync("Bruno Mars", preferFullText: false));
        Assert.Equal(1, handler.Calls);   // the second ask reads the cache
        Assert.Contains("method=artist.getinfo", handler.Requests[0].Query);
        Assert.Contains("artist=Bruno%20Mars", handler.Requests[0].Query);
        Assert.DoesNotContain("album=", handler.Requests[0].Query);

        using (var doc = JsonDocument.Parse(File.ReadAllText(CachePath)))
            Assert.Equal("Peter Gene Hernandez, known as Bruno Mars, is a singer.", doc.RootElement.GetProperty("bruno mars").GetProperty("Summary").GetString());

        // A second store over the same file answers offline, whatever the name's case.
        var offline = new Handler((_, _) => throw new HttpRequestException("offline"));
        var again = new LastFmArtistBiographies(new HttpClient(offline), CachePath, rememberFailedLookups: false);
        Assert.Equal(full, await again.GetAsync(" bruno MARS ", preferFullText: true));
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public async Task UnansweredLookup_IsNotCached_OnThePhone()
    {
        var offline = new Handler((_, _) => throw new HttpRequestException("offline"));
        var phone = new LastFmArtistBiographies(new HttpClient(offline), CachePath, rememberFailedLookups: false);
        Assert.Null(await phone.GetAsync("Bruno Mars", preferFullText: true));
        Assert.False(File.Exists(CachePath));

        // A server error or a rate limit is no answer either.
        var busy = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var later = new LastFmArtistBiographies(new HttpClient(busy), CachePath, rememberFailedLookups: false);
        Assert.Null(await later.GetAsync("Adele", preferFullText: true));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task AnswerWithoutABio_IsCachedAsNone()
    {
        foreach (var body in new[] { NotFound, EmptyBio })
        {
            var handler = new Handler((_, _) => Json(body));
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
            var store = new LastFmArtistBiographies(new HttpClient(handler), path, rememberFailedLookups: false);
            Assert.Null(await store.GetAsync("Tiny Band", preferFullText: true));
            Assert.Contains("tiny band", File.ReadAllText(path));
            // Known to have none: not asked again.
            Assert.Null(await new LastFmArtistBiographies(new HttpClient(handler), path).GetAsync("Tiny Band", preferFullText: true));
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown Artist")]
    [InlineData("Various Artists")]
    [InlineData("various artists")]
    [InlineData("VA")]
    public async Task PlaceholderNames_AreNeverAsked(string name)
    {
        var handler = new Handler((_, _) => Json(Bio));
        var store = new LastFmArtistBiographies(new HttpClient(handler), CachePath);
        Assert.False(LastFmArtistBiographies.IsLookupName(name));
        Assert.Null(await store.GetAsync(name, preferFullText: true));
        Assert.Null(await new LastFmArtistBiographySource(store).GetBiographyAsync(name, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Clear_ForgetsTheCache()
    {
        var store = new LastFmArtistBiographies(new HttpClient(new Handler((_, _) => Json(Bio))), CachePath);
        await store.GetAsync("Bruno Mars", preferFullText: true);
        Assert.True(File.Exists(CachePath));
        await store.ClearAsync();
        Assert.False(File.Exists(CachePath));
    }

    /// <summary>A joint credit with no bio of its own falls back to its first artist, as the album
    /// text does; a single artist is asked once.</summary>
    [Fact]
    public async Task PhoneSource_FallsBackToTheFirstArtist_ForAJointCredit()
    {
        var handler = new Handler((request, _) => request.RequestUri!.Query.Contains("artist=Bruno%20Mars&")
            ? Json(Bio)
            : Json(NotFound));
        var source = LastFmArtistBiographySource.Create(_dir, new HttpClient(handler));
        var text = await source.GetBiographyAsync("Bruno Mars, Anderson .Paak & Silk Sonic", CancellationToken.None);
        Assert.StartsWith("Peter Gene Hernandez", text);
        Assert.Equal(2, handler.Calls);
        Assert.True(File.Exists(CachePath));   // Create keeps it under cache/ in the data directory

        Assert.Null(await source.GetBiographyAsync("Adele", CancellationToken.None));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PhoneSource_GivesUpAfterItsTimeout_WithNull_AndCachesNothing()
    {
        var hang = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var source = new LastFmArtistBiographySource(
            new LastFmArtistBiographies(new HttpClient(hang), CachePath, rememberFailedLookups: false), TimeSpan.FromMilliseconds(200));
        Assert.Null(await source.GetBiographyAsync("A", CancellationToken.None));
        Assert.False(File.Exists(CachePath));

        // The page closing (its token) cancels instead.
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetBiographyAsync("A", closed.Token));

        var failing = new LastFmArtistBiographySource(new LastFmArtistBiographies(
            new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline"))), CachePath, rememberFailedLookups: false));
        Assert.Null(await failing.GetBiographyAsync("B", CancellationToken.None));
    }
}
