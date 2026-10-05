using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi, 2026-10-03): "Rihanna; Drake" should read "Rihanna, Drake" in rows and
/// tiles, while the tag itself stays unchanged.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistCreditDisplayTests
{
    public ArtistCreditDisplayTests() => ArtistCredit.ResetToDefaults();

    [Theory]
    [InlineData("Rihanna; Drake", "Rihanna, Drake")]
    [InlineData("Rihanna;Drake", "Rihanna, Drake")]
    [InlineData("Hugo Cantarra; Eli & Fur", "Hugo Cantarra, Eli & Fur")]   // "&" is not a default separator
    [InlineData("Bad Bunny / Bomba Estéreo", "Bad Bunny, Bomba Estéreo")]
    [InlineData("AC/DC", "AC/DC")]                                         // tight slash is part of the name
    [InlineData("A, B", "A, B")]
    [InlineData("A,B", "A, B")]
    [InlineData("Metro Boomin feat. Drake", "Metro Boomin feat. Drake")]   // word separators stay as written
    [InlineData("Solo Artist", "Solo Artist")]
    [InlineData("", "")]
    public void Display_ShowsSymbolSeparatorsAsCommas(string credit, string expected)
        => Assert.Equal(expected, ArtistCredit.Display(credit));

    [Fact]
    public void Display_SplitsBackIntoTheSameNames()
    {
        const string credit = "Rihanna; Drake / SZA";
        Assert.Equal(ArtistCredit.Split(credit), ArtistCredit.Split(ArtistCredit.Display(credit)));
    }

    [Fact]
    public void Display_UsesTheActiveJoin_WhenCommaIsNotASeparator()
    {
        try
        {
            ArtistCredit.Configure(ArtistGroupMode.Artist, new[] { ";", "/" });
            Assert.Equal("A; B; C", ArtistCredit.Display("A / B;C"));
        }
        finally
        {
            ArtistCredit.ResetToDefaults();
        }
    }

    [Fact]
    public void TrackAndAlbum_DisplayCommas_TagUntouched()
    {
        var track = new Track { Artist = "Rihanna; Drake" };
        Assert.Equal("Rihanna, Drake", track.ArtistDisplay);
        Assert.Equal("Rihanna; Drake", track.Artist);

        var album = new Album { Artist = "Hugo Cantarra; Eli & Fur", Year = 2026 };
        Assert.Equal("Hugo Cantarra, Eli & Fur · 2026", album.TileSubtitle);
    }
}
