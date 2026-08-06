using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards the "(feat. X)" title-to-artist merge helpers: featured-name extraction,
/// whole-word containment (a credited "Maxwell" must not swallow a featured "Max"),
/// and the un-merge candidate filter the live toggle-off pass relies on.
/// </summary>
public class MergeFeaturedFromTitlesTests
{
    [Theory]
    [InlineData("Song (feat. Drake)", new[] { "Drake" })]
    [InlineData("Song [ft. Drake]", new[] { "Drake" })]
    [InlineData("Song (featuring Drake)", new[] { "Drake" })]
    [InlineData("Song (FEAT. Drake)", new[] { "Drake" })]
    [InlineData("Song (feat. Drake & Rihanna)", new[] { "Drake", "Rihanna" })]
    [InlineData("Song (feat. Drake, Rihanna and Future)", new[] { "Drake", "Rihanna", "Future" })]
    [InlineData("Song (Remix) (feat. Drake)", new[] { "Drake" })]
    [InlineData("Song", new string[0])]
    [InlineData("Song feat. Drake", new string[0])] // bare credit outside ()/[] is not parsed
    [InlineData("", new string[0])]
    public void ExtractFeaturedNames_ParsesTitleCredit(string title, string[] expected)
        => Assert.Equal(expected, MetadataService.ExtractFeaturedNamesFromTitle(title));

    [Theory]
    [InlineData("Metro Boomin", "Song (feat. Drake)", "Metro Boomin & Drake")]
    [InlineData("Metro Boomin & Drake", "Song (feat. Drake)", "Metro Boomin & Drake")] // already credited
    [InlineData("Maxwell", "Song (feat. Max)", "Maxwell & Max")] // substring is not a credit
    [InlineData("Max Wells", "Song (feat. Max)", "Max Wells")]   // whole word is
    [InlineData("Drake", "Song", "Drake")]
    [InlineData("Metro Boomin", "Song (feat. Drake & Rihanna)", "Metro Boomin & Drake & Rihanna")]
    public void EnrichArtistFromTitle_MergesMissingCreditsOnly(string artist, string title, string expected)
        => Assert.Equal(expected, MetadataService.EnrichArtistFromTitle(artist, title));

    [Theory]
    [InlineData("Metro Boomin & Drake", "Song (feat. Drake)", true)]
    [InlineData("Metro Boomin", "Song (feat. Drake)", false)]
    [InlineData("Metro Boomin", "Song", false)]
    [InlineData("", "Song (feat. Drake)", false)]
    public void MayHaveMergedFeaturedCredit_FlagsOnlyMergedLookingTracks(string artist, string title, bool expected)
        => Assert.Equal(expected, MetadataService.MayHaveMergedFeaturedCredit(artist, title));
}
