using System.Net;
using System.Net.Http;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #265: Genius plain lyrics against trimmed copies of real answers captured 2026-11-25
/// (Fixtures/Genius: the /api/search/song hits for "Bjork Joga", and the song page with its
/// header, an ad block and two of its lyrics containers) — no network. Same contract as the other
/// providers: a definitive miss is null, a failure is <see cref="LyricsProviderException"/>.
/// </summary>
public class GeniusProviderTests
{
    private sealed class Router(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Genius", name));

    private static readonly string Search = Fixture("search-bjork-joga.json");
    private static readonly string Page = Fixture("bjork-joga-lyrics.html");

    private const string JogaLyrics =
        "All these accidents that happen follow the dot\n" +
        "Coincidence makes sense only with you\n" +
        "You don't have to speak, I feel\n" +
        "\n" +
        "Emotional landscapes\n" +
        "They puzzle me\n" +
        "\n" +
        "This state of emergency\n" +
        "How beautiful to be\n" +
        "State of emergency\n" +
        "Is where I want to be\n" +
        "State of emergency\n" +
        "How beautiful to be";

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static (GeniusLyricsService Service, Router Router) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var router = new Router(respond);
        return (new GeniusLyricsService(new HttpClient(router)), router);
    }

    private static HttpResponseMessage Genius(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath == "/api/search/song" ? Ok(Search)
        : r.RequestUri.AbsolutePath == "/Bjork-joga-lyrics" ? Ok(Page)
        : new HttpResponseMessage(HttpStatusCode.NotFound);

    [Fact]
    public void Extract_KeepsTheWords_DropsHeaderAdsAndSectionLabels()
    {
        var text = GeniusLyricsService.ExtractLyrics(Page);
        Assert.Equal(JogaLyrics, text);
        Assert.DoesNotContain("Contributors", text);
        Assert.DoesNotContain("Read More", text);
        Assert.DoesNotContain("[", text);
        Assert.DoesNotContain("About this song", text);
    }

    [Fact]
    public void Extract_DecodesEntities_AndBreaksLinesOnBr()
    {
        const string html = "<div data-lyrics-container=\"true\" class=\"x\">[Verse 1: A &amp; B]<br/>Don&#x27;t <i>stop</i><br>Rock &amp; roll<br/><br/><br/>[Chorus]<br/>Ça va</div>";
        Assert.Equal("Don't stop\nRock & roll\n\nÇa va", GeniusLyricsService.ExtractLyrics(html));
    }

    [Fact]
    public void Extract_PageWithoutLyrics_IsNull()
    {
        Assert.Null(GeniusLyricsService.ExtractLyrics("<html><body><div>Lyrics for this song have yet to be released.</div></body></html>"));
        Assert.Null(GeniusLyricsService.ExtractLyrics("<div data-lyrics-container=\"true\"><div data-exclude-from-selection=\"true\">3 Contributors</div></div>"));
    }

    [Fact]
    public void Pick_TheSongItself_NotTheTranslationOrALiveTake()
    {
        var hits = GeniusLyricsService.ParseHits(Search);
        Assert.Equal(5, hits.Count);

        Assert.Equal("https://genius.com/Bjork-joga-lyrics", GeniusLyricsService.PickHit(hits, "Björk", "Jóga")!.Url);
        // Unaccented tags still find it.
        Assert.Equal("https://genius.com/Bjork-joga-lyrics", GeniusLyricsService.PickHit(hits, "Bjork", "Joga")!.Url);
        // A live recording in the library gets the live page.
        Assert.Equal("https://genius.com/Bjork-joga-live-on-november-29th-1997-lyrics",
            GeniusLyricsService.PickHit(hits, "Björk", "Jóga (Live on November 29th, 1997)")!.Url);
    }

    [Fact]
    public void Pick_AnotherArtist_IsAMiss()
    {
        var hits = GeniusLyricsService.ParseHits(Search);
        Assert.Null(GeniusLyricsService.PickHit(hits, "Sigur Rós", "Jóga"));
        Assert.Null(GeniusLyricsService.PickHit(hits, "Björk", "Hyperballad"));
    }

    [Fact]
    public void Pick_SkipsInstrumentals_AndPagesOffGenius()
    {
        var hits = new[]
        {
            new GeniusLyricsService.Hit("Jóga", "Björk", "https://genius.com/Bjork-joga-lyrics", Instrumental: true),
            new GeniusLyricsService.Hit("Jóga", "Björk", "https://evil.example/Bjork-joga-lyrics", false),
            new GeniusLyricsService.Hit("Jóga", "Björk", "http://genius.com/Bjork-joga-lyrics", false),
            new GeniusLyricsService.Hit("Jóga", "Björk", "https://genius.com.evil.example/x", false),
        };
        Assert.Null(GeniusLyricsService.PickHit(hits, "Björk", "Jóga"));
    }

    [Fact]
    public async Task Search_ReturnsPlainLyricsOnly_FromTheSongPage()
    {
        var (svc, router) = Make(Genius);
        var r = await svc.SearchLyricsAsync("Björk", "Jóga", 305);

        Assert.NotNull(r);
        Assert.Equal(JogaLyrics, r!.PlainLyrics);
        Assert.Null(r.SyncedLyrics);
        Assert.Equal(1, LyricsSearchSelector.FormatRank(r));
        Assert.Equal("Jóga", r.TrackName);
        Assert.Equal("Björk", r.ArtistName);
        Assert.Equal(2, router.Requests.Count);
        Assert.StartsWith("https://genius.com/api/search/song?q=Bj%C3%B6rk%20J%C3%B3ga", router.Requests[0]);
        Assert.Equal("https://genius.com/Bjork-joga-lyrics", router.Requests[1]);

        // Remembered: a second ask costs nothing.
        Assert.NotNull(await svc.SearchLyricsAsync("Björk", "Jóga", 305));
        Assert.Equal(2, router.Requests.Count);
    }

    [Fact]
    public async Task Search_NoMatchingHit_IsNull_WithoutFetchingAPage()
    {
        var (svc, router) = Make(Genius);
        Assert.Null(await svc.SearchLyricsAsync("Sigur Rós", "Jóga", 305));
        Assert.Single(router.Requests);
    }

    [Fact]
    public async Task Search_RedirectOffGenius_IsAMiss()
    {
        var (svc, _) = Make(r => r.RequestUri!.AbsolutePath == "/api/search/song"
            ? Ok(Search)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/Bjork-joga-lyrics"),
            });
        Assert.Null(await svc.SearchLyricsAsync("Björk", "Jóga", 305));
    }

    [Fact]
    public async Task Busy_IsABusyProviderError_AndNotRemembered()
    {
        var (svc, router) = Make(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") });
        var ex = await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Björk", "Jóga", 305));
        Assert.True(ex.IsBusy);
        Assert.Equal("Genius", ex.Provider);
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Björk", "Jóga", 305));
        Assert.Equal(2, router.Requests.Count);
    }

    [Fact]
    public void IsOnByDefault_AndListedBeforeLrcMux()
    {
        Assert.True(new AppSettings().GeniusEnabled);
        var http = new HttpClient(new Router(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var sources = OnlineLyricsSource.BuiltIns(new MusixmatchService(http, null), new KugouLyricsService(http),
            new YouTubeMusicLyricsService(http), new LrcRedLyricsService(http), new UnisonLyricsService(http),
            new LrcMuxLyricsService(http), new GeniusLyricsService(http));
        Assert.Equal(OnlineLyricsSource.Genius, sources[^2].Name);
        Assert.Equal(OnlineLyricsSource.LrcMux, sources[^1].Name);
        Assert.False(sources[^2].IsEnabled(new AppSettings { GeniusEnabled = false }));
    }

    /// <summary>Live check against genius.com; set NOCTIS_LIVE_GENIUS=1 to run.</summary>
    [Fact]
    public async Task Live_RealSong()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_GENIUS") != "1") return;
        var artist = Environment.GetEnvironmentVariable("NOCTIS_LIVE_GENIUS_ARTIST") ?? "glass beach";
        var title = Environment.GetEnvironmentVariable("NOCTIS_LIVE_GENIUS_TITLE") ?? "cold weather";
        using var http = GeniusLyricsService.CreateHttpClient() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var r = await new GeniusLyricsService(http).SearchLyricsAsync(artist, title, 0);
        Assert.NotNull(r);
        var lines = r!.PlainLyrics!.Split('\n');
        TestContext.Current.TestOutputHelper?.WriteLine($"LIVE {r.ArtistName} - {r.TrackName}: {lines.Length} lines\n" + string.Join('\n', lines.Take(6)));
        Assert.True(lines.Length > 4);
    }
}
