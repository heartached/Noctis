using Noctis.Models;
using Noctis.ViewModels;
using Xunit;
namespace Noctis.Tests;
public class SongTransitionSettingsTests
{
    [Theory]
    [InlineData(false, "Crossfade", AutoMixTransitionMode.Off)]
    [InlineData(true, "Crossfade", AutoMixTransitionMode.Crossfade)]
    [InlineData(true, "AutoMix", AutoMixTransitionMode.AutoMix)]
    public void StyleMapsToMode(bool enabled, string style, AutoMixTransitionMode expected)
    {
        Assert.Equal(expected, SettingsViewModel.MapTransitionMode(enabled, style));
    }
    [Fact]
    public void LegacyCrossfadeEnabledMigratesToSongTransitions()
    {
        var s = new AppSettings { CrossfadeEnabled = true, SongTransitionsEnabled = false };
        SettingsViewModel.MigrateTransitionSettings(s);
        Assert.True(s.SongTransitionsEnabled);
        Assert.Equal("Crossfade", s.TransitionStyle);
    }
}
