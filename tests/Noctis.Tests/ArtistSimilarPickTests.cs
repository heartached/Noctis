using System.Diagnostics;
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>GitHub #170 "when an artist is selected, similar artists are not updated": Similar
/// Artists searched Deezer by name every time and cached the list per artist, so the account
/// picked with Search Artist Info (#141) never reached it and the wrong artist's list stayed.</summary>
public class ArtistSimilarPickTests
{
    private const string Search = "https://api.deezer.com/search/artist";

    /// <summary>Two Deezer accounts named "Nova": the name search finds the popstar (id 1);
    /// the user's Nova is the indie one (id 2).</summary>
    private sealed class DeezerFake : HttpMessageHandler
    {
        public int Searches;
        public List<long> RelatedAsked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith(Search, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Searches);
                return Json("""{ "data": [ { "id": 1, "name": "Nova", "nb_fan": 900000 } ] }""");
            }
            if (url.StartsWith("https://api.deezer.com/artist/1/related", StringComparison.Ordinal))
            {
                lock (RelatedAsked) RelatedAsked.Add(1);
                return Json("""{ "data": [ { "id": 101, "name": "Pop Star A" }, { "id": 102, "name": "Pop Star B" } ] }""");
            }
            if (url.StartsWith("https://api.deezer.com/artist/2/related", StringComparison.Ordinal))
            {
                lock (RelatedAsked) RelatedAsked.Add(2);
                return Json("""{ "data": [ { "id": 201, "name": "Indie Band C" } ] }""");
            }
            if (url == "https://api.deezer.com/artist/1")
                return Json("""{ "id": 1, "name": "Nova", "nb_fan": 900000 }""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task GetAsync_PinnedAccount_AsksItsRelatedList_AndAPickChangeRefetches()
    {
        using var persistence = new TestPersistenceService();
        var handler = new DeezerFake();
        var service = new SimilarArtistsService(new HttpClient(handler), persistence);
        var id = Guid.NewGuid();

        var auto = await service.GetAsync(id, "Nova");
        Assert.Equal(new[] { "Pop Star A", "Pop Star B" }, auto.Select(a => a.Name));
        Assert.Equal(1, handler.Searches);

        // The pick wins over the fresh cached name-search list.
        var picked = await service.GetAsync(id, "Nova", pinnedDeezerId: 2);
        Assert.Equal(new[] { "Indie Band C" }, picked.Select(a => a.Name));
        Assert.Equal(1, handler.Searches);                                 // no name search for a pick

        // Survives a restart: a new service over the same data serves the pick's list from cache.
        var again = new SimilarArtistsService(new HttpClient(handler), persistence);
        Assert.Equal(new[] { "Indie Band C" }, (await again.GetAsync(id, "Nova", 2)).Select(a => a.Name));
        Assert.Equal(new long[] { 1, 2 }, handler.RelatedAsked);

        // "Use Automatic Artist Info" goes back to the name search.
        var reset = await again.GetAsync(id, "Nova");
        Assert.Equal(new[] { "Pop Star A", "Pop Star B" }, reset.Select(a => a.Name));
        Assert.Equal(2, handler.Searches);
    }

    [AvaloniaFact]
    public void Pick_ReplacesTheShownSimilarArtists_AndResetBringsTheAutomaticOnesBack()
    {
        using var persistence = new TestPersistenceService();
        var handler = new DeezerFake();
        var http = new HttpClient(handler);
        var images = new ArtistImageService(http, persistence);
        var similar = new SimilarArtistsService(http, persistence);
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());

        var vm = new ArtistDetailViewModel("Nova", lib, player, images: images, similar: similar);
        PumpUntil(() => vm.SimilarLoaded);
        Assert.Equal(new[] { "Pop Star A", "Pop Star B" }, vm.SimilarArtists.Select(r => r.Name));

        var pick = new DeezerArtistPhotos.SearchHit(2, "Nova", null, null, 4000, 3);
        var apply = vm.ApplyArtistPickAsync(pick);
        PumpUntil(() => apply.IsCompleted && vm.SimilarArtists.Select(r => r.Name).SequenceEqual(new[] { "Indie Band C" }));
        Assert.Equal(new[] { "Indie Band C" }, vm.SimilarArtists.Select(r => r.Name));
        Assert.Equal(new[] { "Indie Band C" }, vm.OverviewSimilar.Select(r => r.Name));

        // Reopening the page (or a restart) keeps the picked account's list.
        var reopened = new ArtistDetailViewModel("Nova", lib, player, images: images, similar: similar);
        PumpUntil(() => reopened.SimilarLoaded);
        Assert.Equal(new[] { "Indie Band C" }, reopened.SimilarArtists.Select(r => r.Name));

        var reset = vm.ResetArtistPickAsync();
        PumpUntil(() => reset.IsCompleted && vm.SimilarArtists.Count == 2);
        Assert.Equal(new[] { "Pop Star A", "Pop Star B" }, vm.SimilarArtists.Select(r => r.Name));
    }

    /// <summary>Picking account A (linked to MusicBrainz) then account B (no link anywhere) kept
    /// A's MusicBrainz pin, so About went on showing A's facts under B's photo.</summary>
    [AvaloniaFact]
    public void Pick_WithNoMusicBrainzLink_DropsAnEarlierPicksFacts()
    {
        using var persistence = new TestPersistenceService();
        var http = new HttpClient(new DeezerFake()); // MusicBrainz + Wikidata: 404 = no link
        var images = new ArtistImageService(http, persistence);
        var info = new ArtistInfoService(http, persistence);
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Nova", lib, player, images: images, info: info);
        info.SetPinnedMusicBrainzId(vm.Artist.Id, "056e4f3e-d505-4dad-8ec1-d04f521cbb56"); // the earlier pick

        var apply = vm.ApplyArtistPickAsync(new DeezerArtistPhotos.SearchHit(2, "Nova", null, null, 4000, 3));
        PumpUntil(() => apply.IsCompleted, seconds: 20);

        Assert.True(apply.IsCompletedSuccessfully);
        Assert.Null(info.GetPinnedMusicBrainzId(vm.Artist.Id));
    }

    private static void PumpUntil(Func<bool> done, double seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
