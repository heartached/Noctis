using System.Net;
using System.Text;
using System.Text.Json;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #197: the computer's plays wait in pending_scrobbles.json while Last.fm / ListenBrainz is
/// signed out or can't be reached, and go out once it can be sent to again.
/// </summary>
public class ScrobbleQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private string QueuePath => Path.Combine(_dir, "pending_scrobbles.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A fake pair of services: whether each can be sent to, what each answers, what each got.</summary>
    private sealed class Services
    {
        public bool LastFmUp { get; set; } = true;
        public bool ListenBrainzUp { get; set; } = true;
        public Func<IReadOnlyList<ScrobbleEntry>, ScrobbleSendResult> LastFmAnswer { get; set; } = _ => ScrobbleSendResult.Ok;
        public Func<IReadOnlyList<ScrobbleEntry>, ScrobbleSendResult> ListenBrainzAnswer { get; set; } = _ => ScrobbleSendResult.Ok;
        public List<IReadOnlyList<ScrobbleEntry>> LastFmCalls { get; } = new();
        public List<IReadOnlyList<ScrobbleEntry>> ListenBrainzCalls { get; } = new();

        public bool CanSend(ScrobbleService s) => s == ScrobbleService.LastFm ? LastFmUp : ListenBrainzUp;

        public Task<ScrobbleSendResult> Send(ScrobbleService s, IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct)
        {
            if (s == ScrobbleService.LastFm) { LastFmCalls.Add(plays); return Task.FromResult(LastFmAnswer(plays)); }
            ListenBrainzCalls.Add(plays);
            return Task.FromResult(ListenBrainzAnswer(plays));
        }
    }

    private ScrobbleQueue Create(Services services, Clock? clock = null) =>
        new(QueuePath, services.CanSend, services.Send, clock ?? new Clock()) { AutoRetry = false };

    private static ScrobbleEntry Play(string title, DateTimeOffset at) => new("Artist", title, "Album", 200, at.ToUnixTimeSeconds());

    [Fact]
    public async Task SignedOut_PlaysWait_AndGoOutInOneBatch_OnceSignedIn()
    {
        var services = new Services { LastFmUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        for (var i = 0; i < 3; i++)
        {
            queue.Add(Play($"Song {i}", clock.Now.AddMinutes(-10 * (3 - i))), lastFm: true, listenBrainz: false);
            await queue.FlushAsync(now: true);
        }
        Assert.Empty(services.LastFmCalls);
        Assert.Equal(3, queue.PendingCount(ScrobbleService.LastFm));

        services.LastFmUp = true;
        await queue.FlushAsync(now: true);

        var call = Assert.Single(services.LastFmCalls);
        Assert.Equal(new[] { "Song 0", "Song 1", "Song 2" }, call.Select(p => p.Title));
        Assert.Equal(0, queue.PendingCount(ScrobbleService.LastFm));
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Queue_SurvivesARestart()
    {
        var services = new Services { LastFmAnswer = _ => ScrobbleSendResult.Retry };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("Offline song", clock.Now.AddMinutes(-5)), lastFm: true, listenBrainz: true);
        await queue.FlushAsync(now: true);
        Assert.Equal(1, queue.PendingCount(ScrobbleService.LastFm));
        Assert.Equal(0, queue.PendingCount(ScrobbleService.ListenBrainz)); // ListenBrainz took it

        services.LastFmAnswer = _ => ScrobbleSendResult.Ok;
        var afterRestart = Create(services, clock);
        Assert.Equal(1, afterRestart.PendingCount(ScrobbleService.LastFm));
        await afterRestart.FlushAsync();
        Assert.Equal("Offline song", services.LastFmCalls.Last().Single().Title);
        Assert.Equal(0, afterRestart.PendingCount(ScrobbleService.LastFm));
    }

    [Fact]
    public async Task FailedSend_BacksOff_UnlessANewPlayOrSignInAsksNow()
    {
        var services = new Services { LastFmAnswer = _ => ScrobbleSendResult.Retry };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("A", clock.Now.AddMinutes(-5)), true, false);
        await queue.FlushAsync(now: true);
        Assert.Single(services.LastFmCalls);

        await queue.FlushAsync(); // timer-style flush inside the backoff: skipped
        Assert.Single(services.LastFmCalls);

        clock.Now = clock.Now.AddMinutes(1);
        services.LastFmAnswer = _ => ScrobbleSendResult.Ok;
        await queue.FlushAsync(); // backoff over
        Assert.Equal(2, services.LastFmCalls.Count);
        Assert.Equal(0, queue.PendingCount(ScrobbleService.LastFm));
    }

    [Fact]
    public async Task SessionInvalid_KeepsThePlays()
    {
        var services = new Services { ListenBrainzAnswer = _ => ScrobbleSendResult.SessionInvalid };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("A", clock.Now.AddMinutes(-5)), false, true);
        await queue.FlushAsync(now: true);
        Assert.Equal(1, queue.PendingCount(ScrobbleService.ListenBrainz));
    }

    [Fact]
    public async Task MoreThan50_GoInBatchesOf50_OldestFirst()
    {
        var services = new Services { LastFmUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        for (var i = 0; i < 120; i++)
            queue.Add(Play($"S{i:000}", clock.Now.AddMinutes(-200 + i)), true, false);

        services.LastFmUp = true;
        await queue.FlushAsync(now: true);

        Assert.Equal(new[] { 50, 50, 20 }, services.LastFmCalls.Select(c => c.Count));
        Assert.Equal("S000", services.LastFmCalls[0][0].Title);
        Assert.Equal("S119", services.LastFmCalls[2][^1].Title);
    }

    [Fact]
    public async Task ARejectedPlay_IsDropped_WithoutTakingTheGoodOnes()
    {
        var services = new Services
        {
            LastFmUp = false,
            LastFmAnswer = plays => plays.Any(p => p.Title == "Bad") ? ScrobbleSendResult.Rejected : ScrobbleSendResult.Ok,
        };
        var clock = new Clock();
        var queue = Create(services, clock);
        foreach (var (title, i) in new[] { "A", "Bad", "C", "D" }.Select((t, i) => (t, i)))
            queue.Add(Play(title, clock.Now.AddMinutes(-10 + i)), true, false);

        services.LastFmUp = true;
        await queue.FlushAsync(now: true);

        var accepted = services.LastFmCalls.Where(c => c.All(p => p.Title != "Bad")).SelectMany(c => c).Select(p => p.Title);
        Assert.Equal(new[] { "A", "C", "D" }, accepted.OrderBy(t => t));
        Assert.Equal(0, queue.PendingCount(ScrobbleService.LastFm));
    }

    [Fact]
    public async Task LastFm_DropsPlaysOlderThan14Days_ListenBrainzStillGetsThem()
    {
        var services = new Services { LastFmUp = false, ListenBrainzUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("Old", clock.Now.AddDays(-15)), true, true);
        queue.Add(Play("Recent", clock.Now.AddDays(-13)), true, true);

        services.LastFmUp = services.ListenBrainzUp = true;
        await queue.FlushAsync(now: true);

        Assert.Equal(new[] { "Recent" }, services.LastFmCalls.Single().Select(p => p.Title));
        Assert.Equal(new[] { "Old", "Recent" }, services.ListenBrainzCalls.Single().Select(p => p.Title));
    }

    [Fact]
    public void SamePlayTwice_IsQueuedOnce_WithBothServices()
    {
        var services = new Services { LastFmUp = false, ListenBrainzUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        var play = Play("A", clock.Now.AddMinutes(-5));
        queue.Add(play, true, false);
        queue.Add(play, false, true);

        Assert.Equal(1, queue.PendingCount(ScrobbleService.LastFm));
        Assert.Equal(1, queue.PendingCount(ScrobbleService.ListenBrainz));
        Assert.Single(JsonSerializer.Deserialize<List<PendingScrobble>>(File.ReadAllText(QueuePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
    }

    [Fact]
    public void QueueIsCapped_OldestGoFirst()
    {
        var services = new Services { LastFmUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        for (var i = 0; i < ScrobbleQueue.MaxQueued + 5; i++)
            queue.Add(Play($"S{i}", clock.Now.AddSeconds(-100000 + i)), true, false);

        Assert.Equal(ScrobbleQueue.MaxQueued, queue.PendingCount(ScrobbleService.LastFm));
        var kept = JsonSerializer.Deserialize<List<PendingScrobble>>(File.ReadAllText(QueuePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.DoesNotContain(kept, p => p.Title == "S0");
        Assert.Contains(kept, p => p.Title == $"S{ScrobbleQueue.MaxQueued + 4}");
    }

    [Fact]
    public void Forget_DropsOnlyThatService()
    {
        var services = new Services { LastFmUp = false, ListenBrainzUp = false };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("A", clock.Now.AddMinutes(-5)), true, true);
        queue.Add(Play("B", clock.Now.AddMinutes(-4)), true, false);

        queue.Forget(ScrobbleService.LastFm);

        Assert.Equal(0, queue.PendingCount(ScrobbleService.LastFm));
        Assert.Equal(1, queue.PendingCount(ScrobbleService.ListenBrainz));
    }

    [Fact]
    public async Task ASendThatThrows_KeepsThePlays()
    {
        var services = new Services { LastFmAnswer = _ => throw new HttpRequestException("offline") };
        var clock = new Clock();
        var queue = Create(services, clock);
        queue.Add(Play("A", clock.Now.AddMinutes(-5)), true, false);
        await queue.FlushAsync(now: true);
        Assert.Equal(1, queue.PendingCount(ScrobbleService.LastFm));
    }

    // ── The desktop ListenBrainz client's batch send ──

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ScrobbleSendResult.Ok)]
    [InlineData(HttpStatusCode.Unauthorized, ScrobbleSendResult.SessionInvalid)]
    [InlineData(HttpStatusCode.BadRequest, ScrobbleSendResult.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, ScrobbleSendResult.Retry)]
    [InlineData(HttpStatusCode.BadGateway, ScrobbleSendResult.Retry)]
    [InlineData(HttpStatusCode.NotFound, ScrobbleSendResult.Retry)] // a self-hosted server moving must not cost the plays
    public async Task ListenBrainz_SendListens_ClassifiesTheAnswer(HttpStatusCode status, ScrobbleSendResult expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var svc = new ListenBrainzService(new HttpClient(handler));
        svc.Configure("tok");
        svc.SetApiUrl("http://192.168.1.20:4110/apis/listenbrainz");

        var result = await svc.SendListensAsync(new[] { Play("A", DateTimeOffset.UtcNow), Play("B", DateTimeOffset.UtcNow.AddMinutes(4)) });

        Assert.Equal(expected, result);
        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz/1/submit-listens", url);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("import", doc.RootElement.GetProperty("listen_type").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("payload").GetArrayLength());
        Assert.Equal("A", doc.RootElement.GetProperty("payload")[0].GetProperty("track_metadata").GetProperty("track_name").GetString());
    }

    [Fact]
    public async Task ListenBrainz_SendListens_Offline_IsRetry_AndSignedOut_IsSessionInvalid()
    {
        var offline = new ListenBrainzService(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))));
        offline.Configure("tok");
        Assert.Equal(ScrobbleSendResult.Retry, await offline.SendListensAsync(new[] { Play("A", DateTimeOffset.UtcNow) }));

        var signedOut = new ListenBrainzService(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        Assert.Equal(ScrobbleSendResult.SessionInvalid, await signedOut.SendListensAsync(new[] { Play("A", DateTimeOffset.UtcNow) }));
    }
}
