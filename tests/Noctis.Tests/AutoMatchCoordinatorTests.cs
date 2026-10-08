using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class AutoMatchCoordinatorTests
{
    private static Track SampleTrack() => new()
    {
        Title = "Lucid Dreams",
        Artist = "Juice WRLD",
        AlbumArtist = "Juice WRLD",
        Album = "Goodbye & Good Riddance",
        Duration = TimeSpan.FromSeconds(239),
        FilePath = "C:/music/lucid.flac",
    };

    private static AppSettings DeezerOff() => new() { DeezerEnabled = false };

    // Deezer enrichment uses a live HttpClient; with Deezer disabled the coordinator
    // returns only the identify result, so we can drive these tests offline.
    private static DeezerMetadataService NoNetworkDeezer()
        => new(new HttpClient(new ThrowingHandler()));

    [Fact]
    public async Task MatchAsync_ReturnsIdentifyResult_WhenDeezerDisabled()
    {
        var finder = new FakeFinder(new TagSuggestion(
            "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 0.9, "MusicBrainz"));

        var coord = new AutoMatchCoordinator(finder, NoNetworkDeezer(), DeezerOff);
        var hit = await coord.MatchAsync(SampleTrack());

        Assert.NotNull(hit);
        Assert.Equal("MusicBrainz", hit!.Source);
        Assert.Equal("Lucid Dreams", hit.Title);
        Assert.Equal(2018, hit.Year);
    }

    [Fact]
    public async Task MatchAsync_FinderThrows_ReturnsNull_WhenDeezerDisabled()
    {
        var finder = new FakeFinder(throws: true);

        var coord = new AutoMatchCoordinator(finder, NoNetworkDeezer(), DeezerOff);
        var hit = await coord.MatchAsync(SampleTrack());

        Assert.Null(hit);
    }

    [Fact]
    public void Merge_PrefersIdentifyCore_FillsRichFromEnrich()
    {
        var identify = new TagSuggestion("Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 0.9, "MusicBrainz");
        var enrich = new TagSuggestion("LUCID DREAMS", "JW", "GBGR", 2017, 0.0, "Deezer",
            AlbumArtist: "Juice WRLD", Genre: "Rap/Hip Hop", TrackNumber: 8, TrackCount: 17,
            DiscNumber: 1, Bpm: 84, Isrc: "USUM71808193");

        var merged = AutoMatchCoordinator.Merge(identify, enrich);

        // Core fields come from identify.
        Assert.Equal("Lucid Dreams", merged!.Title);
        Assert.Equal("Juice WRLD", merged.Artist);
        Assert.Equal("Goodbye & Good Riddance", merged.Album);
        Assert.Equal(2018, merged.Year);
        // Rich fields filled from enrich.
        Assert.Equal("Rap/Hip Hop", merged.Genre);
        Assert.Equal(8, merged.TrackNumber);
        Assert.Equal(17, merged.TrackCount);
        Assert.Equal(84, merged.Bpm);
        Assert.Equal("USUM71808193", merged.Isrc);
    }

    // Owner 10-08 (Search metadata revamp): a weak identify hit is another song, not a match.
    [Fact]
    public async Task MatchAsync_WeakIdentify_IsNotAMatch()
    {
        var finder = new FakeFinder(new TagSuggestion("Something Else", "Other", "X", 2001, 0.3, "MusicBrainz"));

        var coord = new AutoMatchCoordinator(finder, NoNetworkDeezer(), DeezerOff);

        Assert.Null(await coord.MatchAsync(SampleTrack()));
    }

    // Enrichment that lands on a different song must not lend it its ISRC/track #/genre.
    [Fact]
    public async Task MatchAsync_EnrichForAnotherSong_IsDiscarded()
    {
        var finder = new FakeFinder(new TagSuggestion(
            "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 0.95, "MusicBrainz"));
        var deezer = new DeezerMetadataService(new HttpClient(new OtherSongHandler()));

        var coord = new AutoMatchCoordinator(finder, deezer, () => new AppSettings());
        var hit = await coord.MatchAsync(SampleTrack());

        Assert.NotNull(hit);
        Assert.Equal("MusicBrainz", hit!.Source);
        Assert.Null(hit.Isrc);
        Assert.Null(hit.Genre);
    }

    private sealed class OtherSongHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.StartsWith("/search")
                ? """{"data":[{"id":5,"title":"Robbery","artist":{"name":"Juice WRLD"}}]}"""
                : path.StartsWith("/track/")
                    ? """{"id":5,"title":"Robbery","isrc":"USUG11900176","artist":{"name":"Juice WRLD"},"album":{"id":9,"title":"Death Race for Love"}}"""
                    : """{"id":9,"title":"Death Race for Love","genres":{"data":[{"name":"Rap/Hip Hop"}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Merge_NullIdentify_ReturnsEnrich()
    {
        var enrich = new TagSuggestion("t", "a", "al", 2020, 0.0, "Deezer", Genre: "Pop");
        var merged = AutoMatchCoordinator.Merge(null, enrich);
        Assert.Equal("Pop", merged!.Genre);
    }

    private sealed class FakeFinder : IMetadataFinderService
    {
        private readonly TagSuggestion? _hit;
        private readonly bool _throws;
        public FakeFinder(TagSuggestion? hit = null, bool throws = false) { _hit = hit; _throws = throws; }
        public Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
        {
            if (_throws) throw new InvalidOperationException("boom");
            IReadOnlyList<TagSuggestion> list = _hit is null ? Array.Empty<TagSuggestion>() : new[] { _hit };
            return Task.FromResult(list);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no network in test");
    }
}
