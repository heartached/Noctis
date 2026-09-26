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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultTrackAndVolumeKeys_StayWithAFocusedTextBox_OtherModifiedKeysDoNot(bool isMac)
    {
        foreach (var action in new[] { ShortcutAction.NextTrack, ShortcutAction.PreviousTrack,
                     ShortcutAction.VolumeUp, ShortcutAction.VolumeDown, ShortcutAction.PlayPause })
        {
            var g = ShortcutDefaults.For(action, isMac);
            Assert.True(ShortcutDefaults.IsTextBoxKey(g.Key, g.KeyModifiers), action.ToString());
        }
        foreach (var action in new[] { ShortcutAction.ToggleQueue, ShortcutAction.SearchLibrary,
                     ShortcutAction.CommandPalette, ShortcutAction.ToggleFavorite })
        {
            var g = ShortcutDefaults.For(action, isMac);
            Assert.False(ShortcutDefaults.IsTextBoxKey(g.Key, g.KeyModifiers), action.ToString());
        }
        Assert.True(ShortcutDefaults.IsTextBoxKey(Key.Back, KeyModifiers.Control));
    }

    [Fact]
    public void EveryAction_HasExactlyOneDescriptor_AndAValidDefaultOnBothPlatforms()
    {
        var actions = System.Enum.GetValues<ShortcutAction>();
        Assert.Equal(actions.Length, ShortcutDefaults.All.Count);
        Assert.Equal(actions.OrderBy(a => a), ShortcutDefaults.All.Select(d => d.Action).OrderBy(a => a));

        foreach (var action in actions)
        {
            Assert.True(ShortcutDefaults.IsValid(ShortcutDefaults.For(action, false)));
            Assert.True(ShortcutDefaults.IsValid(ShortcutDefaults.For(action, true)));
        }
    }
}
