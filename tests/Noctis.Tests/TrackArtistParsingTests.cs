using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class TrackArtistParsingTests
{
    [Theory]
    [InlineData("Bad Bunny & Bomba Estéreo", "Bad Bunny")]
    [InlineData("Bad Bunny feat. Bryant Myers", "Bad Bunny")]
    [InlineData("Bad Bunny featuring Bomba Estéreo & Buscabulla", "Bad Bunny")]
    [InlineData("  Bad Bunny  ", "Bad Bunny")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void GetPrimaryArtist_ReturnsFirstCreditedArtist(string? artist, string expected)
    {
        Assert.Equal(expected, Track.GetPrimaryArtist(artist));
    }
}
