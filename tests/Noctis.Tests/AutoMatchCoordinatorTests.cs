using System;
using System.Collections.Generic;
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

    private static AppSettings AllEnabled() => new()
    {
        ITunesEnabled = true,
        LrcLibEnabled = true,
        AcoustIdEnabled = true,
        MusicBrainzEnabled = true,
        DeezerEnabled = true,
    };

    [Fact]
    public async Task MatchAsync_AggregatesAllProviders()
    {
        var finder = new FakeFinder(new TagSuggestion("Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 0.9, "MusicBrainz"));
        var art = new FakeArtwork(new ITunesArtworkService.ArtworkCandidate(1, "Goodbye & Good Riddance", "Juice WRLD", "thumb", "std", "hi", "view"));
        var lyrics = new FakeLyrics(new LrcLibResult { SyncedLyrics = "[00:01.00]line", PlainLyrics = "line" });

        var coord = new AutoMatchCoordinator(finder, art, lyrics, AllEnabled);
        var p = await coord.MatchAsync(SampleTrack());

        Assert.NotNull(p.Tags);
        Assert.Equal("MusicBrainz", p.Tags!.Source);
        Assert.NotNull(p.Artwork);
        Assert.Equal("hi", p.Artwork!.HiResUrl);
        Assert.Equal("[00:01.00]line", p.SyncedLyrics);
        Assert.Equal("line", p.PlainLyrics);
        Assert.True(p.HasAnything);
    }

    [Fact]
    public async Task MatchAsync_OneProviderThrows_OthersStillPopulate()
    {
        var finder = new FakeFinder(throws: true);
        var art = new FakeArtwork(new ITunesArtworkService.ArtworkCandidate(1, "A", "B", "t", "s", "hi", "v"));
        var lyrics = new FakeLyrics(new LrcLibResult { PlainLyrics = "words" });

        var coord = new AutoMatchCoordinator(finder, art, lyrics, AllEnabled);
        var p = await coord.MatchAsync(SampleTrack());

        Assert.Null(p.Tags);           // finder threw → tags null
        Assert.NotNull(p.Artwork);     // art survived
        Assert.Equal("words", p.PlainLyrics);
    }

    [Fact]
    public async Task MatchAsync_RespectsDisabledToggles()
    {
        var finder = new FakeFinder(new TagSuggestion("t", "a", "al", null, 0.5, "Deezer"));
        var art = new FakeArtwork(new ITunesArtworkService.ArtworkCandidate(1, "A", "B", "t", "s", "hi", "v"));
        var lyrics = new FakeLyrics(new LrcLibResult { SyncedLyrics = "[00:01.00]x" });
        var settings = new AppSettings { ITunesEnabled = false, LrcLibEnabled = false };

        var coord = new AutoMatchCoordinator(finder, art, lyrics, () => settings);
        var p = await coord.MatchAsync(SampleTrack());

        Assert.NotNull(p.Tags);        // tag finder owns its own toggles, always invoked
        Assert.Null(p.Artwork);        // iTunes disabled
        Assert.Null(p.SyncedLyrics);   // lyrics disabled
        Assert.False(art.WasCalled);   // disabled → never called
    }

    // ── Fakes ──

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

    private sealed class FakeArtwork : IAlbumArtworkSearch
    {
        private readonly ITunesArtworkService.ArtworkCandidate? _candidate;
        public bool WasCalled { get; private set; }
        public FakeArtwork(ITunesArtworkService.ArtworkCandidate? candidate) { _candidate = candidate; }
        public Task<IReadOnlyList<ITunesArtworkService.ArtworkCandidate>> SearchAlbumsAsync(
            string artist, string album, int limit = 8, CancellationToken ct = default)
        {
            WasCalled = true;
            IReadOnlyList<ITunesArtworkService.ArtworkCandidate> list =
                _candidate is null ? Array.Empty<ITunesArtworkService.ArtworkCandidate>() : new[] { _candidate };
            return Task.FromResult(list);
        }
    }

    private sealed class FakeLyrics : ILrcLibService
    {
        private readonly LrcLibResult? _result;
        public FakeLyrics(LrcLibResult? result) { _result = result; }
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds)
            => Task.FromResult(_result);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName)
            => Task.FromResult(new List<LrcLibResult>());
    }
}
