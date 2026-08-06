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
}
