using System.Net;
using System.Net.Http;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The NetEase lookup memo kept one entry per distinct track for the whole session
/// (a hit holds the full LRC twice). It is capped now, and still answers repeats.
/// </summary>
public class NetEaseCacheBoundTests
{
    private sealed class EmptySearchHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            // A genuine "no results": 200 with an empty songs list (a cached miss).
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":{\"songs\":[]},\"code\":200}")
            });
        }
    }

    [Fact]
    public async Task RepeatLookup_IsServedFromTheMemo()
    {
        var handler = new EmptySearchHandler();
        var svc = new NetEaseService(new HttpClient(handler));

        Assert.Null(await svc.SearchLyricsAsync("Artist", "Title", 200));
        Assert.Null(await svc.SearchLyricsAsync("Artist", "Title", 200));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Memo_NeverGrowsPastItsCap()
    {
        var svc = new NetEaseService(new HttpClient(new EmptySearchHandler()));

        for (var i = 0; i < NetEaseService.MaxCacheEntries * 2 + 10; i++)
            await svc.SearchLyricsAsync("Artist", $"Title {i}", 200);

        Assert.InRange(svc.CacheCount, 1, NetEaseService.MaxCacheEntries);
    }
}
