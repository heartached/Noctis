using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A smart playlist's SortBy only runs when a limit is set — it decides WHICH tracks
/// make the cut, not the order they are shown in (that is the playlist view's own
/// sort). So the release-date options are exercised through a limited playlist, and
/// the library is deliberately stored out of chronological order: an unsorted
/// implementation would otherwise pass by accident.
/// </summary>
public class SmartPlaylistReleaseSortTests
{
    private static Track Released(string title, int year, string releaseDate) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        FilePath = title + ".mp3",
        Artist = "Band",
        Year = year,
        ReleaseDate = releaseDate
    };

    private static readonly List<Track> Library = new()
    {
        Released("Sophomore", 2014, "2014-01-05"),
        Released("Latest", 2021, "2021-11-30"),
        Released("Debut", 2009, "2009-05-01"),
    };

    private static Playlist Limited(SmartPlaylistSortBy sortBy) => new()
    {
        Name = "smart",
        IsSmartPlaylist = true,
        MatchAll = true,
        LimitCount = 2,
        SortBy = sortBy,
        Rules = { new SmartPlaylistRule { Field = RuleField.Artist, Operator = RuleOperator.Equals, Value = "Band" } }
    };

    [Fact]
    public void OldestRelease_KeepsTheEarliestTracks()
    {
        var result = SmartPlaylistEvaluator.Evaluate(Limited(SmartPlaylistSortBy.ReleaseDateOldest), Library);
        Assert.Equal(new[] { "Debut", "Sophomore" }, result.Select(t => t.Title));
    }

    [Fact]
    public void NewestRelease_KeepsTheLatestTracks()
    {
        var result = SmartPlaylistEvaluator.Evaluate(Limited(SmartPlaylistSortBy.ReleaseDateNewest), Library);
        Assert.Equal(new[] { "Latest", "Sophomore" }, result.Select(t => t.Title));
    }

    [Fact]
    public void ReleaseSorts_HaveReadableNamesInThePicker()
    {
        // The dialog's combo lists Enum.GetValues, so a missing arm would show the
        // raw enum name next to "Most Played" and "Recently Added".
        Assert.Equal("Oldest Release", SmartPlaylistEvaluator.GetSortDisplayName(SmartPlaylistSortBy.ReleaseDateOldest));
        Assert.Equal("Newest Release", SmartPlaylistEvaluator.GetSortDisplayName(SmartPlaylistSortBy.ReleaseDateNewest));
    }
}
