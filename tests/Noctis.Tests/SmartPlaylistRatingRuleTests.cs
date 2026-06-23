using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class SmartPlaylistRatingRuleTests
{
    private static List<Track> SampleTracks() =>
    [
        new Track { Title = "Unrated", Rating = 0 },
        new Track { Title = "Two", Rating = 2 },
        new Track { Title = "Four", Rating = 4 },
        new Track { Title = "Five", Rating = 5, IsDisliked = false },
        new Track { Title = "Disliked", Rating = 1, IsDisliked = true },
    ];

    private static Playlist SmartPlaylist(params SmartPlaylistRule[] rules) => new()
    {
        IsSmartPlaylist = true,
        MatchAll = true,
        Rules = rules.ToList()
    };

    [Fact]
    public void Rating_GreaterThan_FiltersByStars()
    {
        var playlist = SmartPlaylist(new SmartPlaylistRule
        {
            Field = RuleField.Rating,
            Operator = RuleOperator.GreaterThan,
            Value = "3"
        });

        var matches = SmartPlaylistEvaluator.Evaluate(playlist, SampleTracks());

        Assert.Equal(["Four", "Five"], matches.Select(t => t.Title));
    }

    [Fact]
    public void Rating_Between_IsInclusive()
    {
        var playlist = SmartPlaylist(new SmartPlaylistRule
        {
            Field = RuleField.Rating,
            Operator = RuleOperator.Between,
            Value = "2",
            Value2 = "4"
        });

        var matches = SmartPlaylistEvaluator.Evaluate(playlist, SampleTracks());

        Assert.Equal(["Two", "Four"], matches.Select(t => t.Title));
    }

    [Fact]
    public void IsDisliked_IsTrue_MatchesOnlyDislikedTracks()
    {
        var playlist = SmartPlaylist(new SmartPlaylistRule
        {
            Field = RuleField.IsDisliked,
            Operator = RuleOperator.IsTrue
        });

        var matches = SmartPlaylistEvaluator.Evaluate(playlist, SampleTracks());

        Assert.Equal(["Disliked"], matches.Select(t => t.Title));
    }

    [Fact]
    public void RatingField_OffersNumericOperators()
    {
        var operators = SmartPlaylistEvaluator.GetOperatorsForField(RuleField.Rating);

        Assert.Equal(
            [RuleOperator.Equals, RuleOperator.GreaterThan, RuleOperator.LessThan, RuleOperator.Between],
            operators);
    }
}
