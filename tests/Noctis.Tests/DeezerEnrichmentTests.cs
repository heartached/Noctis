using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DeezerEnrichmentTests
{
    private const string TrackJson = """
    {
      "id": 142986206,
      "title": "Lucid Dreams",
      "isrc": "USUM71808193",
      "track_position": 8,
      "disk_number": 1,
      "bpm": 83.9,
      "artist": { "name": "Juice WRLD" },
      "album": { "id": 14801948, "title": "Goodbye & Good Riddance", "release_date": "2018-05-23" },
      "contributors": [ { "name": "Juice WRLD", "role": "Main" } ]
    }
    """;

    private const string AlbumJson = """
    {
      "id": 14801948,
      "title": "Goodbye & Good Riddance",
      "nb_tracks": 17,
      "release_date": "2018-05-23",
      "artist": { "name": "Juice WRLD" },
      "genres": { "data": [ { "name": "Rap/Hip Hop" } ] }
    }
    """;

    [Fact]
    public void ParseTrack_ExtractsPerTrackFields()
    {
        var t = DeezerApi.ParseTrack(TrackJson);
        Assert.NotNull(t);
        Assert.Equal(14801948, t!.AlbumId);
        Assert.Equal("Lucid Dreams", t.Title);
        Assert.Equal("USUM71808193", t.Isrc);
        Assert.Equal(8, t.TrackNumber);
        Assert.Equal(1, t.DiscNumber);
        Assert.Equal(84, t.Bpm);            // 83.9 rounds to 84
        Assert.Equal("Juice WRLD", t.AlbumArtist);
        Assert.Equal(2018, t.AlbumYear);    // from the nested album.release_date (original date)
    }

    [Fact]
    public void ParseAlbum_ExtractsAlbumFields()
    {
        var a = DeezerApi.ParseAlbum(AlbumJson);
        Assert.NotNull(a);
        Assert.Equal("Goodbye & Good Riddance", a!.Title);
        Assert.Equal(17, a.TrackCount);
        Assert.Equal(2018, a.Year);
        Assert.Equal("Rap/Hip Hop", a.Genre);
        Assert.Equal("Juice WRLD", a.AlbumArtist);
    }

    [Fact]
    public void ParseTrack_Malformed_ReturnsNull()
    {
        Assert.Null(DeezerApi.ParseTrack("not json"));
        Assert.Null(DeezerApi.ParseTrack("{}"));
    }
}
