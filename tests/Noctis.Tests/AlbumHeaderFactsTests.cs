using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Album page header facts: the ALBUM / SINGLE / EP kicker and the facts line.</summary>
public class AlbumHeaderFactsTests
{
    private static Album Make(int trackCount, ReleaseType? tagged = null, bool overridden = false, int seconds = 200)
    {
        var tracks = new List<Track>();
        for (var i = 0; i < trackCount; i++)
        {
            var t = new Track { Title = $"t{i}", Duration = TimeSpan.FromSeconds(seconds) };
            if (tagged is { } rt)
            {
                t.ReleaseType = rt;
                t.ReleaseTypeFromTag = true;
                t.IsReleaseTypeOverridden = overridden;
            }
            tracks.Add(t);
        }
        return new Album
        {
            Tracks = tracks,
            TrackCount = trackCount,
            TotalDuration = TimeSpan.FromSeconds(seconds * trackCount),
        };
    }

    [Theory]
    [InlineData(1, "SINGLE")]
    [InlineData(2, "SINGLE")]
    [InlineData(3, "EP")]
    [InlineData(6, "EP")]
    [InlineData(7, "ALBUM")]
    [InlineData(14, "ALBUM")]
    public void Kicker_FallsBackToTrackCount_WhenUntagged(int count, string expected)
        => Assert.Equal(expected, Make(count).ReleaseKindLabel);

    [Fact]
    public void Kicker_TrustsTagsOverTrackCount()
    {
        // A tagged 12-track "EP" and a tagged 2-track "Album" both keep their tag.
        Assert.Equal("EP", Make(12, ReleaseType.EP).ReleaseKindLabel);
        Assert.Equal("ALBUM", Make(2, ReleaseType.Album).ReleaseKindLabel);
        Assert.Equal("SINGLE", Make(9, ReleaseType.Single, overridden: true).ReleaseKindLabel);
        Assert.Equal("SOUNDTRACK", Make(20, ReleaseType.Soundtrack).ReleaseKindLabel);
    }

    [Fact]
    public void FactsLine_TrackCountAndDurationFormats()
    {
        var single = Make(1, seconds: 227);
        Assert.Equal("1 track", single.TrackCountText);
        Assert.Equal("3m 47s", single.HeaderDurationFormatted);

        var longAlbum = Make(20, seconds: 240); // 80 minutes
        Assert.Equal("20 tracks", longAlbum.TrackCountText);
        Assert.Equal("1h 20m", longAlbum.HeaderDurationFormatted);
    }
}
