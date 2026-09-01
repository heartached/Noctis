using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The "Now Playing Artwork" setting is stored as a string (like TransitionStyle and
/// ReplayGainMode), so a hand-edited or future-version value must fall back to the
/// plain cover rather than throw.
/// </summary>
public class ArtworkMediumTests
{
    [Theory]
    [InlineData("Cover", ArtworkMedium.Cover)]
    [InlineData("CompactDisc", ArtworkMedium.CompactDisc)]
    [InlineData("Vinyl", ArtworkMedium.Vinyl)]
    [InlineData("Cassette", ArtworkMedium.Cassette)]
    [InlineData("vinyl", ArtworkMedium.Vinyl)]
    [InlineData("  cassette ", ArtworkMedium.Cassette)]
    public void Parse_AcceptsEveryMedium_CaseInsensitively(string setting, ArtworkMedium expected)
        => Assert.Equal(expected, ArtworkMediums.Parse(setting));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MiniDisc")]
    [InlineData("7")]
    [InlineData("2")]
    public void Parse_FallsBackToCover_ForAnythingUnknown(string? setting)
        => Assert.Equal(ArtworkMedium.Cover, ArtworkMediums.Parse(setting));

    [Fact]
    public void DefaultSetting_IsTheCover()
        => Assert.Equal(ArtworkMedium.Cover, ArtworkMediums.Parse(ArtworkMediums.DefaultSetting));
}
