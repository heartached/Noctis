using System.Net;
using System.Net.Http;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// lrc.red's "Sync with the model" (POST /s/{isrc}/sync) against a trimmed copy of the real answer
/// for "Die With A Smile (Acoustic)" (USUM72413062, line-synced in the lookup) captured 2026-10-19,
/// and every way it can fail. No network.
/// </summary>
public class LrcRedModelSyncServiceTests
{
    internal const string AcousticIsrc = "USUM72413062";

    // The real sync answer's head, its first two of 50 lines and its "source" TTML cut to match.
    internal static readonly string SyncAnswer =
        "{\"id\":\"USUM72413062\",\"title\":\"Die With A Smile (Acoustic)\",\"artist\":\"Lady Gaga, Bruno Mars\",\"album\":\"Die With A Smile - Single\"," +
        "\"year\":2024,\"language\":\"en\",\"lyrics\":{\"sync\":\"word\",\"timing\":\"word\",\"meta\":{},\"lines\":" + SyncLines + ",\"duration\":251.728}," +
        "\"source\":" + System.Text.Json.JsonSerializer.Serialize(SyncTtml) + ",\"duration\":251.728,\"level\":\"word\"}";

    internal const string SyncLines =
        "[{\"words\":[[{\"text\":\"(Ooh,\",\"begin\":3.856,\"end\":4.776}],[{\"text\":\"ooh)\",\"begin\":4.776,\"end\":4.998}]],\"text\":\"(Ooh, ooh)\",\"begin\":3.856,\"end\":4.998,\"voice\":\"a\",\"first\":true,\"timed\":true}," +
        "{\"words\":[[{\"text\":\"I,\",\"begin\":9.556,\"end\":11.855}],[{\"text\":\"I\",\"begin\":11.855,\"end\":12.269}],[{\"text\":\"just\",\"begin\":12.269,\"end\":12.636}],[{\"text\":\"woke\",\"begin\":12.636,\"end\":13.015}],[{\"text\":\"up\",\"begin\":13.015,\"end\":13.355}],[{\"text\":\"from\",\"begin\":13.355,\"end\":13.905}],[{\"text\":\"a\",\"begin\":13.905,\"end\":14.145}],[{\"text\":\"dream\",\"begin\":14.145,\"end\":14.195}]],\"text\":\"I, I just woke up from a dream\",\"begin\":9.556,\"end\":14.195,\"voice\":\"a\",\"first\":true,\"timed\":true}]";

    internal const string SyncTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" itunes:timing=\"Word\" xml:lang=\"en\"><head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/></metadata></head>" +
        "<body dur=\"4:11.728\"><div begin=\"0:03.856\" end=\"0:14.195\">" +
        "<p begin=\"0:03.856\" end=\"0:04.998\" itunes:key=\"L1\" ttm:agent=\"v1\"><span begin=\"0:03.856\" end=\"0:04.776\">(Ooh,</span> <span begin=\"0:04.776\" end=\"0:04.998\">ooh)</span></p>" +
        "<p begin=\"0:09.556\" end=\"0:14.195\" itunes:key=\"L2\" ttm:agent=\"v1\"><span begin=\"0:09.556\" end=\"0:11.855\">I,</span> <span begin=\"0:11.855\" end=\"0:12.269\">I</span> <span begin=\"0:12.269\" end=\"0:12.636\">just</span> <span begin=\"0:12.636\" end=\"0:13.015\">woke</span> <span begin=\"0:13.015\" end=\"0:13.355\">up</span> <span begin=\"0:13.355\" end=\"0:13.905\">from</span> <span begin=\"0:13.905\" end=\"0:14.145\">a</span> <span begin=\"0:14.145\" end=\"0:14.195\">dream</span></p>" +
        "</div></body></tt>";

    // The lookup's row for it on 2026-10-19 (line-synced), and lrc.red's line-synced file.
    internal const string AcousticLookup =
        "{\"results\":[{\"album_name\":\"Die With A Smile - Single\",\"artist_name\":\"Lady Gaga, Bruno Mars\",\"duration\":252,\"id\":\"USUM72413062\",\"isrc\":\"USUM72413062\",\"timing_type\":\"line\",\"track_name\":\"Die With A Smile (Acoustic)\"}],\"source\":\"HIT-LRC-RED\",\"total\":1}";

    internal const string AcousticLineTtml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:lrc=\"http://lrc.red/lyric-ttml-internal\" lrc:timing=\"Line\" xml:lang=\"en\"><head><metadata></metadata></head><body><div>" +
        "<p begin=\"9.556\" end=\"14.195\">I, I just woke up from a dream</p><p begin=\"15.000\" end=\"18.000\">Where you and I had to say goodbye</p></div></body></tt>";

    internal sealed class Router : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public readonly List<(HttpMethod Method, string Url, string? UserAgent, bool HasBody)> Requests = new();

        public Router(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }
        public Router(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
                Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString(), request.Content != null));
            return _respond(request, cancellationToken);
        }

        public int Count(HttpMethod method) { lock (Requests) return Requests.Count(r => r.Method == method); }
    }

    internal static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body) };

    /// <summary>lrc.red as the acoustic cut: line-synced in the lookup and its file, word-synced by the model.</summary>
    internal static Router AcousticRouter(Func<HttpResponseMessage>? sync = null) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/v1") return Json(HttpStatusCode.OK, AcousticLookup);
        if (path == $"/s/{AcousticIsrc}.ttml") return Json(HttpStatusCode.OK, AcousticLineTtml);
        if (path == $"/s/{AcousticIsrc}/sync" && req.Method == HttpMethod.Post) return sync?.Invoke() ?? Json(HttpStatusCode.OK, SyncAnswer);
        return Json(HttpStatusCode.NotFound, "{\"error\":\"Not found\"}");
    });

    [Fact]
    public async Task Sync_PostsOnceWithNoBody_AndReturnsTheModelsWordSyncedLyrics()
    {
        var router = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var result = await svc.SyncWithModelAsync(AcousticIsrc);

        var request = Assert.Single(router.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://lrc.red/s/USUM72413062/sync", request.Url);
        Assert.False(request.HasBody);
        Assert.StartsWith("Noctis", request.UserAgent);
        Assert.Equal(AcousticIsrc, result.Isrc);
        Assert.Equal("Die With A Smile (Acoustic)", result.TrackName);
        Assert.Equal("Lady Gaga, Bruno Mars", result.ArtistName);
        Assert.Equal(251.728, result.Duration, 3);
        Assert.Equal(LyricsSearchSelector.WordSyncedRank, LyricsSearchSelector.FormatRank(result));
        var dream = LrcParser.Parse(result.SyncedLyrics!).Single(l => l.Text.Replace('\n', ' ') == "I, I just woke up from a dream");
        Assert.Equal(8, dream.Words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12260), dream.Words[2].Start); // "just" at LRC's centiseconds
    }

    [Fact]
    public async Task Sync_AfterASearch_LaterSearchesAnswerWithTheWordSyncedLyrics_WithoutAskingAgain()
    {
        var router = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var before = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);
        Assert.Equal(AcousticIsrc, before!.Isrc);
        Assert.Equal(2, LyricsSearchSelector.FormatRank(before)); // line-synced

        var synced = await svc.SyncWithModelAsync(before.Isrc!);
        var after = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);

        Assert.Same(synced, after);
        Assert.Equal(1, router.Count(HttpMethod.Post));
        Assert.Equal(2, router.Count(HttpMethod.Get)); // the first search's lookup and file only

        // A search not made before the sync (another length of the same song) gets it too.
        var other = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile (Acoustic)", 249.6);
        Assert.Same(synced, other);
        Assert.Equal(3, router.Count(HttpMethod.Get)); // its lookup, no file
    }

    [Fact]
    public async Task Sync_SecondPressForTheSameRecording_IsAnsweredWithoutASecondModelRun()
    {
        var router = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var first = await svc.SyncWithModelAsync(AcousticIsrc);
        var second = await svc.SyncWithModelAsync(AcousticIsrc.ToLowerInvariant());

        Assert.Same(first, second);
        Assert.Equal(1, router.Count(HttpMethod.Post));
    }

    [Fact]
    public async Task FindMatch_IsTheLookupAlone_AndRemembered()
    {
        var router = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var match = await svc.FindMatchAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);
        var again = await svc.FindMatchAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);
        var search = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);

        Assert.Equal(new LrcRedMatch(AcousticIsrc, "Die With A Smile (Acoustic)", "Lady Gaga, Bruno Mars", "Die With A Smile - Single", 252, "line"), match);
        Assert.False(match!.IsWordSynced);
        Assert.Equal(match, again);
        Assert.NotNull(search);
        Assert.Equal(new[] { "/api/v1", $"/s/{AcousticIsrc}.ttml" }, router.Requests.Select(r => new Uri(r.Url).AbsolutePath));
    }

    [Fact]
    public async Task Sync_WithoutSourceTtml_ReadsTheLinesInstead()
    {
        var answer = "{\"id\":\"USUM72413062\",\"title\":\"Die With A Smile (Acoustic)\",\"artist\":\"Lady Gaga, Bruno Mars\",\"lyrics\":{\"lines\":" + SyncLines + ",\"duration\":251.728}}";
        var svc = new LrcRedLyricsService(new HttpClient(AcousticRouter(() => Json(HttpStatusCode.OK, answer))));

        var result = await svc.SyncWithModelAsync(AcousticIsrc);

        Assert.Equal(251.728, result.Duration, 3);
        var dream = LrcParser.Parse(result.SyncedLyrics!).Single(l => l.Text.Replace('\n', ' ') == "I, I just woke up from a dream");
        Assert.Equal(new[] { "I,", "I", "just", "woke", "up", "from", "a", "dream" }, dream.Words!.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromMilliseconds(14140), dream.Words[^1].Start);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":\"No lyrics with that ISRC.\"}", LrcRedSyncFailure.Refused, "No lyrics with that ISRC.")]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"Alignment failed:\\n audio unavailable\"}", LrcRedSyncFailure.Refused, "Alignment failed: audio unavailable")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", LrcRedSyncFailure.Refused, null)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":\"Slow down.\"}", LrcRedSyncFailure.RateLimited, "Slow down.")]
    public async Task Sync_Refusal_KeepsTheStatusAndLrcRedsOwnWords(HttpStatusCode status, string body, LrcRedSyncFailure failure, string? message)
    {
        var router = AcousticRouter(() => Json(status, body));
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Equal(message, ex.ServerMessage);
        Assert.Single(router.Requests); // never retried
    }

    // lrc.red's real answer (curl, 2026-10-24: POST /s/UYB282688400/sync, an ISRC it does not
    // know) — the page's "lrc.red: We can't find the recording this comes from." / Try again.
    [Fact]
    public async Task Sync_RecordingNotFound_IsARefusalWithLrcRedsWords_AndReachesTheSessionLog()
    {
        var router = AcousticRouter(() => Json((HttpStatusCode)422, "{\n  \"error\": \"We can't find the recording this comes from.\"\n}\n"));
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));

        Assert.Equal(LrcRedSyncFailure.Refused, ex.Failure);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("We can't find the recording this comes from.", ex.ServerMessage);
        var log = DebugLog.Snapshot();
        Assert.Contains($"lrc.red model sync failed: Refused (422) — We can't find the recording this comes from. | isrc={AcousticIsrc}", log);
    }

    [Fact]
    public async Task Sync_NetworkFailure_IsNetwork()
    {
        var svc = new LrcRedLyricsService(new HttpClient(new Router(_ => throw new HttpRequestException("No such host is known."))));
        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));
        Assert.Equal(LrcRedSyncFailure.Network, ex.Failure);
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Sync_TooSlow_IsTimeout_NotACancel()
    {
        var router = new Router(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, SyncAnswer);
        });
        var svc = new LrcRedLyricsService(new HttpClient(router)) { ModelSyncTimeout = TimeSpan.FromMilliseconds(50) };

        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));
        Assert.Equal(LrcRedSyncFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task Sync_CancelledByTheCaller_IsACancel()
    {
        var started = new TaskCompletionSource();
        var router = new Router(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, SyncAnswer);
        });
        var svc = new LrcRedLyricsService(new HttpClient(router));
        using var cts = new CancellationTokenSource();

        var sync = svc.SyncWithModelAsync(AcousticIsrc, cts.Token);
        await started.Task;
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.IsNotType<LrcRedSyncException>(ex);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":\"USUM72413062\"}")]
    [InlineData("{\"source\":\"<tt><body><p>\"}")]
    public async Task Sync_AnswerWithoutReadableLyrics_IsBadResponse(string body)
    {
        var svc = new LrcRedLyricsService(new HttpClient(AcousticRouter(() => Json(HttpStatusCode.OK, body))));
        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));
        Assert.Equal(LrcRedSyncFailure.BadResponse, ex.Failure);
    }

    [Fact]
    public async Task Sync_AnswerWithLineTimingOnly_IsNotWordSynced_AndNotRemembered()
    {
        var lineOnly = "{\"title\":\"Die With A Smile (Acoustic)\",\"source\":" + System.Text.Json.JsonSerializer.Serialize(AcousticLineTtml) + "}";
        var router = AcousticRouter(() => Json(HttpStatusCode.OK, lineOnly));
        var svc = new LrcRedLyricsService(new HttpClient(router));

        var ex = await Assert.ThrowsAsync<LrcRedSyncException>(() => svc.SyncWithModelAsync(AcousticIsrc));
        Assert.Equal(LrcRedSyncFailure.NotWordSynced, ex.Failure);
        var search = await svc.SearchLyricsAsync("Lady Gaga", "Die With A Smile (Acoustic)", 251.7);
        Assert.Equal(2, LyricsSearchSelector.FormatRank(search));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../api/v1")]
    [InlineData("USUM7241306")]
    public async Task Sync_RefusesAnythingButAnIsrc_WithoutARequest(string isrc)
    {
        var router = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(router));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SyncWithModelAsync(isrc));
        Assert.Empty(router.Requests);
    }

    [Fact]
    public async Task Sync_UsesTheModelClient_WhenGiven()
    {
        var lookups = AcousticRouter(() => throw new InvalidOperationException("the model call must not use the lookup client"));
        var model = AcousticRouter();
        var svc = new LrcRedLyricsService(new HttpClient(lookups), new HttpClient(model));

        await svc.SyncWithModelAsync(AcousticIsrc);

        Assert.Empty(lookups.Requests);
        Assert.Single(model.Requests);
        Assert.Equal(Timeout.InfiniteTimeSpan, LrcRedLyricsService.CreateModelHttpClient().Timeout);
    }
}
