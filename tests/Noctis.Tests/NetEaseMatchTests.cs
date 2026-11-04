using System.Net;
using System.Net.Http;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #162: auto-search put "纯音乐，请欣赏" ("instrumental, please enjoy") on an instrumental
/// song. NetEase took the search's first hit whatever song it was, and its instrumental notice
/// came back as lyrics. A hit must be the song (title + artist), and the notice — or NetEase's
/// "nolyric" flag — is an instrumental answer, not lyrics.
/// </summary>
public class NetEaseMatchTests
{
    private sealed class Router(string songs, string lyrics) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            var body = url.Contains("/api/search/") ? songs : lyrics;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string Songs(params (long Id, string Name, string Artist, long DurationMs)[] songs) =>
        "{\"result\":{\"songs\":[" + string.Join(",", songs.Select(s =>
            $"{{\"id\":{s.Id},\"name\":\"{s.Name}\",\"duration\":{s.DurationMs},\"artists\":[{{\"id\":1,\"name\":\"{s.Artist}\"}}]}}")) +
        "]},\"code\":200}";

    private const string RealLyrics =
        "{\"lrc\":{\"lyric\":\"[00:01.00]First line\\n[00:04.00]Second line\\n\"},\"code\":200}";

    [Fact]
    public async Task FirstHitOfAnotherSong_IsAMiss()
    {
        var router = new Router(Songs((7, "Saint Pablo", "Kanye West", 372000)), RealLyrics);
        var svc = new NetEaseService(new HttpClient(router));

        Assert.Null(await svc.SearchLyricsAsync("Kid Wise", "pablo", 181));
        Assert.DoesNotContain(router.Requests, u => u.Contains("/api/song/lyric"));
    }

    [Fact]
    public async Task TheSongItself_IsStillFound_ClosestLengthFirst()
    {
        var router = new Router(Songs(
            (1, "Other", "Kid Wise", 181000),
            (2, "pablo", "Kid Wise", 250000),
            (3, "pablo", "Kid Wise", 181500)), RealLyrics);
        var svc = new NetEaseService(new HttpClient(router));

        var result = await svc.SearchLyricsAsync("Kid Wise", "pablo", 181);

        Assert.NotNull(result);
        Assert.True(result!.HasSyncedLyrics);
        Assert.Contains(router.Requests, u => u.Contains("/api/song/lyric?id=3&"));
    }

    [Theory]
    [InlineData("{\"lrc\":{\"lyric\":\"[99:00.00]纯音乐，请欣赏\\n\"},\"code\":200}")]
    [InlineData("{\"lrc\":{\"lyric\":\"[00:00.000] 作曲 : Kid Wise\\n[00:01.000]纯音乐，请欣赏\\n\"},\"code\":200}")]
    [InlineData("{\"nolyric\":true,\"code\":200}")]
    public async Task InstrumentalNotice_IsAnInstrumentalAnswer_NotLyrics(string lyrics)
    {
        var svc = new NetEaseService(new HttpClient(new Router(Songs((3, "pablo", "Kid Wise", 181000)), lyrics)));

        var result = await svc.SearchLyricsAsync("Kid Wise", "pablo", 181);

        Assert.NotNull(result);
        Assert.True(result!.Instrumental);
        Assert.False(result.HasLyrics);
    }

    [Theory]
    [InlineData("[00:01.58]纯音乐，请欣赏", true)]
    [InlineData("[00:01.58]<00:01.58>纯音乐，请欣赏<00:04.58>", true)]
    [InlineData("[ar:x]\n[00:00.00]作词 : 无\n[00:00.50]作曲 : Kid Wise\n[00:01.00]此歌曲为没有填词的纯音乐，请您欣赏", true)]
    [InlineData("[00:01.00]纯音乐，请欣赏\n[00:05.00]But then somebody sings", false)]
    [InlineData("[00:01.00]A real line", false)]
    [InlineData("", false)]
    public void InstrumentalPlaceholder_IsOnlyTheNotice(string lyrics, bool expected)
    {
        Assert.Equal(expected, LyricsSearchSelector.IsInstrumentalPlaceholder(lyrics));
    }
}
