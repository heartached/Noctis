using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #51: configurable artist-tag separators and the "Group Artists By" mode.
/// The pure overloads are exercised in parallel-safe tests; the process-wide
/// configuration is exercised only from <see cref="ArtistCreditConfigurationTests"/>,
/// which xunit runs after the parallel collections so no other test observes the swap.
/// </summary>
public class ArtistGroupingTests
{
    private static readonly string[] Defaults = ArtistCredit.DefaultSeparators.ToArray();

    [Theory]
    [InlineData("Bad Bunny & Bomba Estéreo", new[] { "Bad Bunny", "Bomba Estéreo" })]
    [InlineData("A / B; C, D", new[] { "A", "B", "C", "D" })]
    [InlineData("Metro Boomin feat. Drake", new[] { "Metro Boomin", "Drake" })]
    [InlineData("Metro Boomin feat Drake", new[] { "Metro Boomin", "Drake" })]      // dot optional
    [InlineData("Metro Boomin FEAT. Drake", new[] { "Metro Boomin", "Drake" })]     // case-insensitive
    [InlineData("Metro Boomin ft. Drake", new[] { "Metro Boomin", "Drake" })]
    [InlineData("Metro Boomin featuring Drake", new[] { "Metro Boomin", "Drake" })]
    [InlineData("Taylor Swift.", new[] { "Taylor Swift." })]                        // "ft." must not bite "Swift."
    [InlineData("Drake, Drake", new[] { "Drake" })]                                 // de-duplicated
    public void DefaultSeparators_SplitCollaborationCredits(string credit, string[] expected)
        => Assert.Equal(expected, ArtistCredit.Split(credit, Defaults));

    [Theory]
    [InlineData("Lil Nas X")]
    [InlineData("Florence and the Machine")]
    [InlineData("Sly and the Family Stone")]
    [InlineData("Bill Withers with Grover Washington")]
    [InlineData("Kraftwerk")]
    public void DefaultSeparators_KeepBareWordsInsideNamesWhole(string credit)
        => Assert.Equal(new[] { credit }, ArtistCredit.Split(credit, Defaults));

    [Fact]
    public void CustomSeparators_ApplyExactly()
    {
        // Without "&" the duo stays whole; with "and" added the sentence splits.
        Assert.Equal(new[] { "Simon & Garfunkel" }, ArtistCredit.Split("Simon & Garfunkel", new[] { "/", ";" }));
        Assert.Equal(new[] { "Simon", "Garfunkel" }, ArtistCredit.Split("Simon and Garfunkel", new[] { "and" }));
        // A word separator only matches whole words: "and" must not split "Andrea / Sandra".
        Assert.Equal(new[] { "Andrea", "Sandra" }, ArtistCredit.Split("Andrea / Sandra", new[] { "/", "and" }));
        // Regex metacharacters are literal.
        Assert.Equal(new[] { "A", "B" }, ArtistCredit.Split("A | B", new[] { "|" }));
        Assert.Equal(new[] { "A", "B" }, ArtistCredit.Split("A + B", new[] { "+" }));
    }

    [Fact]
    public void NormalizeSeparators_TrimsDedupesAndFallsBackToDefaults()
    {
        Assert.Equal(new[] { "/", "FEAT." }, ArtistCredit.NormalizeSeparators(new[] { " / ", "", "FEAT.", "feat." })); // first spelling wins
        Assert.Equal(Defaults, ArtistCredit.NormalizeSeparators(Array.Empty<string>()));
        Assert.Equal(Defaults, ArtistCredit.NormalizeSeparators(null));
    }

    [Theory]
    [InlineData("Artist", ArtistGroupMode.Artist)]
    [InlineData("albumartist", ArtistGroupMode.AlbumArtist)]
    [InlineData("AlbumArtist", ArtistGroupMode.AlbumArtist)]
    [InlineData("1", ArtistGroupMode.Artist)]
    [InlineData("", ArtistGroupMode.Artist)]
    [InlineData(null, ArtistGroupMode.Artist)]
    [InlineData("bogus", ArtistGroupMode.Artist)]
    public void GroupMode_ParsesByNameOnly(string? setting, ArtistGroupMode expected)
        => Assert.Equal(expected, ArtistGroupModes.Parse(setting));

    [Fact]
    public void GroupingArtist_FollowsMode()
    {
        var featured = new Track { Artist = "Drake feat. Rihanna", AlbumArtist = "Drake" };
        Assert.Equal("Drake", featured.GetGroupingArtist(ArtistGroupMode.Artist));
        Assert.Equal("Drake", featured.GetGroupingArtist(ArtistGroupMode.AlbumArtist));

        var compilationCut = new Track { Artist = "Rihanna", AlbumArtist = Track.VariousArtists };
        Assert.Equal("Rihanna", compilationCut.GetGroupingArtist(ArtistGroupMode.Artist));
        Assert.Equal(Track.VariousArtists, compilationCut.GetGroupingArtist(ArtistGroupMode.AlbumArtist));

        // No album-artist tag: album-artist mode falls back to the track artist.
        var loose = new Track { Artist = "Rihanna / Drake", AlbumArtist = "" };
        Assert.Equal("Rihanna", loose.GetGroupingArtist(ArtistGroupMode.AlbumArtist));

        // Nothing at all still yields the shared placeholder bucket.
        var untagged = new Track { Artist = "", AlbumArtist = "" };
        Assert.Equal("Unknown Artist", untagged.GetGroupingArtist(ArtistGroupMode.AlbumArtist));
    }

    [Fact]
    public void FreshSettings_GroupByArtistWithDefaultSeparators()
    {
        var fresh = new AppSettings();
        Assert.Equal(ArtistGroupMode.Artist, ArtistGroupModes.Parse(fresh.ArtistGroupMode));
        Assert.Equal(Defaults, fresh.ArtistTagSeparators);
    }

    [Fact]
    public void Signature_ChangesWithModeOrSeparators()
    {
        var a = ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Defaults);
        Assert.Equal(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, Defaults.ToList()));
        Assert.NotEqual(a, ArtistCredit.BuildSignature(ArtistGroupMode.AlbumArtist, Defaults));
        Assert.NotEqual(a, ArtistCredit.BuildSignature(ArtistGroupMode.Artist, new[] { "/" }));
    }
}

[CollectionDefinition("ArtistCredit global configuration", DisableParallelization = true)]
public class ArtistCreditConfigurationCollection { }

/// <summary>
/// Touches the process-wide tokenizer. Runs in its own non-parallel collection and always
/// restores the defaults, so the parallel suites never see a foreign separator set.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistCreditConfigurationTests
{
    [Fact]
    public void Configure_RetokenizesCachedPrimaryArtist_AndBumpsVersionOnlyOnChange()
    {
        try
        {
            var track = new Track { Artist = "Simon & Garfunkel" };
            Assert.Equal("Simon", track.PrimaryArtist);

            var v0 = ArtistCredit.Version;
            ArtistCredit.Configure(ArtistGroupMode.Artist, ArtistCredit.DefaultSeparators);
            Assert.Equal(v0, ArtistCredit.Version); // no-op: nothing changed

            ArtistCredit.Configure(ArtistGroupMode.AlbumArtist, new[] { "/", "feat." });
            Assert.NotEqual(v0, ArtistCredit.Version);
            Assert.Equal(ArtistGroupMode.AlbumArtist, ArtistCredit.GroupMode);

            // The cached parse is invalidated by the version bump, not by an Artist write.
            Assert.Equal("Simon & Garfunkel", track.PrimaryArtist);
            Assert.Equal("Simon & Garfunkel", Track.GetPrimaryArtist("Simon & Garfunkel feat. Nobody"));
            Assert.Equal(ArtistCredit.BuildSignature(ArtistGroupMode.AlbumArtist, new[] { "/", "feat." }), ArtistCredit.Signature);
        }
        finally
        {
            ArtistCredit.ResetToDefaults();
        }

        Assert.Equal(ArtistGroupMode.Artist, ArtistCredit.GroupMode);
        Assert.Equal("Simon", Track.GetPrimaryArtist("Simon & Garfunkel"));
    }
}
