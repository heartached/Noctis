using System;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DeezerApiTests
{
    [Fact]
    public void ParseSearch_ReturnsSuggestions_FromDeezerPayload()
    {
        var json = """
        {"data":[
          {"title":"Graduation","artist":{"name":"benny blanco"},"album":{"title":"Graduation"}},
          {"title":"Lucid Dreams","artist":{"name":"Juice WRLD"},"album":{"title":"Goodbye & Good Riddance"}}
        ]}
        """;

        var result = DeezerApi.ParseSearch(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("Graduation", result[0].Title);
        Assert.Equal("benny blanco", result[0].Artist);
        Assert.Equal("Graduation", result[0].Album);
        Assert.Equal("Deezer", result[0].Source);
    }

    [Fact]
    public void ParseSearch_ReturnsEmpty_ForGarbageOrError()
    {
        Assert.Empty(DeezerApi.ParseSearch(""));
        Assert.Empty(DeezerApi.ParseSearch("{\"error\":{\"code\":4}}"));
    }

    [Fact]
    public void BuildSearchUrl_EncodesArtistAndTitle()
    {
        var url = DeezerApi.BuildSearchUrl("Juice WRLD", "Lucid Dreams", "");

        Assert.StartsWith("https://api.deezer.com/search?q=", url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("artist:", decoded);
        Assert.Contains("Lucid Dreams", decoded);
        Assert.Contains("Juice WRLD", decoded);
    }
}
