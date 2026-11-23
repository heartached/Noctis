using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Listening › ReplayGain on the phone: Off / Track / Album, the pre-amp and Prevent
/// Clipping, saved in settings.json under the desktop's names (ReplayGainMode, ReplayGainPreampDb)
/// and handed to the player at start and whenever one changes.
/// </summary>
public class MobileReplayGainSettingsTests
{
    private static Track[] Songs(int n) => Enumerable.Range(0, n).Select(i => MobileFixtures.Song($"S{i}")).ToArray();

    [Fact]
    public async Task Defaults_AreOff_ZeroPreamp_PreventClippingOn()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        await rig.Shell.InitializeAsync();
        var player = rig.Shell.Player;
        Assert.Equal(ReplayGainMode.Off, player.ReplayGainMode);
        Assert.Equal(0, player.ReplayGainPreampDb);
        Assert.True(player.ReplayGainPreventClipping);
        Assert.Equal(("Off", 0.0, true), rig.Player.ReplayGainCalls.Last());
    }

    [Fact]
    public async Task SavedValues_AreReadAtStart_AndReachThePlayer_BeforeTheQueueRestores()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.ReplayGainMode = "Album";
            s.ReplayGainPreampDb = 3.5;
            s.ReplayGainPreventClipping = false;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(ReplayGainMode.Album, rig.Shell.Player.ReplayGainMode);
        Assert.Equal(3.5, rig.Shell.Player.ReplayGainPreampDb);
        Assert.False(rig.Shell.Player.ReplayGainPreventClipping);
        Assert.Equal(("Album", 3.5, false), rig.Player.ReplayGainCalls.Last());
    }

    [Fact]
    public async Task TheDesktopsAuto_ReadsAsAlbum_AndAnOutOfRangePreampIsClamped()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.ReplayGainMode = "Auto";
            s.ReplayGainPreampDb = 40;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.Equal(ReplayGainMode.Album, rig.Shell.Player.ReplayGainMode);
        Assert.Equal(12, rig.Shell.Player.ReplayGainPreampDb);
    }

    [Fact]
    public async Task Changes_ReachThePlayerAtOnce_AndAreSaved()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        await rig.Shell.InitializeAsync();
        var player = rig.Shell.Player;

        player.ReplayGainMode = ReplayGainMode.Track;
        Assert.Equal(("Track", 0.0, true), rig.Player.ReplayGainCalls.Last());
        player.ReplayGainPreampDb = -4.5;
        Assert.Equal(("Track", -4.5, true), rig.Player.ReplayGainCalls.Last());
        player.ReplayGainPreventClipping = false;
        Assert.Equal(("Track", -4.5, false), rig.Player.ReplayGainCalls.Last());

        await player.PendingReplayGainSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Track", saved.ReplayGainMode);
        Assert.Equal(-4.5, saved.ReplayGainPreampDb);
        Assert.False(saved.ReplayGainPreventClipping);
    }

    [Fact]
    public async Task APreampDrag_SavesOnceItSettles()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        await rig.Shell.InitializeAsync();
        var player = rig.Shell.Player;
        for (var db = 0.5; db <= 6; db += 0.5) player.ReplayGainPreampDb = db;
        Assert.Equal(6, rig.Player.ReplayGainCalls.Last().Preamp);   // the player follows every step
        await player.PendingReplayGainSave;
        Assert.Equal(6, (await rig.Persistence.LoadSettingsAsync()).ReplayGainPreampDb);
    }

    [Fact]
    public async Task AChangeBeforeTheLoad_IsKept_AndSaved()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.ReplayGainMode = "Album";
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.Player.ReplayGainMode = ReplayGainMode.Track;   // Settings opened during a cold start
        await rig.Shell.InitializeAsync();
        Assert.Equal(ReplayGainMode.Track, rig.Shell.Player.ReplayGainMode);
        await rig.Shell.Player.PendingReplayGainSave;
        Assert.Equal("Track", (await rig.Persistence.LoadSettingsAsync()).ReplayGainMode);
    }

    [Fact]
    public void PreampLabel_ReadsLikeTheDesktops()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        var player = rig.Shell.Player;
        Assert.Equal("0.0 dB", player.ReplayGainPreampLabel);
        player.ReplayGainPreampDb = 2.5;
        Assert.Equal("+2.5 dB", player.ReplayGainPreampLabel);
        player.ReplayGainPreampDb = -12;
        Assert.Equal("-12.0 dB", player.ReplayGainPreampLabel);
    }

    [AvaloniaFact]
    public async Task TheListeningRow_OpensThePane_WithTheChooserSliderAndSwitch()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        var shell = rig.Shell;
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
            await vm.Loaded;
            MobileFixtures.Land(window);
            var page = view.GetLogicalDescendants().OfType<SettingsPage>().Single();

            var row = MobileFixtures.Named<Button>(page, "ReplayGainRow");
            Assert.True(row.IsEffectivelyVisible);
            Assert.Equal("ReplayGain, Off", Avalonia.Automation.AutomationProperties.GetName(row));
            Assert.Equal("Off", MobileFixtures.Named<TextBlock>(page, "ReplayGainValueLabel").Text);

            row.Command!.Execute(row.CommandParameter);
            MobileFixtures.Land(window);
            Assert.Equal(SettingsPane.ReplayGain, vm.Pane);
            Assert.Equal("ReplayGain", vm.HeaderTitle);
            Assert.True(MobileFixtures.Named<StackPanel>(page, "ReplayGainPane").IsEffectivelyVisible);

            var modeRow = MobileFixtures.Named<Button>(page, "ReplayGainModeRow");
            Assert.Equal("Mode, Off", Avalonia.Automation.AutomationProperties.GetName(modeRow));
            vm.ShowReplayGainModeMenuCommand.Execute(null);
            var menu = Assert.IsType<OptionsMenuViewModel>(shell.PopMenu);
            Assert.Equal(new[] { "Off", "Track", "Album" }, menu.Items.Select(i => i.Label));
            Assert.Equal(new[] { true, false, false }, menu.Items.Select(i => i.IsChecked));
            menu.Items[2].PickCommand.Execute(null);
            Assert.Equal(ReplayGainMode.Album, shell.Player.ReplayGainMode);
            Assert.Equal("Album", vm.ReplayGainValue);
            Assert.Equal("Mode, Album", vm.ReplayGainModeRowName);
            Assert.Equal("ReplayGain, Album", vm.ReplayGainRowName);

            var slider = MobileFixtures.Named<Noctis.Controls.SmoothSlider>(page, "ReplayGainPreampSlider");
            Assert.Equal(-12, slider.Minimum);
            Assert.Equal(12, slider.Maximum);
            Assert.Equal("Pre-amp", Avalonia.Automation.AutomationProperties.GetName(slider));
            slider.SteppedValue = 3;
            Assert.Equal(3, shell.Player.ReplayGainPreampDb);
            Assert.Equal("+3.0 dB", MobileFixtures.Named<TextBlock>(page, "ReplayGainPreampLabel").Text);

            var clip = MobileFixtures.Named<ToggleButton>(page, "ReplayGainClippingSwitch");
            Assert.Equal("Prevent Clipping", Avalonia.Automation.AutomationProperties.GetName(clip));
            Assert.True(clip.IsChecked);
            clip.IsChecked = false;
            Assert.False(shell.Player.ReplayGainPreventClipping);
            Assert.Equal(("Album", 3.0, false), rig.Player.ReplayGainCalls.Last());
        }
        finally
        {
            window.Close();
        }
    }
}
