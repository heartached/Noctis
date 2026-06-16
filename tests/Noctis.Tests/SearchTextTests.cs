using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("Don't You", "dontyou")]
    [InlineData("Don’t You", "dontyou")]          // curly apostrophe (U+2019)
    [InlineData("Taylor's Version", "taylorsversion")]
    [InlineData("Beyoncé", "beyonce")]            // é → e
    [InlineData("  Hello,  World! ", "helloworld")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_FoldsToKey(string? input, string expected)
        => Assert.Equal(expected, SearchText.Normalize(input));

    [Theory]
    [InlineData("Don't You (Taylor's Version)", "dont you", true)]
    [InlineData("Don’t You", "Dont You", true)]   // curly apostrophe in source
    [InlineData("Don’t You", "DONTYOU", true)]
    [InlineData("Beyoncé", "beyonce", true)]
    [InlineData("Cruel Summer", "reputation", false)]
    [InlineData("Anything", "", true)]                 // blank query matches everything
    [InlineData("", "x", false)]                       // blank source, real query
    public void Matches_PunctuationAndAccentInsensitive(string? source, string query, bool expected)
        => Assert.Equal(expected, SearchText.Matches(source, query));
}
