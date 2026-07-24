using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class WrapArchiveServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"wrap_archive_test_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private static Track MakeTrack(string title, string artist, string album, string genre) => new()
    {
        Title = title,
        Artist = artist,
        Album = album,
        Genre = genre,
        Duration = TimeSpan.FromMinutes(4),
    };

    private static PlayHistoryEvent Play(Track t, DateTime localTime) => new()
    {
        TrackId = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        PlayedAtUtc = localTime.ToUniversalTime(),
    };

    [Fact]
    public void EnsureArchived_FreezesPastYear_AndSurvivesReload()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var events = new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local)), // current year, ignored
        };

        var archive = new WrapArchiveService(_file);
        archive.EnsureArchived(events, byId, currentYear: 2026);

        // Fresh instance reads from disk — verifies the JSON round-trips.
        var reloaded = new WrapArchiveService(_file);
        Assert.Contains(2024, reloaded.ArchivedYears);
        Assert.DoesNotContain(2026, reloaded.ArchivedYears);

        var stats = reloaded.GetYear(2024);
        Assert.NotNull(stats);
        Assert.Equal(2, stats!.TotalPlays);
        Assert.NotEmpty(stats.TopArtists); // IReadOnlyList survived deserialization
        Assert.Equal("Artist", stats.TopArtists[0].Name);
    }

    [Fact]
    public void EnsureArchived_DoesNotReArchiveExistingYear()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var events = new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
        };

        var archive = new WrapArchiveService(_file);
        archive.EnsureArchived(events, byId, currentYear: 2026);

        // A later run with more 2024 events must not overwrite the frozen snapshot.
        events.Add(Play(t, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local)));
        archive.EnsureArchived(events, byId, currentYear: 2026);

        Assert.Single(archive.ArchivedYears);
        Assert.Equal(1, archive.GetYear(2024)!.TotalPlays); // still the original snapshot
    }

    [Fact]
    public void EnsureArchived_MarksAYearPartial_WhenTheLogStartsInsideIt()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };

        // The 10k cap has already trimmed everything before May, so this snapshot can
        // only ever cover part of 2024.
        var trimmed = new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
        };

        var archive = new WrapArchiveService(_file);
        archive.EnsureArchived(trimmed, byId, currentYear: 2026);

        Assert.False(archive.IsYearComplete(2024));
        Assert.True(archive.IsYearComplete(2023)); // unarchived years are not "partial"
    }

    [Fact]
    public void EnsureArchived_MarksAYearComplete_WhenTheLogPredatesIt()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var events = new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2023, 11, 1, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
        };

        var archive = new WrapArchiveService(_file);
        archive.EnsureArchived(events, byId, currentYear: 2026);

        Assert.True(archive.IsYearComplete(2024));
    }

    [Fact]
    public void EnsureArchived_ReplacesAPartialSnapshot_WhenALaterLogReachesFurtherBack()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };

        var archive = new WrapArchiveService(_file);
        archive.EnsureArchived(new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
        }, byId, currentYear: 2026);

        Assert.False(archive.IsYearComplete(2024));
        Assert.Equal(1, archive.GetYear(2024)!.TotalPlays);

        // A restored play_history.json reaches back past the start of the year.
        archive.EnsureArchived(new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2023, 12, 1, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Local)),
        }, byId, currentYear: 2026);

        Assert.True(archive.IsYearComplete(2024));
        Assert.Equal(2, archive.GetYear(2024)!.TotalPlays);
        Assert.Single(archive.ArchivedYears, y => y == 2024);
    }
}
