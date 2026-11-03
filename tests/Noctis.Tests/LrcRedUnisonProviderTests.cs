using System.Net;
using System.Net.Http;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// lrc.red and Unison (Discord request, nutf!xx) against trimmed copies of the real responses
/// captured 2026-10-17 — no network. Same contract as the issue #113 providers: a definitive miss
/// is null, a provider failure is <see cref="LyricsProviderException"/>, a different song (title /
/// artist / duration ±5 s) is a miss — and their TTML keeps duet voices and background vocals.
/// </summary>
public class LrcRedUnisonProviderTests
{
    private sealed class Router : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<string> Requests = new();
        public readonly List<string?> UserAgents = new();

        public Router(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.AbsoluteUri);
                UserAgents.Add(request.Headers.UserAgent.ToString());
            }
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };

    // ── lrc.red ──

    // lrc.red/api/v1?track=Die With A Smile&artist=Lady Gaga&duration=252, trimmed to four of its
    // twenty results; the acoustic cut (same length, line-synced) is moved first so the exact
    // title has to win over the lookup's order.
    private const string LrcRedLookup =
        "{\"results\":[" +
        "{\"album_name\":\"Die With A Smile - Single\",\"artist_name\":\"Lady Gaga, Bruno Mars\",\"duration\":252,\"id\":\"USUM72413062\",\"isrc\":\"USUM72413062\",\"lyricsUrl\":\"https://lrc.red/s/USUM72413062.ttml\",\"timing_type\":\"line\",\"track_name\":\"Die With A Smile (Acoustic)\"}," +
        "{\"album_name\":\"MAYHEM\",\"artist_name\":\"Lady Gaga, Bruno Mars\",\"duration\":252,\"id\":\"USUM72409273\",\"isrc\":\"USUM72409273\",\"lyricsUrl\":\"https://lrc.red/s/USUM72409273.ttml\",\"timing_type\":\"word\",\"track_name\":\"Die With A Smile\"}," +
        "{\"album_name\":\"Die With A Smile - Single\",\"artist_name\":\"Lady Gaga, Bruno Mars\",\"duration\":280,\"id\":\"USUM72412854\",\"isrc\":\"USUM72412854\",\"lyricsUrl\":\"https://lrc.red/s/USUM72412854.ttml\",\"timing_type\":\"line\",\"track_name\":\"Die With A Smile (Live in Las Vegas)\"}," +
        "{\"album_name\":\"Apple Music Live: MAYHEM Requiem\",\"artist_name\":\"Lady Gaga, Bruno Mars\",\"duration\":368,\"id\":\"USUM72603847\",\"isrc\":\"USUM72603847\",\"lyricsUrl\":\"https://lrc.red/s/USUM72603847.ttml\",\"timing_type\":\"word\",\"track_name\":\"Die With A Smile (Apple Music Live)\"}" +
        "],\"source\":\"HIT-LRC-RED\",\"total\":20}";

    private const string LrcRedMiss = "{\"results\":[],\"source\":\"MISS-LRC-RED\",\"total\":0}";

    // lrc.red/s/USUM72409273.ttml: its head and three of its 50 lines — Lady Gaga (v2), Bruno
    // Mars (v1) and both together (the v1000 group).
    private const string LrcRedTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:lrc=\"http://lrc.red/lyric-ttml-internal\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" lrc:timing=\"Word\" xml:lang=\"en\">" +
        "<head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/><ttm:agent type=\"person\" xml:id=\"v2\"/><ttm:agent type=\"group\" xml:id=\"v1000\"/>" +
        "<sourceMetadata xmlns=\"http://lrc.red/lyric-ttml-internal\" leadingSilence=\"0.160\"><translations/><songwriters><songwriter>Lady Gaga</songwriter></songwriters></sourceMetadata></metadata></head>" +
        "<body dur=\"4:11.668\" ttm:agent=\"v2\">" +
        "<div begin=\"3.432\" end=\"6.045\" lrc:songPart=\"Intro\"><p begin=\"3.432\" end=\"6.045\" lrc:key=\"L1\" ttm:agent=\"v2\"><span begin=\"3.432\" end=\"4.763\">Ooh,</span> <span begin=\"4.769\" end=\"6.045\">ooh</span></p></div>" +
        "<div begin=\"1:25.193\" end=\"1:41.240\" lrc:songPart=\"Verse\"><p begin=\"1:25.193\" end=\"1:32.431\" lrc:key=\"L19\" ttm:agent=\"v1\"><span begin=\"1:25.193\" end=\"1:26.734\">Ooh,</span> <span begin=\"1:26.734\" end=\"1:28.071\">lost,</span> <span begin=\"1:28.826\" end=\"1:29.293\">lost</span> <span begin=\"1:29.293\" end=\"1:29.960\">in</span> <span begin=\"1:29.960\" end=\"1:30.128\">the</span> <span begin=\"1:30.128\" end=\"1:30.461\">words</span> <span begin=\"1:30.461\" end=\"1:30.861\">that</span> <span begin=\"1:30.861\" end=\"1:31.226\">we</span> <span begin=\"1:31.226\" end=\"1:32.431\">scream</span></p>" +
        "<p begin=\"1:37.994\" end=\"1:41.240\" lrc:key=\"L21\" ttm:agent=\"v1000\"><span begin=\"1:37.994\" end=\"1:38.213\">'Cause</span> <span begin=\"1:38.213\" end=\"1:38.354\">you</span> <span begin=\"1:38.354\" end=\"1:38.805\">already</span> <span begin=\"1:38.805\" end=\"1:39.253\">know</span> <span begin=\"1:39.253\" end=\"1:39.460\">what</span> <span begin=\"1:39.460\" end=\"1:39.673\">you</span> <span begin=\"1:39.673\" end=\"1:40.255\">mean</span> <span begin=\"1:40.255\" end=\"1:40.437\">to</span> <span begin=\"1:40.437\" end=\"1:41.240\">me</span></p></div>" +
        "</body></tt>";

    // lrc.red/s/TCAET2006097.ttml (Coldplay "Paradise", timing_type "none"), trimmed: no timing at all.
    private const string LrcRedUntimedTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:lrc=\"http://lrc.red/lyric-ttml-internal\" lrc:timing=\"None\" xml:lang=\"en\"><head><metadata></metadata></head>" +
        "<body><div><p>When she was just a girl</p><p>She expected the world</p></div></body></tt>";

    private static Router LrcRedRouter(string lookup = LrcRedLookup, string ttml = LrcRedTtml) => new(req =>
    {
        var url = req.RequestUri!.ToString();
        if (url.StartsWith("https://lrc.red/api/v1?")) return Ok(lookup);
        if (url.StartsWith("https://lrc.red/s/")) return Ok(ttml);
        return Status(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task LrcRed_PicksTheExactTitleWordSyncedRecording_AndKeepsDuetVoices()
    {
        var router = LrcRedRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var result = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile", 251.693);

        Assert.NotNull(result);
        Assert.Equal(2, router.Requests.Count);
        Assert.Equal("https://lrc.red/api/v1?track=Die%20With%20A%20Smile&artist=Lady%20Gaga&duration=252", router.Requests[0]);
        Assert.Equal("https://lrc.red/s/USUM72409273.ttml", router.Requests[1]);
        Assert.All(router.UserAgents, ua => Assert.StartsWith("Noctis", ua));
        Assert.Equal("Die With A Smile", result!.TrackName);
        Assert.Equal("MAYHEM", result.AlbumName);
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(result));

        var lines = LrcParser.Parse(result.SyncedLyrics!).Where(l => l.IsSynced).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(LyricVoice.Voice2, lines[0].Voice); // Lady Gaga, the second declared singer
        Assert.Equal(LyricVoice.Default, lines[1].Voice); // Bruno Mars
        Assert.Equal(LyricVoice.Group, lines[2].Voice); // both
        Assert.Equal(9, lines[1].Words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(85190), lines[1].Timestamp); // 1:25.193 at LRC's centiseconds
        Assert.Contains("lost, lost in the words that we scream", result.PlainLyrics);
    }

    [Fact]
    public void LrcRed_LookupQuery_TitleSearch_WhenTheArtistIsThePlaceholder()
    {
        Assert.Equal("https://lrc.red/api/v1?q=Paradise", LrcRedLyricsService.LookupQuery("Unknown Artist", "Paradise", 279));
        Assert.Equal("https://lrc.red/api/v1?track=Paradise&artist=Coldplay", LrcRedLyricsService.LookupQuery("Coldplay", "Paradise", 0));
    }

    [Fact]
    public void LrcRed_PickSong_RejectsOtherSongs_AndIdsThatAreNotIsrcs()
    {
        // Only the live cut (28 s longer) and the Apple Music Live one: neither is this recording.
        Assert.Null(LrcRedLyricsService.PickSong(LrcRedLookup.Replace("\"duration\":252", "\"duration\":200"),
            "Lady Gaga", "Die With A Smile", 251.693));
        Assert.Null(LrcRedLyricsService.PickSong(LrcRedLookup, "Adele", "Hello", 295));
        // The file URL is built from the ISRC, so anything else is skipped.
        var spoofed = "{\"results\":[{\"artist_name\":\"Coldplay\",\"duration\":279,\"isrc\":\"../../x?y=1\",\"timing_type\":\"word\",\"track_name\":\"Paradise\"}]}";
        Assert.Null(LrcRedLyricsService.PickSong(spoofed, "Coldplay", "Paradise", 279));
    }

    [Theory]
    [InlineData("Lady Gaga & Bruno Mars")]
    [InlineData("Bruno Mars; Lady Gaga")]
    [InlineData("Lady Gaga feat. Bruno Mars")]
    [InlineData("Lady Gaga/Bruno Mars")]
    public void LrcRed_PickSong_DuetCreditedWithAnotherSeparator_IsTheSameSong(string localArtist)
    {
        // lrc.red writes "Lady Gaga, Bruno Mars"; a library tag joins the same two singers its own
        // way. Containment either way failed, so the duet had no lrc.red lyrics at all.
        var song = LrcRedLyricsService.PickSong(LrcRedLookup, localArtist, "Die With A Smile", 251.693);
        Assert.Equal("USUM72409273", song?.Isrc);
    }

    [Theory]
    [InlineData("ACRU, DANDARA")]
    [InlineData("Acru & DANDARA")]
    [InlineData("Acru")]
    [InlineData("DANDARA")]
    public void LrcRed_PickSong_AcruDandara_EveryCreditTheReportTried_IsTheSong(string localArtist)
    {
        // The lookup's real row (curl 2026-10-24, /api/v1?track=PENSANDO%20EN%20MI&artist=ACRU%2C%20DANDARA&duration=217),
        // and the same row as CÁPSULA — which lrc.red does not have (its ISRC UYB282651340 is a 404).
        const string pensando = "{\"results\":[{\"album_name\":\"GÉMINIS - EP\",\"artist_name\":\"Acru & DANDARA\",\"duration\":217,\"id\":\"UYB282620945\",\"isrc\":\"UYB282620945\",\"lyricsUrl\":\"https://lrc.red/s/UYB282620945.ttml\",\"timing_type\":\"line\",\"track_name\":\"PENSANDO EN MI\"}],\"source\":\"HIT-LRC-RED\",\"total\":1}";
        Assert.Equal("UYB282620945", LrcRedLyricsService.PickSong(pensando, localArtist, "PENSANDO EN MI", 217.288)?.Isrc);
        var capsula = pensando.Replace("PENSANDO EN MI", "CÁPSULA").Replace("\"duration\":217", "\"duration\":198");
        Assert.Equal("UYB282620945", LrcRedLyricsService.PickSong(capsula, localArtist, "CÁPSULA", 198.27)?.Isrc);
    }

    [Fact]
    public void LrcRed_PickSong_AnotherArtistSharingNoSinger_IsStillAMiss()
    {
        Assert.Null(LrcRedLyricsService.PickSong(LrcRedLookup, "Bruno Major & Lady A", "Die With A Smile", 251.693));
        Assert.Null(LrcRedLyricsService.PickSong(LrcRedLookup, "Bruno, Gaga", "Die With A Smile", 251.693));
    }

    [Fact]
    public async Task LrcRed_NoMatch_IsADefinitiveMiss_AndCached()
    {
        var router = LrcRedRouter(lookup: LrcRedMiss);
        var svc = new LrcRedLyricsService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("Nobody", "Nothing", 100));
        Assert.Null(await svc.SearchLyricsAsync("Nobody", "Nothing", 100));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task LrcRed_MissingTtmlFile_IsAMiss()
    {
        var router = new Router(req => req.RequestUri!.AbsolutePath.StartsWith("/api/") ? Ok(LrcRedLookup) : Status(HttpStatusCode.NotFound));
        var svc = new LrcRedLyricsService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile", 252));
    }

    [Fact]
    public async Task LrcRed_UntimedTtml_IsPlainLyrics()
    {
        var lookup = "{\"results\":[{\"album_name\":\"Love Again\",\"artist_name\":\"Coldplay\",\"duration\":235,\"id\":\"TCAET2006097\",\"isrc\":\"TCAET2006097\",\"timing_type\":\"none\",\"track_name\":\"Paradise\"}],\"total\":1}";
        var svc = new LrcRedLyricsService(new HttpClient(LrcRedRouter(lookup, LrcRedUntimedTtml)));

        var result = await svc.SearchLyricsAsync("Coldplay", "Paradise", 235);

        Assert.NotNull(result);
        Assert.Null(result!.SyncedLyrics);
        Assert.Equal("When she was just a girl\nShe expected the world", result.PlainLyrics);
        Assert.Equal(1, LyricsSearchSelector.FormatRank(result));
    }

    [Fact]
    public async Task LrcRed_ServerErrorOrBrokenTtml_ThrowsProviderError_Uncached()
    {
        var down = new Router(_ => Status(HttpStatusCode.BadGateway));
        var svc = new LrcRedLyricsService(new HttpClient(down));
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile", 252));
        Assert.Equal("lrc.red", ex.Provider);
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile", 252));
        Assert.Equal(2, down.Requests.Count);

        var broken = new LrcRedLyricsService(new HttpClient(LrcRedRouter(ttml: "<tt><body><p>")));
        await Assert.ThrowsAsync<LyricsProviderException>(() => broken.SearchLyricsAsync("Lady Gaga", "Die With A Smile", 252));
    }

    // ── Unison ──

    // unison.boidu.dev/lyrics/search?q=ADÉLA Ain't In LA, trimmed to three of its hits (submitter
    // objects dropped). Two versions of the song: the 219 s one is a longer video.
    private const string UnisonSearch =
        "{\"success\":true,\"data\":[" +
        "{\"id\":7514,\"videoId\":\"Wf0Zq4_0xQ8\",\"song\":\"Ain't In LA\",\"artist\":\"ADÉLA\",\"album\":\"PRIMA\",\"duration\":219,\"format\":\"ttml\",\"language\":\"en\",\"syncType\":\"richsync\",\"score\":3,\"effectiveScore\":1,\"voteCount\":3,\"confidence\":\"low\",\"matchScore\":1}," +
        "{\"id\":4688,\"videoId\":\"E2gPsY36uR0\",\"song\":\"Ain't In LA\",\"artist\":\"ADÉLA\",\"album\":\"PRIMA\",\"isrc\":\"USUM72605978\",\"duration\":185,\"format\":\"ttml\",\"language\":\"en\",\"syncType\":\"richsync\",\"score\":28,\"effectiveScore\":1,\"voteCount\":28,\"confidence\":\"medium\",\"matchScore\":1}," +
        "{\"id\":8687,\"videoId\":\"h-zuG75Ds-w\",\"song\":\"Nicole Kidman\",\"artist\":\"ADÉLA\",\"album\":\"PRIMA\",\"duration\":182,\"format\":\"ttml\",\"language\":\"en\",\"syncType\":\"richsync\",\"score\":11,\"effectiveScore\":1,\"voteCount\":11,\"confidence\":\"medium\",\"matchScore\":0.4}" +
        "]}";

    // unison.boidu.dev/lyrics/4688, its TTML trimmed to three of 53 lines: a lead line with a
    // background "(Ah-ah-ah)", and a line of the other singer.
    private static string UnisonEntry(string format = "ttml", string? lyrics = null) =>
        "{\"success\":true,\"data\":{\"id\":4688,\"videoId\":\"E2gPsY36uR0\",\"song\":\"Ain't In LA\",\"artist\":\"ADÉLA\",\"album\":\"PRIMA\",\"isrc\":\"USUM72605978\"," +
        "\"lyrics\":" + System.Text.Json.JsonSerializer.Serialize(lyrics ?? UnisonTtml) + ",\"format\":\"" + format + "\",\"language\":\"en\",\"syncType\":\"richsync\",\"score\":28,\"voteCount\":28,\"confidence\":\"medium\",\"hidden\":false}}";

    private const string UnisonTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" ttp:timeBase=\"media\" xml:lang=\"en\" itunes:timing=\"Word\">" +
        "<head><metadata><ttm:title>Ain't In LA</ttm:title><ttm:agent xml:id=\"v1\" type=\"person\"><ttm:name>Lead</ttm:name></ttm:agent><ttm:agent xml:id=\"v2000\" type=\"other\"><ttm:name>Chorus</ttm:name></ttm:agent><ttm:agent xml:id=\"v2\" type=\"person\"><ttm:name>Extra Vocals</ttm:name></ttm:agent></metadata></head>" +
        "<body dur=\"3:05.216\"><div>" +
        "<p begin=\"0:02.725\" end=\"0:06.320\" ttm:agent=\"v2\"><span begin=\"0:02.725\" end=\"0:02.932\">Back</span> <span begin=\"0:02.932\" end=\"0:03.131\">it</span> <span begin=\"0:03.131\" end=\"0:03.729\">up,</span> <span begin=\"0:03.729\" end=\"0:04.519\">subwoofers</span> <span begin=\"0:04.519\" end=\"0:04.727\">in</span> <span begin=\"0:04.727\" end=\"0:04.925\">the</span> <span begin=\"0:04.925\" end=\"0:05.323\">trunk,</span> <span begin=\"0:05.323\" end=\"0:05.522\">and</span> <span begin=\"0:05.522\" end=\"0:06.320\">the</span></p>" +
        "<p begin=\"0:16.325\" end=\"0:21.261\" ttm:agent=\"v2\"><span begin=\"0:16.678\" end=\"0:16.878\">Back</span> <span begin=\"0:16.878\" end=\"0:17.076\">it</span> <span begin=\"0:17.076\" end=\"0:17.477\">up,</span> <span begin=\"0:17.477\" end=\"0:18.273\">subwoofers</span> <span begin=\"0:18.273\" end=\"0:18.472\">in</span> <span begin=\"0:18.472\" end=\"0:18.870\">the</span> <span begin=\"0:18.870\" end=\"0:19.268\">trunk,</span> <span begin=\"0:19.268\" end=\"0:19.468\">and</span> <span begin=\"0:19.468\" end=\"0:20.065\">the</span><span ttm:role=\"x-bg\"><span begin=\"0:16.325\" end=\"0:21.261\">(Ah-ah-ah)</span></span></p>" +
        "<p begin=\"0:30.025\" end=\"0:33.413\" ttm:agent=\"v1\"><span begin=\"0:30.025\" end=\"0:30.425\">She</span> <span begin=\"0:30.425\" end=\"0:30.625\">shares</span> <span begin=\"0:30.625\" end=\"0:30.834\">a</span> <span begin=\"0:30.834\" end=\"0:31.413\">room</span> <span begin=\"0:31.413\" end=\"0:31.619\">with</span> <span begin=\"0:31.619\" end=\"0:31.819\">her</span> <span begin=\"0:31.819\" end=\"0:32.608\">younger</span> <span begin=\"0:32.608\" end=\"0:33.413\">brother</span></p>" +
        "</div></body></tt>";

    private static Router UnisonRouter(string search = UnisonSearch, Func<HttpResponseMessage>? entry = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/lyrics/search") return Ok(search);
        if (path == "/lyrics/4688") return entry?.Invoke() ?? Ok(UnisonEntry());
        return Status(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task Unison_PicksTheVersionOfThisLength_AndKeepsBackgroundVocalsAndVoices()
    {
        var router = UnisonRouter();
        var svc = new UnisonLyricsService(new HttpClient(router));

        var result = await svc.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185.4);

        Assert.NotNull(result);
        Assert.Equal(2, router.Requests.Count);
        Assert.StartsWith("https://unison.boidu.dev/lyrics/search?q=AD%C3%89LA%20Ain%27t%20In%20LA", router.Requests[0]);
        Assert.Equal("https://unison.boidu.dev/lyrics/4688", router.Requests[1]);
        Assert.All(router.UserAgents, ua => Assert.StartsWith("Noctis", ua));
        Assert.Equal("PRIMA", result!.AlbumName);
        Assert.Equal(185, result.Duration); // from the search hit: the entry has none
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(result));
        Assert.Contains("[bg: ", result.SyncedLyrics);

        var lines = LrcParser.Parse(result.SyncedLyrics!).Where(l => l.IsSynced).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("(Ah-ah-ah)", string.Concat(lines[1].BackgroundWords!.Select(w => w.Text)).Trim());
        Assert.NotEqual(lines[0].Voice, lines[2].Voice); // the two singers stay apart
    }

    [Fact]
    public async Task Unison_OnlyALongerVideo_IsAMiss_AndCached()
    {
        var router = UnisonRouter(search: UnisonSearch.Replace("\"duration\":185", "\"duration\":230"));
        var svc = new UnisonLyricsService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185.4));
        Assert.Null(await svc.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185.4));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task Unison_NotFoundAndEmptySearch_AreMisses()
    {
        var gone = new UnisonLyricsService(new HttpClient(UnisonRouter(entry: () => Status(HttpStatusCode.NotFound))));
        Assert.Null(await gone.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185));

        var empty = new UnisonLyricsService(new HttpClient(UnisonRouter(search: "{\"success\":true,\"data\":[]}")));
        Assert.Null(await empty.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185));
    }

    [Fact]
    public async Task Unison_LrcVersion_IsKeptAsSynced()
    {
        const string lrc = "[00:02.72]Back it up, subwoofers in the trunk, and the\n[00:06.32]Gemstones in the teeth go dumb, and the";
        var svc = new UnisonLyricsService(new HttpClient(UnisonRouter(entry: () => Ok(UnisonEntry("lrc", lrc)))));

        var result = await svc.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185);

        Assert.Equal(lrc, result!.SyncedLyrics);
        Assert.Equal(2, LyricsSearchSelector.FormatRank(result));
    }

    [Fact]
    public async Task Unison_RateLimited_ThrowsProviderError()
    {
        var svc = new UnisonLyricsService(new HttpClient(new Router(_ => Status(HttpStatusCode.TooManyRequests))));
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("ADÉLA", "Ain't In LA", 185));
        Assert.Equal("Unison", ex.Provider);
    }

    // ── Registration ──

    [Fact]
    public void BuiltIns_RankTheTtmlSourcesAfterMusixmatch_AndBeforeKugou()
    {
        var http = new HttpClient(new Router(_ => Status(HttpStatusCode.NotFound)));
        var sources = OnlineLyricsSource.BuiltIns(new MusixmatchService(http, null), new KugouLyricsService(http),
            new YouTubeMusicLyricsService(http), new LrcRedLyricsService(http), new UnisonLyricsService(http));
        Assert.Equal(new[] { "Musixmatch", "lrc.red", "Unison", "Kugou", "YouTube Music" }, sources.Select(s => s.Name));
        var off = new AppSettings { LrcRedEnabled = false, UnisonEnabled = false };
        Assert.False(sources[1].IsEnabled(off));
        Assert.False(sources[2].IsEnabled(off));
        var on = new AppSettings { LrcRedEnabled = true, UnisonEnabled = true };
        Assert.True(sources[1].IsEnabled(on));
        Assert.True(sources[2].IsEnabled(on));
        Assert.False(sources[1].IsEnabled(new AppSettings())); // lrc.red is opt-in
        Assert.True(sources[2].IsEnabled(new AppSettings()));
    }
}
