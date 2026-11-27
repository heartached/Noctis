using System.Net;
using System.Text;
using Noctis.Services;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// Settings › Artwork › Artist videos: the Apple artist lookup, the artist page's motion video
/// and the variant picked, against Apple responses recorded 2026-11-26 (Fixtures/AppleMusic/
/// ArtistVideo: the iTunes artist search, the artist pages trimmed to their intent and video
/// blocks, the wide video's HLS master). No network.
/// </summary>
public class ArtistVideoLookupTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppleMusic", "ArtistVideo", name));

    private readonly List<string> _requests = new();

    // The search answers with the recorded list whatever the term (Apple's search is fuzzy, so a
    // misspelt name gets the same near hits); pages and the master by id.
    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        lock (_requests) _requests.Add(url);
        string? body = null;
        if (url.StartsWith("https://itunes.apple.com/search", StringComparison.Ordinal) && url.Contains("entity=musicArtist", StringComparison.Ordinal))
        {
            var term = Uri.UnescapeDataString(request.RequestUri.Query);
            body = term.Contains("Taylor", StringComparison.OrdinalIgnoreCase)
                ? Fixture("search-taylor-swift.json")
                : Fixture("search-chase-atlantic.json");
        }
        else if (url.StartsWith("https://music.apple.com/", StringComparison.Ordinal) && url.EndsWith("/879496517", StringComparison.Ordinal))
            body = Fixture("artist-879496517-chase-atlantic.html");
        else if (url.StartsWith("https://music.apple.com/", StringComparison.Ordinal) && url.EndsWith("/159260351", StringComparison.Ordinal))
            body = Fixture("artist-159260351-taylor-swift.html");
        else if (url.EndsWith("/P1197232327_default.m3u8", StringComparison.Ordinal))
            body = Fixture("master-P1197232327.m3u8");

        return body == null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }

    private ITunesArtworkService Service() => new(new HttpClient(new StubHandler(Route))) { HomeStorefront = "us" };

    [Theory]
    [InlineData("Chase Atlantic")]
    [InlineData("chase atlantic")]
    [InlineData("CHASE ATLANTIC")]
    public async Task ExactName_FindsTheArtist_AndTheWideH264Loop_AtTheHeroSize(string name)
    {
        var found = await Service().FindArtistVideoAsync(name);

        Assert.Equal(ArtistVideoOutcome.Found, found.Outcome);
        Assert.Equal(879496517, found.ArtistId);
        var v = found.Variant!;
        // Owner 11-26 "slow and choppy": the smallest H.264 frame that still fills the hero's 960 px
        // decode (this master: 1024x576), not one of the four 1080p streams decoded at full size.
        Assert.Equal((1024, 576), (v.Width, v.Height));
        Assert.StartsWith("avc1", v.Codec);
        Assert.Equal("SDR", v.VideoRange);
        // The master parser reads AVERAGE-BANDWIDTH (its "BANDWIDTH=" match lands there first).
        Assert.Equal(2200108, v.Bandwidth);
        Assert.EndsWith("_sdr_1024x576.m3u8", v.Url);
        Assert.True(IsAppleMediaHost(v.Url));
        Assert.Equal("https://music.apple.com/us/artist/chase-atlantic/879496517", found.PageUrl);
        // One search (home = us), one page, one master: nothing else asked.
        Assert.Equal(3, _requests.Count);
    }

    [Theory]
    [InlineData("Chase Atlantik")]
    [InlineData("Chase")]
    [InlineData("Chase Atlantic Tribute")]
    public async Task WrongName_IsAMiss_AndNoArtistPageIsOpened(string name)
    {
        var found = await Service().FindArtistVideoAsync(name);

        Assert.Equal(ArtistVideoOutcome.NoArtist, found.Outcome);
        Assert.Equal(0, found.ArtistId);
        Assert.DoesNotContain(_requests, u => u.Contains("/artist/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArtistWithVideoArtworkNull_IsNoVideo_AfterEveryStorefront()
    {
        var found = await Service().FindArtistVideoAsync("Taylor Swift");

        Assert.Equal(ArtistVideoOutcome.NoVideo, found.Outcome);
        Assert.Equal(159260351, found.ArtistId);
        // Home + US are one store here, then GB and DE, as the album loops.
        var pages = _requests.Where(u => u.Contains("/artist/", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "/us/", "/gb/", "/de/" }, pages.Select(u => u.Substring(23, 4)).ToArray());
        Assert.DoesNotContain(_requests, u => u.EndsWith(".m3u8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KnownArtistId_SkipsTheSearch()
    {
        var found = await Service().FindArtistVideoAsync("whatever", knownArtistId: 879496517);

        Assert.Equal(ArtistVideoOutcome.Found, found.Outcome);
        Assert.DoesNotContain(_requests, u => u.Contains("itunes.apple.com/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppleNotAnswering_IsAFailure_NotAMiss()
    {
        var down = new ITunesArtworkService(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            HomeStorefront = "us",
        };
        Assert.Equal(ArtistVideoOutcome.Failed, (await down.FindArtistVideoAsync("Chase Atlantic")).Outcome);
        Assert.Equal(ArtistVideoOutcome.Failed, (await down.FindArtistVideoAsync("x", knownArtistId: 879496517)).Outcome);
    }

    [Fact]
    public void Match_IsExactOnly_CaseAndAccentsAside_AndTwoNamesakesAreNoMatch()
    {
        var hits = ParseArtistHits(Fixture("search-taylor-swift.json"));
        Assert.Equal(159260351, MatchArtistExactly("taylor swift", hits, out _)!.Id);
        // "Taylor Swift Tribute Songs", "Taylor Swift DV", "Swift Taylor" are all there; none is taken for a near name.
        Assert.Null(MatchArtistExactly("Taylor Swif", hits, out var ambiguous));
        Assert.False(ambiguous);

        var accents = new[] { new ArtistHit("Beyoncé", 1, null), new ArtistHit("Sigur Rós", 2, null) };
        Assert.Equal(1, MatchArtistExactly("BEYONCE", accents, out _)!.Id);
        Assert.Equal(2, MatchArtistExactly("sigur ros", accents, out _)!.Id);

        var namesakes = new[] { new ArtistHit("Nirvana", 10, null), new ArtistHit("Nirvana", 11, null), new ArtistHit("Nirvana", 10, null) };
        Assert.Null(MatchArtistExactly("Nirvana", namesakes, out ambiguous));
        Assert.True(ambiguous);
    }

    [Fact]
    public async Task TwoArtistsOfTheName_IsNoArtist()
    {
        const string json = """
            {"resultCount":2,"results":[
            {"wrapperType":"artist","artistName":"Nirvana","artistLinkUrl":"https://music.apple.com/us/artist/nirvana/112018","artistId":112018},
            {"wrapperType":"artist","artistName":"Nirvana","artistLinkUrl":"https://music.apple.com/us/artist/nirvana/5940196","artistId":5940196}]}
            """;
        var svc = new ITunesArtworkService(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) })))
        { HomeStorefront = "us" };

        Assert.Equal(ArtistVideoOutcome.NoArtist, (await svc.FindArtistVideoAsync("Nirvana")).Outcome);
    }

    [Fact]
    public void Page_ReadsTheWideLoop_AndVideoArtworkNullIsNone()
    {
        var chase = ArtistMotionPage.Parse(Fixture("artist-879496517-chase-atlantic.html"), 879496517);
        Assert.True(chase.IsArtistPage);
        Assert.Equal("https://mvod.itunes.apple.com/itunes-assets/HLSMusic221/v4/cf/bb/fe/cfbbfec1-c949-54ad-53d9-414bf741ade4/P1197232327_default.m3u8",
            chase.WideVideoUrl);
        Assert.Contains("{w}x{h}", chase.WidePosterUrl);

        var taylor = ArtistMotionPage.Parse(Fixture("artist-159260351-taylor-swift.html"), 159260351);
        Assert.True(taylor.IsArtistPage);
        Assert.False(taylor.HasVideo);
    }

    [Fact]
    public void Page_OfAnotherArtist_IsNotRead()
        => Assert.False(ArtistMotionPage.Parse(Fixture("artist-879496517-chase-atlantic.html"), 159260351).HasVideo);

    [Fact]
    public void Page_WithNoHeaderVideo_DoesNotTakeAShelfsVideo()
    {
        // The header says null; a later shelf (another artist) carries a loop.
        const string html = """
            {"storeAdamID":"42"} "header":{"videoArtwork":null,"wideVideoArtwork":null}
            "shelf":{"wideVideoArtwork":{"dictionary":{"motionArtistWide16x9":{"video":"https://mvod.itunes.apple.com/other.m3u8"}}}}
            """;
        Assert.False(ArtistMotionPage.Parse(html, 42).HasVideo);
    }

    [Fact]
    public void Page_WithoutTheWideKey_TakesTheFullscreenLoop_ButNeverAnOffAppleHost()
    {
        const string full = """
            {"storeAdamID":"42"} "wideVideoArtwork":{"dictionary":{"motionArtistFullscreen16x9":{"previewFrame":{"url":"https://is1-ssl.mzstatic.com/p.png"},"video":"https://mvod.itunes.apple.com/full.m3u8"}}}
            """;
        Assert.Equal("https://mvod.itunes.apple.com/full.m3u8", ArtistMotionPage.Parse(full, 42).WideVideoUrl);

        const string hostile = """
            {"storeAdamID":"42"} "wideVideoArtwork":{"dictionary":{"motionArtistWide16x9":{"video":"https://evil.example/x.m3u8"}}}
            """;
        Assert.False(ArtistMotionPage.Parse(hostile, 42).HasVideo);
    }

    private static AnimatedArtworkVariant V(int w, int h, string codec, long bw, string range = "SDR")
        => new($"{w}x{h}", $"https://mvod.itunes.apple.com/{w}x{h}_{codec}_{bw}_{range}.m3u8", w, h, codec, bw, true) { VideoRange = range };

    [Fact]
    public void Picker_NeverAbove1080p_PrefersH264_AndSdr()
    {
        var pick = PickArtistVideoVariant(new[]
        {
            V(3840, 2160, "avc1.640033", 30_000_000),
            V(2560, 1440, "hvc1.2.20000000.H150.B0", 16_000_000),
            V(1920, 1080, "hvc1.2.20000000.L123.B0", 10_000_000),
            V(1920, 1080, "avc1.640028", 12_350_432),
            V(1920, 1080, "avc1.640028", 4_223_697),
            V(1920, 1080, "avc1.640028", 2_000_000, "PQ"),
            V(1280, 720, "avc1.64001f", 3_522_350),
        })!;
        // Owner 11-26 "slow and choppy": the smallest H.264 frame that still fills the hero's
        // 960 px decode, not the 1080p one (decoded at full size, then scaled down).
        Assert.Equal((1280, 720, "avc1.64001f", 3_522_350L, "SDR"), (pick.Width, pick.Height, pick.Codec, pick.Bandwidth, pick.VideoRange));

        // No H.264 at all: the H.265 one, still never the 4K one.
        var hevc = PickArtistVideoVariant(new[] { V(3840, 2160, "hvc1.2", 9), V(1920, 1080, "hvc1.2", 5), V(1280, 720, "hvc1.2", 3) })!;
        Assert.Equal((1280, 720), (hevc.Width, hevc.Height));

        // Only 4K / HDR: nothing.
        Assert.Null(PickArtistVideoVariant(new[] { V(3840, 2160, "avc1", 9), V(1920, 1080, "avc1", 5, "HLG") }));
    }

    [Fact]
    public void Picker_TakesTheSmallestFrameAtLeastTheHeroDecodeWide()
    {
        Assert.Equal(960, ArtistVideoWantedWidth);
        // 960x540 exists: that one, under the 720p and 1080p.
        var exact = PickArtistVideoVariant(new[] { V(1920, 1080, "avc1", 5), V(960, 540, "avc1", 2), V(1280, 720, "avc1", 3), V(640, 360, "avc1", 1) })!;
        Assert.Equal((960, 540), (exact.Width, exact.Height));
        // Only 1080p fills it: 1080p, never the 360p one below the decode size.
        var big = PickArtistVideoVariant(new[] { V(1920, 1080, "avc1", 5), V(640, 360, "avc1", 1) })!;
        Assert.Equal((1920, 1080), (big.Width, big.Height));
        // Nothing reaches 960 px: the largest there is.
        var small = PickArtistVideoVariant(new[] { V(640, 360, "avc1", 1), V(854, 480, "avc1", 2) })!;
        Assert.Equal((854, 480), (small.Width, small.Height));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
