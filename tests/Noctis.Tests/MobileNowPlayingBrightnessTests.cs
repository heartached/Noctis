using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone beta 2 request: "being able to change the darkness or lightness of the now playing
/// background". Settings › Appearance › Now Playing Background, -50 (darker) to 50 (brighter);
/// 0, the default, is the stock look. Saved with the player bar's settings, applied live.
/// </summary>
public class MobileNowPlayingBrightnessTests
{
    [Fact]
    public void Zero_IsTheStockLook_AndTheEndsDarkenOrLighten()
    {
        Assert.Equal(0, new Noctis.Models.AppSettings().MobileNowPlayingBrightness);
        using var rig = MobileFixtures.MakeRig();
        var shell = rig.Shell;
        Assert.Equal(1, shell.NowPlayingShadeOpacity);
        Assert.Equal(0, shell.NowPlayingDimOpacity);
        Assert.Equal("0", shell.NowPlayingBrightnessLabel);

        shell.NowPlayingBrightness = 50;
        Assert.Equal(0.4, shell.NowPlayingShadeOpacity, 3);
        Assert.Equal(0, shell.NowPlayingDimOpacity);
        Assert.Equal("+50", shell.NowPlayingBrightnessLabel);

        shell.NowPlayingBrightness = -25;
        Assert.Equal(1, shell.NowPlayingShadeOpacity);
        Assert.Equal(0.3, shell.NowPlayingDimOpacity, 3);
        Assert.Equal("-25", shell.NowPlayingBrightnessLabel);
    }

    [Fact]
    public async Task TheBrightness_IsSaved_AndReadBack_Clamped()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingBrightness = -30;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(-30, rig.Shell.NowPlayingBrightness);

        rig.Shell.NowPlayingBrightness = 20;
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal(20, (await rig.Persistence.LoadSettingsAsync()).MobileNowPlayingBrightness);

        using var rig2 = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileNowPlayingBrightness = 400;   // a hand-edited file
            await p.SaveSettingsAsync(s);
        });
        await rig2.Shell.InitializeAsync();
        Assert.Equal(50, rig2.Shell.NowPlayingBrightness);
    }

    /// <summary>The page's shade and dim layers follow the setting live.</summary>
    [AvaloniaFact]
    public void ThePage_FollowsTheSetting_Live()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var shade = MobileFixtures.Named<Border>(page, "WashShade");
        var dim = MobileFixtures.Named<Border>(page, "WashDim");
        Assert.Equal(1, shade.Opacity);
        Assert.Equal(0, dim.Opacity);

        rig.Shell.NowPlayingBrightness = -50;
        Assert.Equal(0.6, dim.Opacity, 3);
        rig.Shell.NowPlayingBrightness = 50;
        Assert.Equal(0, dim.Opacity);
        Assert.Equal(0.4, shade.Opacity, 3);
        window.Close();
    }
}
