using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's artist photos: Deezer through the shared <see cref="DeezerArtistPhotos"/>, sending
/// only the name, cached as files under the data directory, a miss remembered for a while, a
/// failure or a slow answer remembered not at all, and never more than a couple at once.
/// </summary>
public class MobileArtistPhotoSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"), "artist_images");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public ConcurrentQueue<string> Urls { get; } = new();
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((r, _) => Task.FromResult(respond(r))) { }
        public int Searches => Urls.Count(u => u.StartsWith(DeezerArtistPhotos.SearchUrl, StringComparison.Ordinal));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Enqueue(request.RequestUri!.AbsoluteUri);
            return _respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image()
    {
        var bytes = new byte[2048];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } };
    }

    private const string DeezerPhoto = "https://e-cdns-images.dzcdn.net/images/artist/abc/1000x1000-000000-80-0-0.jpg";

    /// <summary>Deezer knows the artist: the search, then the photo at the 1000 px original.</summary>
    private static Handler Knows(string name) => new(r =>
        r.RequestUri!.ToString().StartsWith(DeezerArtistPhotos.SearchUrl, StringComparison.Ordinal)
            ? Json($$"""{ "data": [ { "id": 7, "name": "{{name}}", "nb_fan": 100, "picture_xl": "{{DeezerPhoto}}" } ] }""")
            : Image());

    private DeezerArtistPhotoSource Source(Handler handler, TimeSpan? timeout = null, Func<DateTime>? clock = null) =>
        new(new DeezerArtistPhotos(new HttpClient(handler)), _dir, timeout: timeout, pacing: TimeSpan.Zero, utcNow: clock);

    [Fact]
    public async Task AMatch_IsSavedUnderTheDataDirectory_AndServedFromThereAfterwards()
    {
        var handler = Knows("Bruno Mars");
        var source = Source(handler);
        Assert.Null(source.CachedPhoto("Bruno Mars"));

        var path = await source.GetPhotoAsync("Bruno Mars", CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.EndsWith(".jpg", path);
        // Only the name went out, and the phone asked for Deezer's 1000 px original (not the
        // desktop's 1800 px upgrade).
        Assert.Equal("https://api.deezer.com/search/artist?q=Bruno%20Mars&limit=10", handler.Urls.First());
        Assert.Equal(DeezerPhoto, handler.Urls.Last());
        Assert.Equal(path, source.CachedPhoto("bruno mars "));   // the name's case and spacing do not matter

        // A cache hit makes no request, also from a fresh source over the same folder (restart).
        var before = handler.Urls.Count;
        Assert.Equal(path, await source.GetPhotoAsync("Bruno Mars", CancellationToken.None));
        Assert.Equal(path, await Source(handler).GetPhotoAsync("Bruno Mars", CancellationToken.None));
        Assert.Equal(before, handler.Urls.Count);
    }

    [Fact]
    public async Task AMiss_IsRemembered_UntilTheRetryTime()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new Handler(_ => Json("""{ "data": [] }"""));
        var source = Source(handler, clock: () => now);

        Assert.Null(await source.GetPhotoAsync("Nobody Known", CancellationToken.None));
        Assert.Equal(1, handler.Searches);
        Assert.Single(Directory.GetFiles(_dir, "*.miss"));

        Assert.Null(await source.GetPhotoAsync("Nobody Known", CancellationToken.None));
        Assert.Equal(1, handler.Searches);                                   // not asked again

        now += DeezerArtistPhotoSource.DefaultMissRetryAfter + TimeSpan.FromMinutes(1);
        Assert.Null(await source.GetPhotoAsync("Nobody Known", CancellationToken.None));
        Assert.Equal(2, handler.Searches);                                   // retried once due
    }

    [Fact]
    public async Task AFailedOrSlowLookup_IsNotRemembered_AndReturnsNull()
    {
        // Slower than the timeout: the request is cancelled and null comes back.
        var slow = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });
        var source = Source(slow, timeout: TimeSpan.FromMilliseconds(150));
        Assert.Null(await source.GetPhotoAsync("Slow Artist", CancellationToken.None));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);

        // A server error is no answer either: asked again next time.
        var failing = new Handler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var source2 = Source(failing);
        Assert.Null(await source2.GetPhotoAsync("Error Artist", CancellationToken.None));
        Assert.Null(await source2.GetPhotoAsync("Error Artist", CancellationToken.None));
        Assert.Equal(2, failing.Searches);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.miss").Length > 0);
    }

    [Fact]
    public async Task UnknownAndBlankNames_AreNeverAsked()
    {
        var handler = Knows("x");
        var source = Source(handler);
        Assert.Null(await source.GetPhotoAsync("Unknown Artist", CancellationToken.None));
        Assert.Null(await source.GetPhotoAsync("  ", CancellationToken.None));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task TheLastWaiterLeaving_StopsTheLookup()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Json("{}");
        });
        var source = Source(handler, timeout: TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource();

        var ask = source.GetPhotoAsync("Scrolled Away", cts.Token);
        while (handler.Urls.IsEmpty) await Task.Delay(5);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the request itself was cancelled
    }

    [Fact]
    public async Task OneLookupPerName_AndAtMostTwoAtOnce()
    {
        var inFlight = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (r, _) =>
        {
            var url = r.RequestUri!.ToString();
            if (!url.StartsWith(DeezerArtistPhotos.SearchUrl, StringComparison.Ordinal)) return Image();
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await release.Task;
            Interlocked.Decrement(ref inFlight);
            var name = Uri.UnescapeDataString(url.Split("q=")[1].Split('&')[0]);
            return Json($$"""{ "data": [ { "name": "{{name}}", "picture_xl": "{{DeezerPhoto}}" } ] }""");
        });
        var source = Source(handler);

        var asks = new[] { "A1", "A2", "A3", "A4", "A1" }.Select(n => source.GetPhotoAsync(n, CancellationToken.None)).ToList();
        await Task.Delay(200);
        Assert.Equal(DeezerArtistPhotoSource.MaxConcurrentLookups, Volatile.Read(ref inFlight));
        release.SetResult();
        var paths = await Task.WhenAll(asks);

        Assert.All(paths, Assert.NotNull);
        Assert.Equal(paths[0], paths[4]);
        Assert.Equal(4, handler.Searches);                 // "A1" asked twice, looked up once
        Assert.True(peak <= DeezerArtistPhotoSource.MaxConcurrentLookups, $"peak {peak}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    /// <summary>The shared lookup tells "Deezer has no such artist" from "no answer".</summary>
    [Fact]
    public async Task TheSharedLookup_SaysWhetherDeezerAnswered()
    {
        var empty = new DeezerArtistPhotos(new HttpClient(new Handler(_ => Json("""{ "data": [] }"""))));
        Assert.Equal(new DeezerArtistPhotos.Lookup(null, true), await empty.FindAsync("Nobody"));

        var down = new DeezerArtistPhotos(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        Assert.Equal(new DeezerArtistPhotos.Lookup(null, false), await down.FindAsync("Nobody"));
    }
}
