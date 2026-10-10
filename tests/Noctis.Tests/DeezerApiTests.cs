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

    // Deezer orders by popularity: the first hit for "One More Time" can be a remix or a cover,
    // and enrichment used to take it — ISRC/track #/BPM then belonged to another recording.
    [Fact]
    public void BestTrackId_PrefersTitleArtistAndDurationOverDeezerOrder()
    {
        var json = """
        {"data":[
          {"id":1,"title":"One More Time (As Made Famous By Daft Punk)","duration":469,"artist":{"name":"The Backing Tracks"}},
          {"id":2,"title":"One More Time (Short Radio Edit)","duration":235,"artist":{"name":"Daft Punk"}},
          {"id":3,"title":"One More Time","duration":320,"artist":{"name":"Daft Punk"}}
        ]}
        """;

        Assert.Equal(3, DeezerApi.BestTrackId(json, "Daft Punk", "One More Time"));
        Assert.Equal(3, DeezerApi.BestTrackId(json, "Daft Punk", "One More Time", TimeSpan.FromSeconds(321)));
        Assert.Null(DeezerApi.BestTrackId("{\"data\":[]}", "Daft Punk", "One More Time"));
    }

    [Fact]
    public void BuildSearchUrl_EncodesArtistAndTitle()
    {
        var url = DeezerApi.BuildSearchUrl("Juice WRLD", "Lucid Dreams", "");

        Assert.StartsWith("https://api.deezer.com/search?q=", url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("Lucid Dreams", decoded);
        Assert.Contains("Juice WRLD", decoded);
        // Free text: Deezer's artist:"…" filter returns zero results (verified live 2026-10-08).
        Assert.DoesNotContain("artist:", decoded);
        Assert.DoesNotContain("track:", decoded);
    }
}
