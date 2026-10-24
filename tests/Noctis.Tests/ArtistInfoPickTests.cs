using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>GitHub #141 "Sometimes incorrect metadata is used for artist info": the automatic
/// Deezer match took any search hit, related or not, and the artist page had no way to pick
/// the right artist by hand.</summary>
public class ArtistInfoPickTests
{
    private const string Search = "https://api.deezer.com/search/artist";

    /// <summary>Deezer lacks "Low" under that exact name and answers with whoever shares the
    /// word: the first hit was taken (rank "starts with" for "Lower Than Atlantis", rank
    /// "otherwise" for "Glow"), so a stranger's photo landed on the page.</summary>
    [Fact]
    public async Task FetchAndCacheAsync_NoRelatedName_TakesNoStrangersPhoto()
    {
        using var persistence = new TestPersistenceService();
        var handler = new Handler(r => r.RequestUri!.ToString().StartsWith(Search, StringComparison.Ordinal)
            ? Json("""
              {
                "data": [
                  { "id": 11, "name": "Lower Than Atlantis", "nb_fan": 90000, "picture_xl": "https://images.example/lta.jpg" },
                  { "id": 12, "name": "Glow", "nb_fan": 400, "picture_xl": "https://images.example/glow.jpg" }
                ]
              }
              """)
            : Image());
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Low" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Empty(handler.Images);
        Assert.Null(artist.ImagePath);
    }

    [Theory]
    [InlineData("Ghost", "Ghost", 0)]
    [InlineData("Ghost B.C.", "Ghost", 2)]
    [InlineData("Ghostface Killah", "Ghost", 10)]
    [InlineData("The Weeknd", "Weeknd", 3)]
    [InlineData("Asia", "Sia", 10)]
    [InlineData("Bad Bunny Chapin", "Bad Bunny", 2)]
    public void RankMatch_PartialNamesCountOnlyAsWholeWords(string result, string query, int expected)
        => Assert.Equal(expected, DeezerArtistPhotos.RankMatch(result, query));

    /// <summary>A pinned artist's photo, its refresh and its fan count come from the picked
    /// account, never the name search; resetting goes back to the name search.</summary>
    [Fact]
    public async Task PinnedAccount_DrivesPhotoRefreshAndFans_UntilReset()
    {
        using var persistence = new TestPersistenceService();
        var searches = 0;
        var byId = 0;
        var handler = new Handler(r =>
        {
            var url = r.RequestUri!.ToString();
            if (url.StartsWith(Search, StringComparison.Ordinal))
            {
                searches++;
                return Json("""{ "data": [ { "id": 1, "name": "Nova", "nb_fan": 900000, "picture_xl": "https://images.example/popstar.jpg" } ] }""");
            }
            if (url == "https://api.deezer.com/artist/2")
            {
                byId++;
                return Json("""{ "id": 2, "name": "Nova", "nb_fan": 4000, "picture_xl": "https://images.example/indie.jpg" }""");
            }
            return Image();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Nova" };
        var pick = new DeezerArtistPhotos.SearchHit(2, "Nova", null, "https://images.example/indie.jpg", 4000, 3);

        var path = await service.PinDeezerArtistAsync(artist, pick);

        Assert.NotNull(path);
        Assert.Equal(2, service.GetPinnedDeezerId(artist.Id));
        Assert.Equal(4000, service.TryGetCachedFanCount(artist.Id));
        Assert.Equal("https://images.example/indie.jpg", handler.Images.Single());

        // A due refresh asks the picked account, not the name search (which would answer the popstar).
        var src = Path.Combine(persistence.DataDirectory, "artwork", "artists", $"{artist.Id}.src");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddDays(-2));
        await service.FetchAndCacheAsync(new[] { artist });
        Assert.Equal(0, searches);
        Assert.Equal(1, byId);
        Assert.Single(handler.Images);

        // Survives a restart (a new service over the same data).
        var again = new ArtistImageService(new HttpClient(handler), persistence);
        Assert.Equal(2, again.GetPinnedDeezerId(artist.Id));

        await again.ClearDeezerPinAsync(artist);
        Assert.Null(again.GetPinnedDeezerId(artist.Id));
        Assert.Equal(1, searches);
        Assert.Equal("https://images.example/popstar.jpg", handler.Images.Last());
        Assert.Equal(900000, again.TryGetCachedFanCount(artist.Id));
    }

    [Fact]
    public async Task SearchAsync_ListsEveryAccount_PhotoOrNot()
    {
        var handler = new Handler(_ => Json("""
            {
              "data": [
                { "id": 1, "name": "Nova", "nb_fan": 900000, "nb_album": 12, "picture_medium": "https://images.example/s1.jpg", "picture_xl": "https://images.example/1.jpg" },
                { "id": 2, "name": "Nova", "nb_fan": 4000, "nb_album": 3, "picture_medium": "https://cdn-images.dzcdn.net/images/artist//250x250-000000-80-0-0.jpg", "picture_xl": "https://cdn-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg" },
                { "name": "No id" }
              ]
            }
            """));
        var hits = await new DeezerArtistPhotos(new HttpClient(handler)).SearchAsync("Nova");

        Assert.Equal(new long[] { 1, 2 }, hits.Select(h => h.Id));
        Assert.Equal("https://images.example/s1.jpg", hits[0].ThumbnailUrl);
        Assert.Null(hits[1].ThumbnailUrl);
        Assert.Null(hits[1].ImageUrl);
        Assert.Equal(12, hits[0].Albums);
    }

    [Fact]
    public void ParseUrlArtist_TakesTheOneLinkedArtist()
    {
        using var one = JsonDocument.Parse("""
            { "resource": "https://www.deezer.com/artist/27",
              "relations": [ { "type": "streaming", "artist": { "id": "056e4f3e-d505-4dad-8ec1-d04f521cbb56", "name": "Daft Punk" } } ] }
            """);
        Assert.Equal("056e4f3e-d505-4dad-8ec1-d04f521cbb56", ArtistInfoService.ParseUrlArtist(one.RootElement));

        using var two = JsonDocument.Parse("""
            { "relations": [ { "artist": { "id": "056e4f3e-d505-4dad-8ec1-d04f521cbb56" } },
                             { "artist": { "id": "11111111-2222-3333-4444-555555555555" } } ] }
            """);
        Assert.Null(ArtistInfoService.ParseUrlArtist(two.RootElement));
    }

    [Fact]
    public void PinnedMusicBrainzId_PersistsAndClears()
    {
        using var persistence = new TestPersistenceService();
        var info = new ArtistInfoService(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), persistence);
        var id = Guid.NewGuid();

        info.SetPinnedMusicBrainzId(id, "056e4f3e-d505-4dad-8ec1-d04f521cbb56");
        Assert.Equal("056e4f3e-d505-4dad-8ec1-d04f521cbb56",
            new ArtistInfoService(new HttpClient(), persistence).GetPinnedMusicBrainzId(id));
        info.SetPinnedMusicBrainzId(id, null);
        Assert.Null(info.GetPinnedMusicBrainzId(id));
    }

    [AvaloniaFact]
    public async Task Picker_MarksAndPreselectsTheCurrentPick_AndAppliesTheChosenOne()
    {
        DeezerArtistPhotos.SearchHit? applied = null;
        var closed = false;
        var vm = new ArtistInfoSearchViewModel("Nova", currentId: 2,
            (q, _) => Task.FromResult<IReadOnlyList<DeezerArtistPhotos.SearchHit>>(new[]
            {
                new DeezerArtistPhotos.SearchHit(1, "Nova", null, null, 900000, 12),
                new DeezerArtistPhotos.SearchHit(2, "Nova", null, null, 4000, 3),
            }),
            thumbnail: null,
            hit => { applied = hit; return Task.CompletedTask; });
        vm.Closed += (_, _) => closed = true;

        await vm.Search();

        Assert.Equal(2, vm.Results.Count);
        Assert.True(vm.Results[1].IsCurrent);
        Assert.Same(vm.Results[1], vm.SelectedResult);
        vm.SelectedResult = vm.Results[0];
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, applied?.Id);
        Assert.True(closed);
    }

    // ── Harness ──

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image()
    {
        // Over the 5 KB placeholder purge threshold, with a JPEG header.
        var bytes = new byte[6 * 1024];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } }
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Images { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "images.example")
                Images.Add(request.RequestUri.ToString());
            return Task.FromResult(respond(request));
        }
    }
}
