using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class MetadataLookupApiTests
{
    [Fact]
    public void MusicBrainz_BuildSearchUrl_QuotesFields()
    {
        var url = MusicBrainzApi.BuildRecordingSearchUrl("Queen", "Bohemian Rhapsody", "A Night at the Opera");
        Assert.Contains("fmt=json", url);
        Assert.Contains("limit=5", url);
        // The Lucene query is URL-encoded; decode the query value to assert structure.
        var query = System.Uri.UnescapeDataString(url.Split("query=")[1].Split('&')[0]);
        Assert.Contains("recording:\"Bohemian Rhapsody\"", query);
        Assert.Contains("artist:\"Queen\"", query);
        Assert.Contains("release:\"A Night at the Opera\"", query);
        Assert.Contains(" AND ", query);
    }

    [Fact]
    public void MusicBrainz_ParseSearch_ExtractsTagsAndYear()
    {
        const string json = """
        {
          "recordings": [
            {
              "title": "Hello",
              "score": 100,
              "artist-credit": [ { "name": "Adele" } ],
              "releases": [ { "title": "25", "date": "2015-11-20" } ]
            }
          ]
        }
        """;

        var top = MusicBrainzApi.ParseRecordingSearch(json).First();
        Assert.Equal("Hello", top.Title);
        Assert.Equal("Adele", top.Artist);
        Assert.Equal("25", top.Album);
        Assert.Equal(2015, top.Year);
        Assert.Equal("MusicBrainz", top.Source);
        Assert.Equal(1.0, top.Confidence, 3);
    }

    [Fact]
    public void MusicBrainz_ParseSearch_SortsByScoreDescending()
    {
        const string json = """
        { "recordings": [
            { "title": "Low", "score": 40, "artist-credit": [ { "name": "X" } ] },
            { "title": "High", "score": 90, "artist-credit": [ { "name": "X" } ] } ] }
        """;
        var hits = MusicBrainzApi.ParseRecordingSearch(json);
        Assert.Equal("High", hits[0].Title);
    }
}
