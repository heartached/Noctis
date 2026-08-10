using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mini player form-factor thresholds (window size → layout) and the search
/// drawer's library filter ranking.
/// </summary>
public class MiniPlayerFormTests
{
    [Theory]
    // Tiny square → icon
    [InlineData(176, 176, MiniPlayerForm.Icon)]
    [InlineData(230, 260, MiniPlayerForm.Icon)]
    // Short and wide → bar (even when narrow enough to look card-ish)
    [InlineData(420, 172, MiniPlayerForm.Bar)]
    [InlineData(600, 200, MiniPlayerForm.Bar)]
    [InlineData(300, 205, MiniPlayerForm.Bar)]
    // Default card
    [InlineData(340, 432, MiniPlayerForm.Card)]
    [InlineData(300, 260, MiniPlayerForm.Card)]
    // Tall → large icon
    [InlineData(340, 520, MiniPlayerForm.LargeIcon)]
    [InlineData(300, 390, MiniPlayerForm.LargeIcon)]
    // Big and wide → lyrics
    [InlineData(640, 384, MiniPlayerForm.Lyrics)]
    [InlineData(540, 320, MiniPlayerForm.Lyrics)]
    // Wide but too short for the split view stays a bar
    [InlineData(700, 180, MiniPlayerForm.Bar)]
    public void ComputeForm_MapsSizeToExpectedForm(double w, double h, MiniPlayerForm expected)
        => Assert.Equal(expected, MiniPlayerViewModel.ComputeForm(w, h));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 100)]
    [InlineData(double.NaN, 200)]
    public void ComputeForm_DegenerateSizesFallBackToCard(double w, double h)
        => Assert.Equal(MiniPlayerForm.Card, MiniPlayerViewModel.ComputeForm(w, h));

    // ── Hysteresis ───────────────────────────────────────────
    // The form cross-fade restarts on every crossing, so a drag creeping along a
    // threshold must not be able to flip-flop. Each form's own region is widened by the
    // band; every other region shrinks by it.

    private const double Band = 10;
    private const double RatioBand = 0.06;

    private static MiniPlayerForm Sticky(double w, double h, MiniPlayerForm current)
        => MiniPlayerViewModel.ComputeForm(w, h, current, Band, RatioBand);

    [Fact]
    public void ComputeForm_WithoutCurrentForm_MatchesTheRawThresholds()
    {
        // The 2-arg overload must stay band-free, or every existing threshold moves.
        Assert.Equal(MiniPlayerForm.Bar, MiniPlayerViewModel.ComputeForm(400, 210));
        Assert.Equal(MiniPlayerForm.Card, MiniPlayerViewModel.ComputeForm(400, 211));
    }

    [Theory]
    // Just past the Bar/Card line, still Bar because Bar is the current form.
    [InlineData(400, 215, MiniPlayerForm.Bar, MiniPlayerForm.Bar)]
    // Far enough past it to commit.
    [InlineData(400, 225, MiniPlayerForm.Bar, MiniPlayerForm.Card)]
    // Coming back the other way: Card holds until well below the line.
    [InlineData(400, 205, MiniPlayerForm.Card, MiniPlayerForm.Card)]
    [InlineData(400, 195, MiniPlayerForm.Card, MiniPlayerForm.Bar)]
    // Lyrics keeps its split view a little past the shrink threshold.
    [InlineData(535, 384, MiniPlayerForm.Lyrics, MiniPlayerForm.Lyrics)]
    [InlineData(525, 384, MiniPlayerForm.Lyrics, MiniPlayerForm.Card)]
    // ...but growing INTO lyrics needs to clear the line by the band.
    [InlineData(545, 384, MiniPlayerForm.Card, MiniPlayerForm.Card)]
    [InlineData(555, 384, MiniPlayerForm.Card, MiniPlayerForm.Lyrics)]
    public void ComputeForm_HoldsTheCurrentFormInsideTheBand(
        double w, double h, MiniPlayerForm current, MiniPlayerForm expected)
        => Assert.Equal(expected, Sticky(w, h, current));

    [Fact]
    public void ComputeForm_BandStopsFlipFlopAcrossABoundary()
    {
        // Walk a slow drag back and forth across the Bar/Card line entirely inside the
        // band. Without hysteresis this alternates every step and restarts the fade.
        var form = MiniPlayerForm.Bar;
        var switches = 0;
        foreach (var h in new double[] { 208, 212, 209, 213, 207, 214, 211, 206 })
        {
            var next = Sticky(400, h, form);
            if (next != form) switches++;
            form = next;
        }

        Assert.Equal(0, switches);
        Assert.Equal(MiniPlayerForm.Bar, form);
    }

    [Fact]
    public void ComputeForm_BandStillLetsADeliberateDragThrough()
    {
        // A real drag well past the line must still commit — a dead band that never
        // releases would be worse than no band at all.
        var form = MiniPlayerForm.Bar;
        foreach (var h in new double[] { 208, 214, 222, 240 })
            form = Sticky(400, h, form);

        Assert.Equal(MiniPlayerForm.Card, form);
    }

    private static Track T(string title, string artist, string album = "") => new()
    {
        Title = title,
        Artist = artist,
        Album = album,
    };

    [Fact]
    public void FilterTracks_RanksTitlePrefixAboveArtistPrefixAboveContains()
    {
        var tracks = new List<Track>
        {
            T("Midnight City", "M83"),
            T("City Lights", "Midnight Crew"),
            T("Drive", "Someone", "Midnight Album"),
        };

        var results = MiniPlayerViewModel.FilterTracks(tracks, "midnight", 10);

        Assert.Equal(3, results.Count);
        Assert.Equal("Midnight City", results[0].Title); // title prefix
        Assert.Equal("City Lights", results[1].Title);   // artist prefix
        Assert.Equal("Drive", results[2].Title);         // album contains
    }

    [Fact]
    public void FilterTracks_EmptyQueryReturnsNothing()
    {
        var tracks = new List<Track> { T("Song", "Artist") };
        Assert.Empty(MiniPlayerViewModel.FilterTracks(tracks, "   ", 10));
    }

    [Fact]
    public void FilterTracks_RespectsLimit()
    {
        var tracks = new List<Track>();
        for (var i = 0; i < 50; i++)
            tracks.Add(T($"Alpha {i:D2}", "Artist"));

        Assert.Equal(30, MiniPlayerViewModel.FilterTracks(tracks, "alpha", 30).Count);
    }

    [Fact]
    public void FilterTracks_MatchesCaseInsensitively()
    {
        var tracks = new List<Track> { T("HEADLIGHTS", "Alex Warren") };
        Assert.Single(MiniPlayerViewModel.FilterTracks(tracks, "headlights", 10));
    }

    // ── Empty-query suggestions ──────────────────────────────
    // The search drawer opened onto a blank sheet because FilterTracks returns
    // nothing for an empty query, so there was nothing to tap until the user
    // typed. ShuffleSample fills that state with a random slice of the library.

    private static List<Track> Library(int count)
    {
        var tracks = new List<Track>();
        for (var i = 0; i < count; i++)
            tracks.Add(T($"Track {i:D3}", "Artist"));
        return tracks;
    }

    [Fact]
    public void ShuffleSample_ReturnsLimitDistinctTracksFromALargeLibrary()
    {
        var sample = MiniPlayerViewModel.ShuffleSample(Library(500), 30, new Random(1));

        Assert.Equal(30, sample.Count);
        Assert.Equal(30, sample.Distinct().Count());
    }

    [Fact]
    public void ShuffleSample_ReturnsEveryTrackWhenTheLibraryIsSmallerThanTheLimit()
    {
        var tracks = Library(4);

        var sample = MiniPlayerViewModel.ShuffleSample(tracks, 30, new Random(1));

        Assert.Equal(4, sample.Count);
        Assert.Equal(tracks.OrderBy(t => t.Title), sample.OrderBy(t => t.Title));
    }

    [Fact]
    public void ShuffleSample_EmptyLibraryReturnsEmpty()
        => Assert.Empty(MiniPlayerViewModel.ShuffleSample(new List<Track>(), 30, new Random(1)));

    [Fact]
    public void ShuffleSample_PicksADifferentSetOnASecondOpen()
    {
        // "Already on shuffle" means a fresh spread each time the drawer opens —
        // a stable slice (e.g. plain Take(30)) would defeat the point.
        var tracks = Library(500);
        var rng = new Random(7);

        var first = MiniPlayerViewModel.ShuffleSample(tracks, 30, rng);
        var second = MiniPlayerViewModel.ShuffleSample(tracks, 30, rng);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ShuffleSample_DoesNotReorderTheCallersLibrary()
    {
        // _library.Tracks is the live library collection — sampling must not
        // shuffle the user's Songs list as a side effect.
        var tracks = Library(50);
        var before = tracks.ToList();

        MiniPlayerViewModel.ShuffleSample(tracks, 30, new Random(1));

        Assert.Equal(before, tracks);
    }
}
