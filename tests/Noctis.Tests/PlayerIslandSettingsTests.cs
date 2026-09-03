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
        Assert.Equal(15, s.PlaybackBarSkipSeconds);
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
