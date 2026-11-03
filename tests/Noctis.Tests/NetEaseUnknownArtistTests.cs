using System.Net;
using System.Net.Http;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Bulk Find Lyrics asks NetEase for untagged songs too. "Unknown Artist" is the library's
/// placeholder, not a name: sent in the search it poisons the results (every other source
/// leaves it out, LyricsSearchSelector.IsUnknownArtist).
/// </summary>
public class NetEaseUnknownArtistTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public readonly List<string> Queries = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var s = body.Split('&').FirstOrDefault(p => p.StartsWith("s=", StringComparison.Ordinal));
                if (s != null) Queries.Add(WebUtility.UrlDecode(s[2..]));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":{\"songs\":[]},\"code\":200}")
            };
        }
    }

    [Fact]
    public async Task UnknownArtist_IsLeftOutOfTheSearch()
    {
        var handler = new CaptureHandler();
        var svc = new NetEaseService(new HttpClient(handler));

        await svc.SearchLyricsAsync("Unknown Artist", "Some Song", 200);

        Assert.Equal(new[] { "Some Song" }, handler.Queries);
    }

    [Fact]
    public async Task KnownArtist_IsStillSearched()
    {
        var handler = new CaptureHandler();
        var svc = new NetEaseService(new HttpClient(handler));

        await svc.SearchLyricsAsync("Real Artist", "Some Song", 200);

        Assert.Equal(new[] { "Some Song Real Artist" }, handler.Queries);
    }
}
