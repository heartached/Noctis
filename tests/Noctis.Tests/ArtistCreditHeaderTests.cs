using System.Linq;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #187 (inconsistency): the lyrics page and the album header put a hard-coded ", "
/// between their per-artist links, so "Metro Boomin feat. Drake" read "Metro Boomin feat.
/// Drake" in the Songs list but "Metro Boomin, Drake" on the lyrics page. They now keep the
/// text between two names exactly as the other artist lines show it.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistCreditHeaderTests
{
    public ArtistCreditHeaderTests() => ArtistCredit.ResetToDefaults();

    /// <summary>What the lyrics page / album header render: each name, then its separator.</summary>
    private static string Rendered(ArtistTokenItem[] tokens)
        => string.Concat(tokens.Select(t => t.Name + (t.IsLast ? "" : t.Separator)));

    [Theory]
    [InlineData("Metro Boomin feat. Drake")]
    [InlineData("Kanye West, GLC, Consequence")]
    [InlineData("Rihanna; Drake / SZA")]
    [InlineData("Daft Punk ft. Pharrell Williams & Nile Rodgers")]
    [InlineData("Tyler, The Creator; Kali Uchis")]
    [InlineData("Kraak & Smaak")]
    public void LyricsHeader_RendersTheSameTextAsTheSongsList(string credit)
    {
        var track = new Track { Artist = credit };
        var tokens = LyricsViewModel.BuildArtistTokens(ArtistCredit.Display(track.Artist));
        Assert.Equal(track.ArtistDisplay, Rendered(tokens));
        Assert.Equal(ArtistCredit.Split(credit).Length, tokens.Length);
    }

    [Fact]
    public void LyricsHeader_FeatCredit_KeepsFeatBetweenTheLinks()
    {
        var tokens = LyricsViewModel.BuildArtistTokens(ArtistCredit.Display("Metro Boomin feat. Drake"));
        Assert.Equal(new[] { "Metro Boomin", "Drake" }, tokens.Select(t => t.Name));
        Assert.Equal(" feat. ", tokens[0].Separator);
        Assert.True(tokens[1].IsLast);
    }

    [Fact]
    public void AlbumHeader_UsesTheAlbumArtistLine()
    {
        var album = new Album { Artist = "Simon & Garfunkel; Paul Simon" };
        var tokens = ArtistTokenItem.FromCredit(album.ArtistDisplay);
        Assert.Equal(new[] { "Simon & Garfunkel", "Paul Simon" }, tokens.Select(t => t.Name));
        Assert.Equal("Simon & Garfunkel, Paul Simon", Rendered(tokens));
    }

    [Fact]
    public void Header_CreditWithNoSplittableName_IsOneItem()
    {
        var tokens = ArtistTokenItem.FromCredit("  Solo Artist ");
        Assert.Single(tokens);
        Assert.Equal("Solo Artist", tokens[0].Name);
        Assert.True(tokens[0].IsLast);
        Assert.Empty(ArtistTokenItem.FromCredit(" "));
    }
}
