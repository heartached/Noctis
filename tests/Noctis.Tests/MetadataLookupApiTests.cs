using System.Linq;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class MetadataLookupApiTests
{
    [Fact]
    public void AcoustId_BuildLookupUrl_EncodesAndRoundsDuration()
    {
        var url = AcoustIdApi.BuildLookupUrl("my key", 241.7, "FP+abc/def");
        Assert.Contains("client=my%20key", url);
        Assert.Contains("duration=242", url);
        Assert.Contains("fingerprint=FP%2Babc%2Fdef", url);
        Assert.Contains("meta=recordings+releasegroups", url);
    }

    [Fact]
    public void AcoustId_ParseLookup_ExtractsTopRecording()
    {
        const string json = """
        {
          "status": "ok",
          "results": [
            {
              "score": 0.95,
              "recordings": [
                {
                  "title": "Bohemian Rhapsody",
                  "artists": [{ "name": "Queen" }],
                  "releasegroups": [{ "title": "A Night at the Opera", "type": "Album" }]
                }
              ]
            }
          ]
        }
        """;

        var hits = AcoustIdApi.ParseLookup(json);
        var top = hits.First();
        Assert.Equal("Bohemian Rhapsody", top.Title);
        Assert.Equal("Queen", top.Artist);
        Assert.Equal("A Night at the Opera", top.Album);
        Assert.Equal("AcoustID", top.Source);
        Assert.Equal(0.95, top.Confidence, 3);
    }

    [Fact]
    public void AcoustId_ParseLookup_JoinsMultipleArtists()
    {
        const string json = """
        { "results": [ { "score": 0.8, "recordings": [
            { "title": "Song", "artists": [ { "name": "A" }, { "name": "B" } ] } ] } ] }
        """;
        var top = AcoustIdApi.ParseLookup(json).First();
        Assert.Equal("A, B", top.Artist);
    }

    [Fact]
    public void AcoustId_ParseLookup_EmptyOnNoResults()
    {
        Assert.Empty(AcoustIdApi.ParseLookup("""{ "status": "ok", "results": [] }"""));
        Assert.Empty(AcoustIdApi.ParseLookup(""));
    }

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
