using System.Text.Json;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The About-the-artist pipeline's pure parsers against MusicBrainz / Wikidata /
/// Wikipedia response shapes: only an exact (or very high scoring) search hit is
/// trusted, lookup fields map to display facts, and disambiguation pages never
/// become a biography.
/// </summary>
public class ArtistInfoServiceTests
{
    private const string SearchJson = """
    {"artists":[
      {"id":"bad-bunny-id","score":100,"name":"Bad Bunny","type":"Person"},
      {"id":"other-id","score":72,"name":"Bad Bunny Tribute"}
    ]}
    """;

    private const string LookupJson = """
    {
      "id":"bad-bunny-id","name":"Bad Bunny","type":"Person","gender":"male","country":"PR",
      "area":{"name":"Puerto Rico"},"begin-area":{"name":"Vega Baja"},
      "life-span":{"begin":"1994-03-10","ended":false},
      "genres":[{"name":"reggaeton","count":12},{"name":"latin trap","count":9},{"name":"pop","count":1},{"name":"rap","count":3},{"name":"dembow","count":4}],
      "relations":[
        {"type":"wikidata","url":{"resource":"https://www.wikidata.org/wiki/Q29027587"}},
        {"type":"official homepage","url":{"resource":"https://www.badbunnypr.com"}}
      ]
    }
    """;

    [Fact]
    public void PickBestMatch_TakesTheExactNameHit()
    {
        using var doc = JsonDocument.Parse(SearchJson);
        Assert.Equal("bad-bunny-id", ArtistInfoService.PickBestMatch(doc.RootElement, "bad bunny"));
    }

    [Fact]
    public void PickBestMatch_RefusesALooseTopHit()
    {
        using var doc = JsonDocument.Parse("""{"artists":[{"id":"x","score":88,"name":"Bunny Wailer"}]}""");
        Assert.Null(ArtistInfoService.PickBestMatch(doc.RootElement, "Bad Bunny"));
    }

    [Fact]
    public void PickBestMatch_AcceptsAnAliasOrANearCertainTopHit()
    {
        using var alias = JsonDocument.Parse("""{"artists":[{"id":"a","score":90,"name":"Benito Antonio Martínez Ocasio","aliases":[{"name":"Bad Bunny"}]}]}""");
        Assert.Equal("a", ArtistInfoService.PickBestMatch(alias.RootElement, "Bad Bunny"));
        using var top = JsonDocument.Parse("""{"artists":[{"id":"b","score":97,"name":"BAD BUNNY (PR)"}]}""");
        Assert.Equal("b", ArtistInfoService.PickBestMatch(top.RootElement, "Bad Bunny"));
    }

    [Fact]
    public void ParseLookup_MapsIdentityOriginDatesGenresAndLinks()
    {
        using var doc = JsonDocument.Parse(LookupJson);
        var info = ArtistInfoService.ParseLookup(doc.RootElement, "Bad Bunny");

        Assert.Equal("Vega Baja, Puerto Rico", info.FromDisplay);
        Assert.Equal("BORN", info.BeginLabel);
        Assert.Equal("March 10, 1994", info.BeginDisplay);
        Assert.Equal("1994 – present", info.ActiveDisplay);
        Assert.Equal("Solo artist · Male", info.TypeDisplay);
        // Top four by vote count, most-voted first.
        Assert.Equal(new[] { "reggaeton", "latin trap", "dembow", "rap" }, info.Genres);
        Assert.Equal("Q29027587", info.WikidataId);
        Assert.Equal("https://www.badbunnypr.com", info.WebsiteUrl);
        Assert.Equal("https://musicbrainz.org/artist/bad-bunny-id", info.MusicBrainzUrl);
        Assert.False(info.HasBio);
    }

    [Fact]
    public void ParseLookup_GroupUsesFormedAndDisbanded()
    {
        using var doc = JsonDocument.Parse("""
        {"id":"g","name":"The Beatles","type":"Group","country":"GB","begin-area":{"name":"Liverpool"},
         "life-span":{"begin":"1960","end":"1970-04-10","ended":true},"tags":[{"name":"rock","count":40}]}
        """);
        var info = ArtistInfoService.ParseLookup(doc.RootElement, "The Beatles");
        Assert.True(info.IsGroup);
        Assert.Equal("FORMED", info.BeginLabel);
        Assert.Equal("1960", info.BeginDisplay);
        Assert.Equal("DISBANDED", info.EndLabel);
        Assert.Equal("April 10, 1970", info.EndDisplay);
        Assert.Equal("1960 – 1970", info.ActiveDisplay);
        Assert.Equal("Liverpool, United Kingdom", info.FromDisplay);
        Assert.Equal(new[] { "rock" }, info.Genres); // tags are the fallback when no genres
    }

    [Fact]
    public void ParseWikidataTitle_ReadsTheEnglishSitelink()
    {
        using var doc = JsonDocument.Parse("""{"entities":{"Q1":{"sitelinks":{"enwiki":{"title":"Bad Bunny"}}}}}""");
        Assert.Equal("Bad Bunny", ArtistInfoService.ParseWikidataTitle(doc.RootElement, "Q1"));
        Assert.Null(ArtistInfoService.ParseWikidataTitle(doc.RootElement, "Q2"));
    }

    [Fact]
    public void ApplyWikipediaSummary_UsesStandardArticlesOnly()
    {
        var info = new ArtistInfo();
        using var disambig = JsonDocument.Parse("""{"type":"disambiguation","extract":"Bunny may refer to:"}""");
        ArtistInfoService.ApplyWikipediaSummary(disambig.RootElement, info);
        Assert.False(info.HasBio);

        using var article = JsonDocument.Parse("""
        {"type":"standard","description":"Puerto Rican rapper and singer","extract":"Benito … is a Puerto Rican rapper.",
         "content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Bad_Bunny"}}}
        """);
        ArtistInfoService.ApplyWikipediaSummary(article.RootElement, info);
        Assert.True(info.HasBio);
        Assert.Equal("Wikipedia", info.BioSource);
        Assert.Equal("Puerto Rican rapper and singer", info.ShortDescription);
        Assert.Equal("https://en.wikipedia.org/wiki/Bad_Bunny", info.WikipediaUrl);
    }

    [Theory]
    [InlineData("1994-03-10", "March 10, 1994")]
    [InlineData("1994-03", "March 1994")]
    [InlineData("1994", "1994")]
    [InlineData("", "")]
    public void FormatPartialDate_HandlesEveryMusicBrainzPrecision(string iso, string expected)
        => Assert.Equal(expected, ArtistInfo.FormatPartialDate(iso));
}
