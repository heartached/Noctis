using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class WrapStatsBuilderTests
{
    private static Track MakeTrack(string title, string artist, string album, string genre,
        double minutes = 4, bool lossless = false)
    {
        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Duration = TimeSpan.FromMinutes(minutes),
            Codec = lossless ? "FLAC" : "MPEG",
            FilePath = lossless ? "x.flac" : "x.mp3",
            Bitrate = lossless ? 1000 : 320,
        };
    }

    private static PlayHistoryEvent Play(Track t, DateTime localTime, bool skipped = false) => new()
    {
        TrackId = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        PlayedAtUtc = localTime.ToUniversalTime(),
        Skipped = skipped,
    };

    [Fact]
    public void Build_EmptyPeriod_ReturnsZeroStats()
    {
        var stats = WrapStatsBuilder.Build(
            Array.Empty<PlayHistoryEvent>(), new Dictionary<Guid, Track>(), 2026);

        Assert.Equal("2026", stats.PeriodLabel);
        Assert.Equal(0, stats.TotalPlays);
        Assert.Empty(stats.TopTracks);
    }

    [Fact]
    public void Build_FiltersByYearAndMonth()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop");
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var events = new List<PlayHistoryEvent>
        {
            Play(t, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Local)),
            Play(t, new DateTime(2025, 6, 10, 12, 0, 0, DateTimeKind.Local)),
        };

        var yearly = WrapStatsBuilder.Build(events, byId, 2026);
        Assert.Equal(2, yearly.TotalPlays);

        var monthly = WrapStatsBuilder.Build(events, byId, 2026, 6);
        Assert.Equal(1, monthly.TotalPlays);
        Assert.Equal("June 2026", monthly.PeriodLabel);
    }

    [Fact]
    public void Build_RanksTopTracksByPlays()
    {
        var a = MakeTrack("Hit", "X", "AlbumA", "Pop");
        var b = MakeTrack("Deep Cut", "X", "AlbumA", "Pop");
        var byId = new Dictionary<Guid, Track> { [a.Id] = a, [b.Id] = b };
        var when = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        var events = new List<PlayHistoryEvent>
        {
            Play(a, when), Play(a, when.AddHours(1)), Play(a, when.AddHours(2)),
            Play(b, when.AddHours(3)),
        };

        var stats = WrapStatsBuilder.Build(events, byId, 2026);

        Assert.Equal("Hit", stats.TopTracks[0].Name);
        Assert.Equal(3, stats.TopTracks[0].Plays);
        Assert.Equal(1, stats.TopTracks[0].Rank);
        Assert.Equal("Deep Cut", stats.TopTracks[1].Name);
    }

    [Fact]
    public void Build_SkippedPlaysAddNoMinutes()
    {
        var t = MakeTrack("Song", "Artist", "Album", "Pop", minutes: 10);
        var byId = new Dictionary<Guid, Track> { [t.Id] = t };
        var when = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        var events = new List<PlayHistoryEvent>
        {
            Play(t, when),
            Play(t, when.AddHours(1), skipped: true),
        };

        var stats = WrapStatsBuilder.Build(events, byId, 2026);

        Assert.Equal(2, stats.TotalPlays);
        Assert.Equal(10, stats.TotalMinutes);
    }

    [Fact]
    public void Build_ComputesLosslessPercent_AndTopGenre()
    {
        var flac = MakeTrack("A", "X", "Alb", "Metal", lossless: true);
        var mp3 = MakeTrack("B", "Y", "Alb2", "Pop");
        var byId = new Dictionary<Guid, Track> { [flac.Id] = flac, [mp3.Id] = mp3 };
        var when = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        var events = new List<PlayHistoryEvent>
        {
            Play(flac, when), Play(flac, when.AddHours(1)), Play(flac, when.AddHours(2)),
            Play(mp3, when.AddHours(3)),
        };

        var stats = WrapStatsBuilder.Build(events, byId, 2026);

        Assert.Equal(75, stats.LosslessPercent);
        Assert.Equal("Metal", stats.TopGenre);
    }

    [Fact]
    public void Build_UnresolvedTracks_UseFallbackLength()
    {
        // Track played but later removed from the library.
        var ghost = new PlayHistoryEvent
        {
            TrackId = Guid.NewGuid(),
            Title = "Gone",
            Artist = "Ghost",
            PlayedAtUtc = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local).ToUniversalTime(),
        };

        var stats = WrapStatsBuilder.Build(new[] { ghost }, new Dictionary<Guid, Track>(), 2026);

        Assert.Equal(1, stats.TotalPlays);
        Assert.Equal(4, stats.TotalMinutes); // 3.5 rounded
        Assert.Equal("Gone", stats.TopTracks[0].Name);
    }

    [Fact]
    public void BuildTestData_ProducesPlausibleStats()
    {
        var stats = WrapStatsBuilder.BuildTestData();

        Assert.True(stats.TotalPlays > 0);
        Assert.True(stats.TotalMinutes > 0);
        Assert.Equal(5, stats.TopTracks.Count);
        Assert.Equal(5, stats.TopArtists.Count);
        // Ranked descending by plays
        Assert.True(stats.TopTracks[0].Plays >= stats.TopTracks[4].Plays);
        Assert.Equal(stats.TopGenres[0].Name, stats.TopGenre);
    }

    [Fact]
    public void RenderWrapCard_ProducesPng()
    {
        var png = ShareCardRenderer.RenderWrapCard(new WrapCardSpec
        {
            PeriodLabel = "2026",
            TopArtists = new[] { "Within Temptation", "Nightwish" },
            TopTracks = new[] { "Those Eyes", "Ghost Love Score" },
            TotalMinutes = 48_213,
            TotalPlays = 9_412,
            LosslessPercent = 62,
            TopGenre = "Symphonic Metal",
            Format = ShareCardFormat.Story,
        });

        Assert.True(png.Length > 1000);
        Assert.Equal(0x89, png[0]);
    }
}
