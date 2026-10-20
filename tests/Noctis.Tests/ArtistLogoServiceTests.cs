using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

[assembly: AssemblyMetadata("NoctisFanartApiKey", "test-fanart-project")]

namespace Noctis.Tests;

/// <summary>
/// Artist logos: MusicBrainz says which artist a name is (namesakes told apart by the library's
/// titles, else no logo), fanart.tv / TheAudioDB give the logo, only a PNG is kept, and it is
/// cached on disk; no key, no request; a key never reaches a log line.
/// </summary>
public class ArtistLogoServiceTests : IDisposable
{
    private const string FanartKey = "fanart-secret-1234567890";
    private const string AudioDbKey = "audiodb-secret-0987654321";
    private const string ColeId = "875203e1-8e58-4b86-8dcb-7190faf411c5";
    private const string OtherId = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"), "artist_logos");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public string[] Urls => Requests.Select(r => r.RequestUri!.AbsoluteUri).ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly byte[] PngBytes = MakePng();

    private static byte[] MakePng()
    {
        var bytes = new byte[256];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static HttpResponseMessage Png() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) { Headers = { ContentType = new("image/png") } } };

    private static HttpResponseMessage Jpeg()
    {
        var bytes = new byte[256];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } };
    }

    private static string MbArtists(params (string Id, string Name, int Score)[] hits) =>
        "{ \"artists\": [" + string.Join(",", hits.Select(h => $"{{ \"id\": \"{h.Id}\", \"name\": \"{h.Name}\", \"score\": {h.Score} }}")) + "] }";

    private const string FanartLogos = """
        { "name": "J. Cole",
          "hdmusiclogo": [ { "id": "1", "url": "https://assets.fanart.tv/fanart/hd-less-liked.png", "likes": "1" },
                           { "id": "2", "url": "https://assets.fanart.tv/fanart/hd-most-liked.png", "likes": "9" } ],
          "musiclogo": [ { "id": "3", "url": "https://assets.fanart.tv/fanart/small.png", "likes": "20" } ] }
        """;

    private ArtistLogoService Service(Handler handler, ArtistLogoKeys? keys = null, Func<string, System.Collections.Generic.IEnumerable<string>>? titles = null,
        Func<DateTime>? clock = null, Func<byte[], byte[]?>? prepare = null) =>
        new(new HttpClient(handler), _dir, keys ?? new ArtistLogoKeys(FanartProjectKey: FanartKey), titles, prepare,
            musicBrainzSpacing: TimeSpan.Zero, utcNow: clock);

    private static bool IsMusicBrainz(HttpRequestMessage r, string what = "artist/") =>
        r.RequestUri!.AbsoluteUri.StartsWith(ArtistLogoService.MusicBrainzUrl + what, StringComparison.Ordinal);

    private static bool IsFanart(HttpRequestMessage r) => r.RequestUri!.AbsoluteUri.StartsWith(ArtistLogoService.FanartUrl, StringComparison.Ordinal);

    /// <summary>MusicBrainz knows one J. Cole; fanart.tv has his logos.</summary>
    private static Handler KnowsCole() => new(r =>
        IsMusicBrainz(r) ? Json(MbArtists((ColeId, "J. Cole", 100), (OtherId, "J. Coles Band", 80)))
        : IsFanart(r) ? Json(FanartLogos)
        : Png());

    [Fact]
    public async Task WithoutAKey_NothingIsAsked()
    {
        var handler = KnowsCole();
        var service = Service(handler, new ArtistLogoKeys());

        Assert.False(service.IsEnabled);
        Assert.Null(await service.GetLogoAsync("J. Cole"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheMostLikedHdLogo_IsSavedAsAPng_AndServedFromDiskAfterwards()
    {
        var handler = KnowsCole();
        var service = Service(handler);
        Assert.Null(service.CachedLogo("J. Cole"));

        var path = await service.GetLogoAsync("J. Cole");

        Assert.NotNull(path);
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.EndsWith(".png", path);
        Assert.Equal(PngBytes, File.ReadAllBytes(path!));
        var urls = handler.Urls;
        // The name to MusicBrainz (with the User-Agent it requires), the id to fanart.tv, then the
        // most liked HD logo; nothing else.
        Assert.StartsWith(ArtistLogoService.MusicBrainzUrl + "artist/?query=artist%3A%22J.%20Cole%22", urls[0]);
        Assert.Contains("Noctis/", handler.Requests.First().Headers.UserAgent.ToString());
        Assert.Equal($"{ArtistLogoService.FanartUrl}{ColeId}?api_key={FanartKey}", urls[1]);
        Assert.Equal("https://assets.fanart.tv/fanart/hd-most-liked.png", urls[2]);
        Assert.Equal(3, urls.Length);

        // Offline from here on: the file answers, in this service and in a new one (next launch).
        Assert.Equal(path, await service.GetLogoAsync("j. cole"));
        Assert.Equal(path, Service(new Handler(_ => throw new HttpRequestException("offline"))).CachedLogo("J. Cole"));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ACachedLogo_ShowsEvenWithoutKeys()
    {
        var path = await Service(KnowsCole()).GetLogoAsync("J. Cole");
        var keyless = Service(new Handler(_ => throw new InvalidOperationException()), new ArtistLogoKeys());
        Assert.Equal(path, keyless.CachedLogo("J. Cole"));
        Assert.Equal(path, await keyless.GetLogoAsync("J. Cole"));
    }

    [Fact]
    public async Task Namesakes_AreToldApartByTheLibrarysTitles()
    {
        var handler = new Handler(r =>
            IsMusicBrainz(r, "recording/")
                ? Json($$"""
                    { "recordings": [ { "title": "Wet Dreamz", "artist-credit": [ { "artist": { "id": "{{ColeId}}" } } ] },
                                      { "title": "Something Else", "artist-credit": [ { "artist": { "id": "{{OtherId}}" } } ] } ] }
                    """)
                : IsMusicBrainz(r) ? Json(MbArtists((OtherId, "J. Cole", 100), (ColeId, "J. Cole", 100)))
                : IsFanart(r) ? Json(FanartLogos)
                : Png());
        var service = Service(handler, titles: _ => new[] { "Wet Dreamz (Remastered)", "No Role Modelz" });

        Assert.NotNull(await service.GetLogoAsync("J. Cole"));

        var recording = Uri.UnescapeDataString(handler.Urls[1]);
        Assert.Contains("recording:\"Wet Dreamz\"", recording);            // the suffix left off
        Assert.Contains("recording:\"No Role Modelz\"", recording);
        Assert.Equal($"{ArtistLogoService.FanartUrl}{ColeId}?api_key={FanartKey}", handler.Urls[2]);
    }

    [Fact]
    public async Task Namesakes_NothingTellsApart_NoLogo()
    {
        var handler = new Handler(r =>
            IsMusicBrainz(r) ? Json(MbArtists((OtherId, "Mike", 100), (ColeId, "Mike", 100)))
            : IsFanart(r) ? Json(FanartLogos)
            : Png());
        var service = Service(handler);

        Assert.Null(await service.GetLogoAsync("Mike"));
        Assert.DoesNotContain(handler.Urls, u => u.StartsWith(ArtistLogoService.FanartUrl, StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dir, ArtistLogoService.Key("Mike") + ".miss")));   // an answer: not asked again soon
    }

    [Fact]
    public async Task OnlyTheExactName_Counts()
    {
        var handler = new Handler(r => IsMusicBrainz(r) ? Json(MbArtists((ColeId, "Cole", 100))) : Png());
        Assert.Null(await Service(handler).GetLogoAsync("J. Cole"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ANonPngLogo_IsNoLogo()
    {
        var handler = new Handler(r =>
            IsMusicBrainz(r) ? Json(MbArtists((ColeId, "J. Cole", 100)))
            : IsFanart(r) ? Json("""{ "hdmusiclogo": [ { "url": "https://assets.fanart.tv/x.jpg", "likes": "1" } ] }""")
            : Jpeg());
        var service = Service(handler);

        Assert.Null(await service.GetLogoAsync("J. Cole"));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.png").Any());
    }

    [Fact]
    public async Task AFailedService_IsNotRemembered_AMiss_IsForAWhile()
    {
        var fanartUp = false;
        var handler = new Handler(r =>
            IsMusicBrainz(r) ? Json(MbArtists((ColeId, "J. Cole", 100)))
            : IsFanart(r) ? (fanartUp ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.InternalServerError))
            : Png());
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var service = Service(handler, clock: () => now);

        Assert.Null(await service.GetLogoAsync("J. Cole"));                 // fanart.tv down: no answer
        Assert.False(File.Exists(Path.Combine(_dir, ArtistLogoService.Key("J. Cole") + ".miss")));
        var asked = handler.Requests.Count;

        fanartUp = true;                                                      // up, and no logo for him
        Assert.Null(await service.GetLogoAsync("J. Cole"));
        Assert.True(handler.Requests.Count > asked);
        asked = handler.Requests.Count;

        Assert.Null(await service.GetLogoAsync("J. Cole"));                 // remembered: nothing asked
        Assert.Equal(asked, handler.Requests.Count);
        now += ArtistLogoService.DefaultMissRetryAfter + TimeSpan.FromMinutes(1);
        Assert.Null(await service.GetLogoAsync("J. Cole"));                 // a fortnight on: asked again
        Assert.True(handler.Requests.Count > asked);
    }

    [Fact]
    public async Task TheAudioDb_ByMusicBrainzId_WhenFanartHasNone()
    {
        var handler = new Handler(r =>
            IsMusicBrainz(r) ? Json(MbArtists((ColeId, "J. Cole", 100)))
            : IsFanart(r) ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : r.RequestUri!.AbsoluteUri.StartsWith(ArtistLogoService.TheAudioDbUrl, StringComparison.Ordinal)
                ? Json($$"""{ "artists": [ { "strArtist": "J. Cole", "strMusicBrainzID": "{{ColeId}}", "strArtistLogo": "https://r2.theaudiodb.com/images/media/artist/logo/x.png" } ] }""")
            : Png());
        var service = Service(handler, new ArtistLogoKeys(FanartKey, TheAudioDbKey: AudioDbKey));

        Assert.NotNull(await service.GetLogoAsync("J. Cole"));
        Assert.Contains($"{ArtistLogoService.TheAudioDbUrl}{AudioDbKey}/artist-mb.php?i={ColeId}", handler.Urls);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/logo/x.png", handler.Urls.Last());
    }

    [Fact]
    public async Task MusicBrainzDown_TheAudioDbsNameSearch_ExactNameOnly()
    {
        var name = "J. Cole";
        var handler = new Handler(r =>
            IsMusicBrainz(r) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : r.RequestUri!.AbsoluteUri.Contains("/search.php", StringComparison.Ordinal)
                ? Json($$"""{ "artists": [ { "strArtist": "{{name}}", "strArtistAlternate": "J Cole", "strArtistLogo": "https://r2.theaudiodb.com/l.png" } ] }""")
            : Png());
        var keys = new ArtistLogoKeys(TheAudioDbKey: AudioDbKey);

        Assert.NotNull(await Service(handler, keys).GetLogoAsync("J Cole"));       // an alternate name counts

        name = "J. Cole Tribute Band";
        Assert.Null(await Service(handler, keys).GetLogoAsync("Jermaine"));
    }

    [Fact]
    public async Task KeysNeverReachTheLog()
    {
        var handler = new Handler(r => IsMusicBrainz(r)
            ? Json(MbArtists((ColeId, "Keyed Artist", 100)))
            : throw new HttpRequestException($"Failed {r.RequestUri}"));
        await Service(handler, new ArtistLogoKeys(FanartKey, "client-secret-55", AudioDbKey)).GetLogoAsync("Keyed Artist");

        var log = DebugLog.Snapshot();
        Assert.Contains("Artist logo lookup failed", log);
        Assert.DoesNotContain(FanartKey, log);
        Assert.DoesNotContain("client-secret-55", log);
        Assert.DoesNotContain(AudioDbKey, log);
        Assert.DoesNotContain(FanartKey, new ArtistLogoKeys(FanartKey, "client-secret-55", AudioDbKey).ToString());
    }

    [Fact]
    public async Task TheClientKey_TravelsWithTheProjectKey()
    {
        var handler = KnowsCole();
        await Service(handler, new ArtistLogoKeys(FanartKey, "client-1")).GetLogoAsync("J. Cole");
        Assert.Equal($"{ArtistLogoService.FanartUrl}{ColeId}?api_key={FanartKey}&client_key=client-1", handler.Urls[1]);
    }

    [Fact]
    public async Task ThePrepareStep_ShapesTheSavedFile()
    {
        var trimmed = PngBytes.Take(64).ToArray();
        var path = await Service(KnowsCole(), prepare: _ => trimmed).GetLogoAsync("J. Cole");
        Assert.Equal(trimmed, File.ReadAllBytes(path!));
    }

    [Fact]
    public void Keys_ComeFromTheBuildsMetadata()
    {
        var keys = ArtistLogoKeys.FromAssembly(typeof(ArtistLogoServiceTests).Assembly);
        Assert.Equal("test-fanart-project", keys.FanartProjectKey);
        Assert.False(keys.HasTheAudioDb);
        Assert.True(ArtistLogoKeys.FromAssembly(typeof(object).Assembly).IsEmpty);
    }

    [Fact]
    public void FanartLogos_HdMostLikedFirst_ThenTheSmallOnes()
    {
        Assert.Equal(new[]
        {
            "https://assets.fanart.tv/fanart/hd-most-liked.png",
            "https://assets.fanart.tv/fanart/hd-less-liked.png",
            "https://assets.fanart.tv/fanart/small.png",
        }, ArtistLogoService.FanartLogoUrls(FanartLogos));
    }

    [Fact]
    public void Names_CompareFoldedAndSpaced()
    {
        Assert.Equal(ArtistLogoService.NameKey("Beyoncé"), ArtistLogoService.NameKey("beyonce"));
        Assert.Equal(ArtistLogoService.NameKey("Simon & Garfunkel"), ArtistLogoService.NameKey("Simon and Garfunkel"));
        Assert.NotEqual(ArtistLogoService.NameKey("J. Cole"), ArtistLogoService.NameKey("Cole"));
    }
}
