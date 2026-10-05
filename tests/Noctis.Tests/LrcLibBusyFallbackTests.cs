using System.Net;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Live check 10-05: Edit Info's "Search lyrics" said "Search failed — check your internet
/// connection" on a machine that was online. LRCLIB had answered /api/get with 503
/// "ServerOverloaded … please retry in a moment" while /api/search answered 200; the code
/// gave up on the /get error before trying /search. Now: one retry of a busy answer, and a
/// /get error falls through to /search (the lyrics page had the same flow).
/// </summary>
public class LrcLibBusyFallbackTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, Interlocked.Increment(ref Count)));
    }

    private static HttpResponseMessage R(HttpStatusCode status, string body = "") => new(status) { Content = new StringContent(body) };

    private const string Found = """{"id":1,"trackName":"Song","artistName":"Artist","duration":200,"syncedLyrics":"[00:01.00]hi"}""";

    private static LrcLibService Service(Handler h) => new(new HttpClient(h)) { BusyRetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task Get_BusyOnce_IsRetried_AndAnswers()
    {
        var h = new Handler((_, n) => n == 1 ? R(HttpStatusCode.ServiceUnavailable, """{"name":"ServerOverloaded"}""") : R(HttpStatusCode.OK, Found));

        var result = await Service(h).GetLyricsAsync("Artist", "Song", 200);

        Assert.Equal("[00:01.00]hi", result!.SyncedLyrics);
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public async Task Get_StillBusy_AfterTheRetry_IsAProviderError()
    {
        var h = new Handler((_, _) => R(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<LyricsProviderException>(() => Service(h).GetLyricsAsync("Artist", "Song", 200));
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public async Task Search_TooManyRequests_IsRetried()
    {
        var h = new Handler((_, n) => n == 1 ? R(HttpStatusCode.TooManyRequests) : R(HttpStatusCode.OK, "[" + Found + "]"));

        Assert.Single(await Service(h).SearchLyricsAsync("Artist", "Song"));
        Assert.Equal(2, h.Count);
    }

    /// <summary>/get busy, /search fine — the exact live failure.</summary>
    private static Handler GetBusySearchFine() => new((req, _) =>
        req.RequestUri!.AbsolutePath.EndsWith("/get") ? R(HttpStatusCode.ServiceUnavailable) : R(HttpStatusCode.OK, "[" + Found + "]"));

    [Fact]
    public async Task LyricsPage_GetError_FallsThroughToSearch()
    {
        var result = await LyricsViewModel.FetchFromLrcLibAsync(Service(GetBusySearchFine()), "Artist", "Song", 200, CancellationToken.None);

        Assert.Equal("[00:01.00]hi", result!.SyncedLyrics);
    }

    [Fact]
    public async Task LyricsPage_BothFail_StillAProviderError()
    {
        var h = new Handler((_, _) => R(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<LyricsProviderException>(() =>
            LyricsViewModel.FetchFromLrcLibAsync(Service(h), "Artist", "Song", 200, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task EditInfo_GetBusy_StillFindsTheLyricsThroughSearch()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "Artist", Album = "A", FilePath = Path.Combine(Path.GetTempPath(), "noctis-busy", "song.flac"), Duration = TimeSpan.FromSeconds(200) };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, null!, lib, new TestPersistenceService(), new FakeAnimatedCoverService(),
            lrcLib: Service(GetBusySearchFine()));

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.Equal("Lyrics found", vm.SyncedLyricsSearchStatus);
        Assert.Equal("[00:01.00]hi", vm.SyncedLyrics);
    }

    [AvaloniaFact]
    public async Task EditInfo_BothFail_SaysLrclibDidntAnswer_NotCheckYourInternet()
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "Song", Artist = "Artist", Album = "A", FilePath = Path.Combine(Path.GetTempPath(), "noctis-busy", "song.flac"), Duration = TimeSpan.FromSeconds(200) };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, null!, lib, new TestPersistenceService(), new FakeAnimatedCoverService(),
            lrcLib: Service(new Handler((_, _) => R(HttpStatusCode.ServiceUnavailable))));

        await vm.SearchSyncedLyricsCommand.ExecuteAsync(null);

        Assert.Contains("LRCLIB didn't answer", vm.SyncedLyricsSearchStatus);
        Assert.DoesNotContain("internet connection", vm.SyncedLyricsSearchStatus);
    }
}
