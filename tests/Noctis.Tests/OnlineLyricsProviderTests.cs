using System.Net;
using System.Net.Http;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The issue #113 providers (Kugou, Musixmatch, YouTube Music) against trimmed copies of the
/// real responses captured 2026-10-01 — no network. Each keeps the LRCLIB/NetEase contract:
/// a definitive miss is null, a provider failure is <see cref="LyricsProviderException"/>,
/// and a match for a different song (title / artist / duration ±5 s) is a miss.
/// </summary>
public class OnlineLyricsProviderTests
{
    private sealed class Router : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public readonly List<string> Requests = new();
        public readonly List<string> Bodies = new();

        public Router(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.ToString());
                Bodies.Add(body);
            }
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };

    // ── Kugou ──

    // mobileservice.kugou.com/api/v3/search/song for "Adele Hello", trimmed; the live version
    // is first here so the 15 s-shorter cut has to be rejected.
    private const string KugouSongs =
        "{\"status\":1,\"errcode\":0,\"data\":{\"info\":[" +
        "{\"hash\":\"8af8fe687aced13584a08b8ed8fb778c\",\"duration\":280,\"songname\":\"Hello (Live)\",\"singername\":\"Adele\",\"album_name\":\"\"}," +
        "{\"hash\":\"1f62c7d9b83059cc879da895e0ad64cf\",\"duration\":295,\"songname\":\"Hello\",\"singername\":\"Adele\",\"album_name\":\"Hello\"}," +
        "{\"hash\":\"24ef2780115a67df150548e8663a639e\",\"duration\":250,\"songname\":\"Hello (marshmello Remix)\",\"singername\":\"Marshmello、Adele\",\"album_name\":\"HeLLo (marshmello Remix)\"}]}}";

    private const string KugouCandidates =
        "{\"status\":200,\"info\":\"OK\",\"errcode\":200,\"candidates\":[" +
        "{\"id\":\"263915382\",\"accesskey\":\"TESTACCESSKEY\",\"singer\":\"Adele\",\"song\":\"Hello\",\"duration\":295686,\"score\":60,\"krctype\":1}]}";

    private const string KugouKrc =
        "[id:$00000000]\n[ti:Hello]\n[ar:Adele]\n[by:]\n" +
        "[1800,1640]<0,102,0>Hello<102,102,0> <204,102,0>-<306,102,0> <408,102,0>Adele\n" +
        "[6591,3110]<0,2660,0>Hello <2660,150,0>it's <2810,300,0>me\n";

    private static string KugouDownload(string krc) =>
        "{\"status\":200,\"info\":\"OK\",\"error_code\":0,\"fmt\":\"krc\",\"contenttype\":0,\"content\":\"" +
        Convert.ToBase64String(KrcLyricsTests.Encrypt(krc)) + "\",\"id\":\"263915382\"}";

    private static Router KugouRouter(string songs = KugouSongs, string? download = null) => new((req, _) =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/api/v3/search/song")) return Ok(songs);
        if (url.Contains("krcs.kugou.com/search")) return Ok(KugouCandidates);
        if (url.Contains("lyrics.kugou.com/download")) return Ok(download ?? KugouDownload(KugouKrc));
        return Status(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task Kugou_PicksTheMatchingSongByHash_AndReturnsElrc()
    {
        var router = KugouRouter();
        var svc = new KugouLyricsService(new HttpClient(router));

        var result = await svc.SearchLyricsAsync("Adele", "Hello", 296);

        Assert.NotNull(result);
        Assert.Equal("[00:06.59]<00:06.59>Hello <00:09.25>it's <00:09.40>me<00:09.70>", result!.SyncedLyrics);
        Assert.Equal("Hello it's me", result.PlainLyrics?.Trim());
        Assert.Contains(router.Requests, u => u.Contains("krcs.kugou.com") && u.Contains("hash=1f62c7d9b83059cc879da895e0ad64cf"));
        Assert.Contains(router.Requests, u => u.Contains("lyrics.kugou.com") && u.Contains("id=263915382") && u.Contains("fmt=krc"));
    }

    [Fact]
    public async Task Kugou_NoMatchingSong_IsADefinitiveMiss_AndCached()
    {
        var router = KugouRouter();
        var svc = new KugouLyricsService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("Adele", "Someone Like You", 285));
        Assert.Null(await svc.SearchLyricsAsync("Adele", "Someone Like You", 285));
        Assert.Single(router.Requests); // one song search, then the cached miss
    }

    [Fact]
    public async Task Kugou_ServerError_ThrowsProviderError()
    {
        var svc = new KugouLyricsService(new HttpClient(new Router((_, _) => Status(HttpStatusCode.BadGateway))));
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Adele", "Hello", 296));
        Assert.Equal("Kugou", ex.Provider);
    }

    [Fact]
    public async Task Kugou_CorruptKrc_ThrowsProviderError()
    {
        var corrupt = "{\"status\":200,\"content\":\"" + Convert.ToBase64String("not krc at all"u8.ToArray()) + "\"}";
        var svc = new KugouLyricsService(new HttpClient(KugouRouter(download: corrupt)));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Adele", "Hello", 296));
    }

    // ── Musixmatch ──

    private const string MxmToken = "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"user_token\":\"test-token\"}}}";
    private const string MxmRenew = "{\"message\":{\"header\":{\"status_code\":401,\"hint\":\"renew\"},\"body\":{\"macro_calls\":{}}}}";

    // macro.subtitles.get (subtitle_format=lrc) for Coldplay - Yellow, trimmed to two lines.
    private const string MxmMacroYellow =
        "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"macro_calls\":{" +
        "\"track.lyrics.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"lyrics\":{\"lyrics_id\":48665822,\"restricted\":0,\"instrumental\":0,\"lyrics_body\":\"Look at the stars\\nLook how they shine for you\",\"lyrics_language\":\"en\"}}}}," +
        "\"track.subtitles.get\":{\"message\":{\"header\":{\"status_code\":200,\"available\":1},\"body\":{\"subtitle_list\":[{\"subtitle\":{\"subtitle_id\":53954672,\"restricted\":0,\"subtitle_body\":\"[00:33.37] Look at the stars\\n[00:36.16] Look how they shine for you\",\"subtitle_length\":267}}]}}}," +
        "\"matcher.track.get\":{\"message\":{\"header\":{\"status_code\":200,\"confidence\":1000},\"body\":{\"track\":{\"track_id\":461806032,\"track_name\":\"Yellow\",\"track_length\":267,\"commontrack_id\":288342521,\"instrumental\":0,\"has_lyrics\":1,\"has_subtitles\":1,\"has_richsync\":1,\"album_name\":\"Parachutes\",\"artist_name\":\"Coldplay\",\"restricted\":0}}}}}}}}";

    // The decoy the desktop app id was served for every query (wrong song, scrambled text).
    private const string MxmMacroDecoy =
        "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"macro_calls\":{" +
        "\"track.lyrics.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"lyrics\":{\"restricted\":0,\"lyrics_body\":\"Wob gopini den\\nTefe woxica fero\"}}}}," +
        "\"track.subtitles.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"subtitle_list\":[{\"subtitle\":{\"restricted\":0,\"subtitle_body\":\"[00:12.00]Wob gopini den\"}}]}}}," +
        "\"matcher.track.get\":{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"track\":{\"track_name\":\"NOKIA\",\"artist_name\":\"Drake\",\"commontrack_id\":124185591,\"has_richsync\":1,\"instrumental\":0}}}}}}}}";

    private const string MxmMacroMiss =
        "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"macro_calls\":{" +
        "\"matcher.track.get\":{\"message\":{\"header\":{\"status_code\":404}}}," +
        "\"track.subtitles.get\":{\"message\":{\"header\":{\"status_code\":404}}}}}}}";

    // track.richsync.get, trimmed to its first two lines (Daft Punk - One More Time's real body shape).
    private const string RichsyncBody =
        "[{\"ts\": 30.37, \"te\": 31.83, \"l\": [{\"c\": \"One\", \"o\": 0}, {\"c\": \" \", \"o\": 0.018}, {\"c\": \"more\", \"o\": 0.424}, {\"c\": \" \", \"o\": 0.446}, {\"c\": \"time\", \"o\": 0.892}], \"x\": \"One more time\"}, " +
        "{\"ts\": 45.9799, \"te\": 47.31, \"l\": [{\"c\": \"One\", \"o\": 0}, {\"c\": \" \", \"o\": 0.006}, {\"c\": \"more\", \"o\": 0.391}, {\"c\": \" \", \"o\": 0.403}, {\"c\": \"time\", \"o\": 0.835}], \"x\": \"One more time\"}]";

    private static string MxmRichsync(string body) =>
        "{\"message\":{\"header\":{\"status_code\":200,\"available\":2},\"body\":{\"richsync\":{\"richsync_id\":7361800,\"restricted\":0,\"richsync_body\":" +
        System.Text.Json.JsonSerializer.Serialize(body) + ",\"richsync_length\":320}}}}";

    private static Router MxmRouter(Func<string> macro, Func<HttpResponseMessage>? richsync = null) => new((req, _) =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("token.get")) return Ok(MxmToken);
        if (url.Contains("macro.subtitles.get")) return Ok(macro());
        if (url.Contains("track.richsync.get")) return richsync?.Invoke() ?? Ok(MxmRichsync(RichsyncBody));
        return Status(HttpStatusCode.NotFound);
    });

    [Fact]
    public void Musixmatch_RichsyncToElrc_TimesWordsFromTheLineStart()
    {
        Assert.Equal(
            "[00:30.37]<00:30.37>One <00:30.79>more <00:31.26>time<00:31.83>\n" +
            "[00:45.98]<00:45.98>One <00:46.37>more <00:46.81>time<00:47.31>",
            MusixmatchLyrics.RichsyncToElrc(RichsyncBody));
    }

    [Fact]
    public async Task Musixmatch_UsesRichsyncWordTimings_WhenTheMatchHasThem()
    {
        var router = MxmRouter(() => MxmMacroYellow);
        var svc = new MusixmatchService(new HttpClient(router), tokenPath: null);

        var result = await svc.SearchLyricsAsync("Coldplay", "Yellow", 266);

        Assert.NotNull(result);
        Assert.StartsWith("[00:30.37]<00:30.37>One ", result!.SyncedLyrics);
        Assert.Equal("Look at the stars\nLook how they shine for you", result.PlainLyrics);
        Assert.Contains(router.Requests, u => u.Contains("track.richsync.get") && u.Contains("commontrack_id=288342521"));
        Assert.All(router.Requests.Where(u => !u.Contains("token.get")), u => Assert.Contains("usertoken=test-token", u));
    }

    [Fact]
    public async Task Musixmatch_RichsyncFailure_KeepsTheLineSyncedSubtitle()
    {
        var svc = new MusixmatchService(new HttpClient(MxmRouter(() => MxmMacroYellow, () => Status(HttpStatusCode.InternalServerError))), tokenPath: null);

        var result = await svc.SearchLyricsAsync("Coldplay", "Yellow", 266);

        Assert.Equal("[00:33.37] Look at the stars\n[00:36.16] Look how they shine for you", result?.SyncedLyrics);
    }

    [Fact]
    public async Task Musixmatch_DecoyOfADifferentSong_IsAMiss_NotSaved()
    {
        var router = MxmRouter(() => MxmMacroDecoy);
        var svc = new MusixmatchService(new HttpClient(router), tokenPath: null);

        Assert.Null(await svc.SearchLyricsAsync("Adele", "Hello", 296));
        Assert.DoesNotContain(router.Requests, u => u.Contains("track.richsync.get"));
    }

    [Fact]
    public async Task Musixmatch_NoMatch_IsADefinitiveMiss()
    {
        var svc = new MusixmatchService(new HttpClient(MxmRouter(() => MxmMacroMiss)), tokenPath: null);
        Assert.Null(await svc.SearchLyricsAsync("Nobody", "Nothing", 200));
    }

    [Fact]
    public async Task Musixmatch_ExpiredToken_IsRenewedOnce()
    {
        var macroCalls = 0;
        var router = MxmRouter(() => Interlocked.Increment(ref macroCalls) == 1 ? MxmRenew : MxmMacroYellow);
        var svc = new MusixmatchService(new HttpClient(router), tokenPath: null);

        var result = await svc.SearchLyricsAsync("Coldplay", "Yellow", 266);

        Assert.NotNull(result);
        Assert.Equal(2, router.Requests.Count(u => u.Contains("token.get")));
    }

    [Fact]
    public async Task Musixmatch_Token_IsKeptForTheNextRun()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"noctis-mxm-{Guid.NewGuid():N}");
        try
        {
            var first = MxmRouter(() => MxmMacroYellow);
            await new MusixmatchService(new HttpClient(first), tokenPath).SearchLyricsAsync("Coldplay", "Yellow", 266);
            var second = MxmRouter(() => MxmMacroYellow);
            await new MusixmatchService(new HttpClient(second), tokenPath).SearchLyricsAsync("Coldplay", "Yellow", 266);

            Assert.Single(first.Requests, u => u.Contains("token.get"));
            Assert.DoesNotContain(second.Requests, u => u.Contains("token.get"));
        }
        finally { try { File.Delete(tokenPath); } catch { } }
    }

    [Fact]
    public async Task Musixmatch_CaptchaOnTokenGet_BacksOffInsteadOfAskingAgain()
    {
        var router = new Router((req, _) => req.RequestUri!.ToString().Contains("token.get")
            ? Ok("{\"message\":{\"header\":{\"status_code\":401,\"hint\":\"captcha\"}}}")
            : Ok(MxmMacroYellow));
        var svc = new MusixmatchService(new HttpClient(router), tokenPath: null);

        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Coldplay", "Yellow", 266));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Coldplay", "Yellow", 266));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task Musixmatch_TokenRefusedTwice_ThrowsProviderError()
    {
        var svc = new MusixmatchService(new HttpClient(MxmRouter(() => MxmRenew)), tokenPath: null);
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Coldplay", "Yellow", 266));
        Assert.Equal("Musixmatch", ex.Provider);
    }

    // ── YouTube Music ──

    private static string Row(string videoId, string title, string subtitle) =>
        "{\"musicResponsiveListItemRenderer\":{\"playlistItemData\":{\"videoId\":\"" + videoId + "\"},\"flexColumns\":[" +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + title + "\"}]}}}," +
        "{\"musicResponsiveListItemFlexColumnRenderer\":{\"text\":{\"runs\":[{\"text\":\"" + subtitle + "\"}]}}}]}}";

    // Songs search for "Adele Hello", trimmed to its renderer rows; the other song is first.
    private static readonly string YtmSearch =
        "{\"contents\":{\"tabbedSearchResultsRenderer\":{\"tabs\":[{\"tabRenderer\":{\"content\":{\"sectionListRenderer\":{\"contents\":[{\"musicShelfRenderer\":{\"contents\":[" +
        Row("x9Qv47HpEqw", "Rolling in the Deep", "Adele • 21 • 3:49") + "," +
        Row("yjo_aXygRDI", "Hello", "Adele • 25 • 4:56") + "]}}]}}}}]}}}";

    private const string YtmNext =
        "{\"contents\":{\"singleColumnMusicWatchNextResultsRenderer\":{\"tabbedRenderer\":{\"watchNextTabbedResultsRenderer\":{\"tabs\":[" +
        "{\"tabRenderer\":{\"title\":\"Up next\"}}," +
        "{\"tabRenderer\":{\"title\":\"Lyrics\",\"endpoint\":{\"browseEndpoint\":{\"browseId\":\"MPLYt_5HsqbBKw813-1\"}}}}]}}}}}";

    private const string YtmTimed =
        "{\"contents\":{\"elementRenderer\":{\"newElement\":{\"type\":{\"componentType\":{\"model\":{\"timedLyricsModel\":{\"lyricsData\":{\"timedLyricsData\":[" +
        "{\"lyricLine\":\"♪\",\"cueRange\":{\"startTimeMilliseconds\":\"0\",\"endTimeMilliseconds\":\"6380\"}}," +
        "{\"lyricLine\":\"Hello, it's me\",\"cueRange\":{\"startTimeMilliseconds\":\"6380\",\"endTimeMilliseconds\":\"11950\"}}," +
        "{\"lyricLine\":\"I was wondering\",\"cueRange\":{\"startTimeMilliseconds\":\"11950\",\"endTimeMilliseconds\":\"17950\"}}]," +
        "\"sourceMessage\":\"Source: Musixmatch\"}}}}}}}}}";

    private const string YtmPlain =
        "{\"contents\":{\"sectionListRenderer\":{\"contents\":[{\"musicDescriptionShelfRenderer\":{\"description\":{\"runs\":[{\"text\":\"Hello, it's me\\nI was wondering\"}]}," +
        "\"footer\":{\"runs\":[{\"text\":\"Source: Musixmatch\"}]}}}]}}}";

    private static Router YtmRouter(string browse, string next = YtmNext) => new((req, _) =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/youtubei/v1/search")) return Ok(YtmSearch);
        if (url.Contains("/youtubei/v1/next")) return Ok(next);
        if (url.Contains("/youtubei/v1/browse")) return Ok(browse);
        return Status(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task YouTubeMusic_TimedLyrics_BecomeLineSyncedLrc()
    {
        var router = YtmRouter(YtmTimed);
        var svc = new YouTubeMusicLyricsService(new HttpClient(router));

        var result = await svc.SearchLyricsAsync("Adele", "Hello", 295);

        Assert.NotNull(result);
        Assert.Equal("[00:06.38]Hello, it's me\n[00:11.95]I was wondering", result!.SyncedLyrics);
        Assert.Contains("\"videoId\":\"yjo_aXygRDI\"", router.Bodies[1]);
        Assert.Contains("WEB_REMIX", router.Bodies[0]);
        Assert.Contains("ANDROID_MUSIC", router.Bodies[2]);
        Assert.Contains("MPLYt_5HsqbBKw813-1", router.Bodies[2]);
    }

    [Fact]
    public async Task YouTubeMusic_PlainShelf_WhenThereIsNoTiming()
    {
        var svc = new YouTubeMusicLyricsService(new HttpClient(YtmRouter(YtmPlain)));

        var result = await svc.SearchLyricsAsync("Adele", "Hello", 295);

        Assert.Null(result?.SyncedLyrics);
        Assert.Equal("Hello, it's me\nI was wondering", result?.PlainLyrics);
    }

    [Fact]
    public async Task YouTubeMusic_NoLyricsTab_IsADefinitiveMiss()
    {
        var router = YtmRouter(YtmTimed, next: "{\"contents\":{}}");
        var svc = new YouTubeMusicLyricsService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("Adele", "Hello", 295));
        Assert.Equal(2, router.Requests.Count); // no browse without a lyrics tab
    }

    [Fact]
    public async Task YouTubeMusic_RetiredClientVersion_ThrowsProviderError()
    {
        var svc = new YouTubeMusicLyricsService(new HttpClient(new Router((_, _) => Status(HttpStatusCode.BadRequest))));
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Adele", "Hello", 295));
        Assert.Equal("YouTube Music", ex.Provider);
    }
}
