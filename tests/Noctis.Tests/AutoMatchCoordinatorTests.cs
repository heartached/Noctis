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
        public bool HasFingerprinting => false;
        public bool HasApiKey => false;
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
