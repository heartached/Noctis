using System.Net;
using System.Net.Http;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics services must keep "definitive miss" (404 / empty results → null or
/// empty list) distinguishable from "provider error" (network failure, timeout,
/// 5xx, malformed body → LyricsProviderException). Before this contract, an
/// LRCLIB outage (Cloudflare 504s) or a plain offline machine surfaced as
/// "No Lyrics found." — telling the user the track has no lyrics.
/// </summary>
public class LyricsProviderErrorContractTests
{
    // ── Harness ──

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int RequestCount;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    private static LrcLibService Service(StubHandler handler) => new(new HttpClient(handler));

    // ── Definitive miss: 404 stays a null/empty answer, and is cached ──

    [Fact]
    public async Task Get_404_isDefinitiveMiss_andCached()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.NotFound));
        var svc = Service(handler);

        Assert.Null(await svc.GetLyricsAsync("Artist", "Title", 200));
        Assert.Null(await svc.GetLyricsAsync("Artist", "Title", 200));
        Assert.Equal(1, handler.RequestCount); // second answer came from the cache
    }

    [Fact]
    public async Task Search_404_isDefinitiveMiss_returnsEmptyList()
    {
        var svc = Service(new StubHandler(_ => Response(HttpStatusCode.NotFound)));
        Assert.Empty(await svc.SearchLyricsAsync("Artist", "Title"));
    }

    // ── Provider error: 5xx / timeout / garbage → LyricsProviderException, uncached ──

    [Fact]
    public async Task Get_500_throwsProviderError_andIsNotCached()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.InternalServerError));
        var svc = Service(handler);

        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.GetLyricsAsync("Artist", "Title", 200));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.GetLyricsAsync("Artist", "Title", 200));
        Assert.Equal(2, handler.RequestCount); // the failure was not stored as a miss
    }

    [Fact]
    public async Task Get_timeout_throwsProviderError()
    {
        // HttpClient timeouts surface as TaskCanceledException without the caller's
        // token being cancelled — that is an infrastructure failure, not a cancel.
        var svc = Service(new StubHandler(_ => throw new TaskCanceledException("timed out")));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.GetLyricsAsync("Artist", "Title", 200));
    }

    [Fact]
    public async Task Get_garbageJson_throwsProviderError()
    {
        var svc = Service(new StubHandler(_ => Response(HttpStatusCode.OK, "<html>cloudflare 504</html>")));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.GetLyricsAsync("Artist", "Title", 200));
    }

    [Fact]
    public async Task Search_500_throwsProviderError()
    {
        var svc = Service(new StubHandler(_ => Response(HttpStatusCode.BadGateway)));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Artist", "Title"));
    }

    // ── Caller cancellation: silent, never disguised as a provider error ──

    [Fact]
    public async Task Get_cancelledToken_propagatesCancellation_notProviderError()
    {
        var svc = Service(new StubHandler(_ => Response(HttpStatusCode.OK, "{}")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GetLyricsAsync("Artist", "Title", 200, cts.Token));
        Assert.IsNotType<LyricsProviderException>(ex);
    }

    // ── Cache-key injectivity: "A|B"+"C" must not collide with "A"+"B|C" ──

    [Fact]
    public async Task Get_cacheKeys_doNotCollideOnSeparatorCharacters()
    {
        var handler = new StubHandler(req =>
        {
            // Echo the requested artist back so a collision would serve the wrong body.
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var artist = query["artist_name"];
            return Response(HttpStatusCode.OK, $"{{\"artistName\":\"{artist}\"}}");
        });
        var svc = Service(handler);

        var first = await svc.GetLyricsAsync("A|B", "C", 200);
        var second = await svc.GetLyricsAsync("A", "B|C", 200);

        Assert.Equal(2, handler.RequestCount); // distinct keys → both hit the network
        Assert.Equal("A|B", first?.ArtistName);
        Assert.Equal("A", second?.ArtistName);
    }

    // ── NetEase shares the same contract ──

    [Fact]
    public async Task NetEase_500_throwsProviderError()
    {
        var svc = new NetEaseService(new HttpClient(new StubHandler(_ => Response(HttpStatusCode.InternalServerError))));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Artist", "Title", 200));
    }

    [Fact]
    public async Task NetEase_emptySongList_isDefinitiveMiss_returnsNull()
    {
        var svc = new NetEaseService(new HttpClient(new StubHandler(_ =>
            Response(HttpStatusCode.OK, "{\"result\":{\"songs\":[]}}"))));
        Assert.Null(await svc.SearchLyricsAsync("Artist", "Title", 200));
    }

    [Fact]
    public async Task NetEase_garbageJson_throwsProviderError()
    {
        var svc = new NetEaseService(new HttpClient(new StubHandler(_ =>
            Response(HttpStatusCode.OK, "not json"))));
        await Assert.ThrowsAsync<LyricsProviderException>(() => svc.SearchLyricsAsync("Artist", "Title", 200));
    }

    [Fact]
    public async Task NetEase_abroadEncryptedResult_isDefinitiveMiss_andCached()
    {
        // For IPs outside mainland China the search endpoint answers 200 with
        // "abroad": true and "result" as an opaque encrypted hex STRING instead
        // of the songs object. The provider is up and answering — treating the
        // unusable payload as a transfer error turned every overseas search into
        // a fake "check your internet connection" upstream.
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK,
            "{\"result\":\"35b1748964af8a7c1d883e3a6f3c773b\",\"abroad\":true,\"code\":200}"));
        var svc = new NetEaseService(new HttpClient(handler));

        Assert.Null(await svc.SearchLyricsAsync("Artist", "Title", 200));
        Assert.Null(await svc.SearchLyricsAsync("Artist", "Title", 200));
        Assert.Equal(1, handler.RequestCount); // definitive miss → cached
    }
}
