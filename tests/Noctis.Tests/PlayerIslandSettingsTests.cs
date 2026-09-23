using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class PlayerIslandSettingsTests
{
    [Fact]
    public void FreshInstall_HasNoIslandExtras_And15SecondSkip()
    {
        var s = new AppSettings();
        Assert.False(s.PlaybackBarShowSkipButtons);
        Assert.False(s.PlaybackBarShowPlaybackSpeed);
        Assert.False(s.PlaybackBarShowSleepTimer);
        Assert.False(s.PlaybackBarShowShuffle);
        // Repeat and the favorite heart became opt-in with the track-box layout.
        Assert.False(s.PlaybackBarShowRepeat);
        Assert.False(s.PlaybackBarShowFavorite);
        // Elapsed / remaining time in the track box (Discord, Luwi 09-21) is opt-in too.
        Assert.False(s.PlaybackBarShowTime);
        // The mini player button (#80) is the one extra that starts on, for discoverability.
        Assert.True(s.PlaybackBarShowMiniPlayer);
        Assert.Equal(15, s.PlaybackBarSkipSeconds);
        Assert.Equal(0.07, s.PlaybackBarTrackBoxOpacity);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(1.7, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(double.NaN, 0.07)]
    public void Clamp_KeepsTrackBoxOpacity_InRange(double stored, double expected)
    {
        var s = new AppSettings { PlaybackBarTrackBoxOpacity = stored };
        s.ClampToValidRanges();
        Assert.Equal(expected, s.PlaybackBarTrackBoxOpacity);
    }

    /// <summary>A stored width equal to an old stock width is the untouched default, not a
    /// choice: it follows the current stock width (590 → 626 → 536) instead of leaving a
    /// stretched pill. A genuinely user-chosen width is left alone.</summary>
    [Theory]
    [InlineData(590, 536)]
    [InlineData(626, 536)]
    [InlineData(536, 536)]
    [InlineData(720, 720)]
    public void Clamp_MigratesOldStockWidths_ToTheCurrentDefault(double stored, double expected)
    {
        var s = new AppSettings { PlaybackBarWidth = stored };
        s.ClampToValidRanges();
        Assert.Equal(expected, s.PlaybackBarWidth);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(12, 15)]
    [InlineData(-5, 15)]
    [InlineData(0, 15)]
    public void Clamp_NormalizesSkipSeconds_ToTheThreeChoices(int stored, int expected)
    {
        var s = new AppSettings { PlaybackBarSkipSeconds = stored };
        s.ClampToValidRanges();
        Assert.Equal(expected, s.PlaybackBarSkipSeconds);
    }
}
