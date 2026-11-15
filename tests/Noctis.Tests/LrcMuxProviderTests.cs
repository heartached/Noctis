using System.Net;
using System.Net.Http;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #219: lrcmux (api.lrcmux.dev, documented by its OpenAPI spec, keyless, 60 requests a minute)
/// against trimmed copies of real answers captured 2026-11-09 — no network. Same contract as the other
/// providers: a definitive miss is null, a failure is <see cref="LyricsProviderException"/> (busy on
/// 429), and an answer for another song or another-language version is a miss.
/// </summary>
public class LrcMuxProviderTests
{
    private sealed class Router(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public readonly List<string> UserAgents = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.AbsoluteUri);
                UserAgents.Add(request.Headers.UserAgent.ToString());
            }
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{\"title\":\"Not Found\",\"status\":404,\"detail\":\"no lyrics found\"}") };

    // /get?artist=Vijay Yesudas&title=Malare&duration=316: KuGou's word timings, two of its 29 lines.
    private const string MalareWord =
        "{\"track\":{\"isrc\":\"FRX451554822\",\"title\":\"Malare (From \\\"Premam\\\")\",\"artist\":\"Vijay Yesudas\",\"album\":\"Malare (From \\\"Premam\\\")\",\"duration\":316}," +
        "\"meta\":{\"source\":{\"id\":\"kugou\",\"name\":\"KuGou\",\"url\":\"https://kugou.com\"},\"level\":\"word\"}," +
        "\"lines\":[" +
        "{\"text\":\"Thelimanam mazhavillin\",\"start\":18154,\"end\":20246,\"words\":[{\"text\":\"Thelimanam \",\"start\":18154,\"end\":19048},{\"text\":\"mazhavillin\",\"start\":19048,\"end\":20246}]}," +
        "{\"text\":\"Puzhayoram thazhukunnee\",\"start\":26691,\"end\":28759,\"words\":[{\"text\":\"Puzhayoram \",\"start\":26691,\"end\":27589},{\"text\":\"thazhukunnee\",\"start\":27589,\"end\":28759}]}" +
        "]}";

    // /get?artist=Vineeth Sreenivasan&title=Aluva Puzha: Deezer's line timings.
    private const string AluvaLine =
        "{\"track\":{\"title\":\"Aluva Puzha\",\"artist\":\"Vineeth Sreenivasan\",\"album\":\"Premam\",\"duration\":183}," +
        "\"meta\":{\"source\":{\"id\":\"deezer\",\"name\":\"Deezer\"},\"level\":\"line\"}," +
        "\"lines\":[{\"text\":\"തന്നനം തെന്നി തെന്നി\",\"start\":26820,\"end\":28780},{\"text\":\"ആലുവ പുഴയുടെ\",\"start\":28780,\"end\":31000}]}";

    // /get?artist=P. Jayachandran&title=Anuraga Ganam Pole: Musixmatch, plain text only (LRCLIB had none).
    private const string AnuragaPlain =
        "{\"track\":{\"title\":\"Anuraga Ganam pole (From \\\"Udyogastha\\\")\",\"artist\":\"P. Jayachandran\",\"duration\":229}," +
        "\"meta\":{\"source\":{\"id\":\"musixmatch\",\"name\":\"Musixmatch\"},\"level\":\"none\"}," +
        "\"lines\":[{\"text\":\"ആരു നീ ആരു നീ ദേവതേ\"},{\"text\":\"അനുരാഗ ഗാനം പോലെ\"}]}";

    // /get?artist=Dhanush&title=Rowdy Baby: the Telugu version, credited to the composer.
    private const string RowdyTelugu =
        "{\"track\":{\"title\":\"Rowdy Baby\",\"artist\":\"Yuvan Shankar Raja\",\"album\":\"Maari 2\",\"duration\":281}," +
        "\"meta\":{\"source\":{\"id\":\"deezer\",\"name\":\"Deezer\"},\"level\":\"line\"}," +
        "\"lines\":[{\"text\":\"హే నా నాటుకోడివే\",\"start\":33390,\"end\":36200}]}";

    private static (LrcMuxLyricsService Service, Router Router) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var router = new Router(respond);
        return (new LrcMuxLyricsService(new HttpClient(router)), router);
    }

    [Fact]
    public async Task WordLevelAnswer_BecomesElrc_AndRanksWordSynced()
    {
        var (svc, router) = Make(_ => Ok(MalareWord));
        var r = await svc.SearchLyricsAsync("Vijay Yesudas", "Malare", 316);

        Assert.NotNull(r);
        Assert.Equal("[00:18.15]<00:18.15>Thelimanam <00:19.04>mazhavillin<00:20.24>\n[00:26.69]<00:26.69>Puzhayoram <00:27.58>thazhukunnee<00:28.75>", r!.SyncedLyrics);
        Assert.Equal("Thelimanam mazhavillin\nPuzhayoram thazhukunnee", r.PlainLyrics);
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(r));
        var url = Assert.Single(router.Requests);
        Assert.StartsWith("https://api.lrcmux.dev/get?", url);
        Assert.Contains("title=Malare", url);
        Assert.Contains("artist=Vijay%20Yesudas", url);
        Assert.Contains("duration=316", url);
        Assert.Contains("github.com/heartached/Noctis", Assert.Single(router.UserAgents));
    }

    [Fact]
    public async Task LineLevelAnswer_IsLrc()
    {
        var (svc, _) = Make(_ => Ok(AluvaLine));
        var r = await svc.SearchLyricsAsync("Vineeth Sreenivasan", "Aluva Puzha", 183);
        Assert.Equal("[00:26.82]തന്നനം തെന്നി തെന്നി\n[00:28.78]ആലുവ പുഴയുടെ", r!.SyncedLyrics);
        Assert.Equal(2, LyricsSearchSelector.FormatRank(r));
    }

    [Fact]
    public async Task UnsyncedAnswer_IsPlainLyrics()
    {
        var (svc, _) = Make(_ => Ok(AnuragaPlain));
        var r = await svc.SearchLyricsAsync("P. Jayachandran", "Anuraga Ganam Pole", 229);
        Assert.Null(r!.SyncedLyrics);
        Assert.Equal("ആരു നീ ആരു നീ ദേവതേ\nഅനുരാഗ ഗാനം പോലെ", r.PlainLyrics);
        Assert.Equal(1, LyricsSearchSelector.FormatRank(r));
    }

    [Fact]
    public async Task AnotherArtistsVersion_IsAMiss()
    {
        // Live 11-09: Dhanush's Tamil "Rowdy Baby" came back as the Telugu version, credited to Yuvan Shankar Raja.
        var (svc, _) = Make(_ => Ok(RowdyTelugu));
        Assert.Null(await svc.SearchLyricsAsync("Dhanush", "Rowdy Baby", 281));
    }

    [Fact]
    public async Task WrongLength_IsAMiss()
    {
        var (svc, _) = Make(_ => Ok(AluvaLine));
        Assert.Null(await svc.SearchLyricsAsync("Vineeth Sreenivasan", "Aluva Puzha", 240));
    }

    [Fact]
    public async Task NotFound_IsNull_AndRemembered()
    {
        var (svc, router) = Make(_ => Status(HttpStatusCode.NotFound));
        Assert.Null(await svc.SearchLyricsAsync("M. G. Sreekumar", "Kandu Kandu", 300));
        Assert.Null(await svc.SearchLyricsAsync("M. G. Sreekumar", "Kandu Kandu", 300));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task RateLimited_IsABusyProviderError_AndNotRemembered()
    {
        var (svc, router) = Make(_ => Status(HttpStatusCode.TooManyRequests));
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("A", "B", 200));
        Assert.True(ex.IsBusy);
        Assert.Equal("lrcmux", ex.Provider);
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("A", "B", 200));
        Assert.Equal(2, router.Requests.Count);
    }

    [Fact]
    public async Task UnknownArtist_IsNotSent()
    {
        var (svc, router) = Make(_ => Status(HttpStatusCode.NotFound));
        await svc.SearchLyricsAsync("Unknown Artist", "Song", 0);
        Assert.DoesNotContain("artist=", Assert.Single(router.Requests));
        Assert.DoesNotContain("duration=", router.Requests[0]);
    }

    [Fact]
    public void IsOffByDefault_AndAskedLast_AtItsOwnPace()
    {
        Assert.False(new AppSettings().LrcMuxEnabled);
        var http = new HttpClient(new Router(_ => Status(HttpStatusCode.NotFound)));
        var sources = OnlineLyricsSource.BuiltIns(new MusixmatchService(http, null), new KugouLyricsService(http),
            new YouTubeMusicLyricsService(http), new LrcRedLyricsService(http), new UnisonLyricsService(http), new LrcMuxLyricsService(http));
        var last = sources[^1];
        Assert.Equal(OnlineLyricsSource.LrcMux, last.Name);
        // 60 requests a minute: a bulk run spaces its requests at least a second apart.
        Assert.True(last.MinInterval >= TimeSpan.FromSeconds(1));
        Assert.False(last.IsEnabled(new AppSettings()));
        Assert.True(last.IsEnabled(new AppSettings { LrcMuxEnabled = true }));
    }
}
