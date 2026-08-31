using System.Linq;
using Avalonia.Input;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class ShortcutDefaultsTests
{
    [Fact]
    public void NextTrack_IsCtrlRight_OnWindows_AndCmdRight_OnMac()
    {
        Assert.Equal(new KeyGesture(Key.Right, KeyModifiers.Control), ShortcutDefaults.For(ShortcutAction.NextTrack, isMac: false));
        Assert.Equal(new KeyGesture(Key.Right, KeyModifiers.Meta), ShortcutDefaults.For(ShortcutAction.NextTrack, isMac: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlatformNeutralKeys_AreTheSameEverywhere(bool isMac)
    {
        Assert.Equal(new KeyGesture(Key.Space), ShortcutDefaults.For(ShortcutAction.PlayPause, isMac));
        Assert.Equal(new KeyGesture(Key.F11), ShortcutDefaults.For(ShortcutAction.ToggleFullscreen, isMac));
        Assert.Equal(new KeyGesture(Key.F2), ShortcutDefaults.For(ShortcutAction.ToggleFullscreenAlt, isMac));
    }

    [Fact]
    public void DebugPanel_KeepsShift_OnBothPlatforms()
    {
        Assert.Equal(new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift), ShortcutDefaults.For(ShortcutAction.DebugPanel, false));
        Assert.Equal(new KeyGesture(Key.D, KeyModifiers.Meta | KeyModifiers.Shift), ShortcutDefaults.For(ShortcutAction.DebugPanel, true));
    }

    [Fact]
    public void ModifierOnlyAndNoneKeys_AreNotValid()
    {
        Assert.False(ShortcutDefaults.IsValid(new KeyGesture(Key.LeftCtrl, KeyModifiers.Control)));
        Assert.False(ShortcutDefaults.IsValid(new KeyGesture(Key.RightShift, KeyModifiers.Shift)));
        Assert.False(ShortcutDefaults.IsValid(new KeyGesture(Key.None)));
        Assert.True(ShortcutDefaults.IsValid(new KeyGesture(Key.P)));
        Assert.True(ShortcutDefaults.IsValid(new KeyGesture(Key.Space)));
    }

    [Fact]
    public void EveryAction_HasExactlyOneDescriptor_AndOnlyDebugPanelIsDeveloperOnly()
    {
        var actions = System.Enum.GetValues<ShortcutAction>();
        Assert.Equal(actions.Length, ShortcutDefaults.All.Count);
        Assert.Equal(actions.OrderBy(a => a), ShortcutDefaults.All.Select(d => d.Action).OrderBy(a => a));
        Assert.Equal(new[] { ShortcutAction.DebugPanel }, ShortcutDefaults.All.Where(d => d.DeveloperOnly).Select(d => d.Action));

        // Every action must resolve to a valid default on both platforms.
        foreach (var action in actions)
        {
            Assert.True(ShortcutDefaults.IsValid(ShortcutDefaults.For(action, false)));
            Assert.True(ShortcutDefaults.IsValid(ShortcutDefaults.For(action, true)));
        }
    }
}
