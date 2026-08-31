using System;
using System.Collections.Generic;
using Avalonia.Input;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ShortcutServiceTests
{
    private static ShortcutService Win() => new(isMac: false);

    private static KeyEventArgs Press(Key key, KeyModifiers mods = KeyModifiers.None)
        => new() { Key = key, KeyModifiers = mods, RoutedEvent = InputElement.KeyDownEvent };

    [Fact]
    public void FreshService_ReturnsDefaults_AndReportsThemAsDefault()
    {
        var s = Win();
        Assert.Equal(new KeyGesture(Key.Right, KeyModifiers.Control), s.Get(ShortcutAction.NextTrack));
        Assert.True(s.IsDefault(ShortcutAction.NextTrack));
    }

    [Fact]
    public void Set_OverridesGesture_AndRaisesChangedOnce()
    {
        var s = Win();
        var raised = 0;
        s.Changed += (_, _) => raised++;

        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));

        Assert.Equal(new KeyGesture(Key.P), s.Get(ShortcutAction.PlayPause));
        Assert.False(s.IsDefault(ShortcutAction.PlayPause));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Set_SameGestureTwice_DoesNotRaiseChangedAgain()
    {
        var s = Win();
        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));
        var raised = 0;
        s.Changed += (_, _) => raised++;

        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Set_BackToDefault_ClearsTheOverride()
    {
        var s = Win();
        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));
        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.Space));
        Assert.True(s.IsDefault(ShortcutAction.PlayPause));
    }

    [Fact]
    public void Set_GestureOwnedByAnotherAction_IsRefused()
    {
        var s = Win();
        var ex = Assert.Throws<ShortcutConflictException>(() => s.Set(ShortcutAction.NextTrack, new KeyGesture(Key.Space)));
        Assert.Equal(ShortcutAction.PlayPause, ex.Other);
        Assert.True(s.IsDefault(ShortcutAction.NextTrack));
    }

    [Fact]
    public void FindConflict_IgnoresTheActionItself()
    {
        var s = Win();
        Assert.Null(s.FindConflict(new KeyGesture(Key.Space), ShortcutAction.PlayPause));
        Assert.Equal(ShortcutAction.PlayPause, s.FindConflict(new KeyGesture(Key.Space), ShortcutAction.NextTrack));
    }

    [Fact]
    public void Set_ModifierOnlyGesture_IsRefused()
    {
        var s = Win();
        Assert.Throws<ArgumentException>(() => s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.LeftShift, KeyModifiers.Shift)));
    }

    [Fact]
    public void Set_Null_UnbindsTheAction()
    {
        var s = Win();
        s.Set(ShortcutAction.PlayPause, null);
        Assert.Null(s.Get(ShortcutAction.PlayPause));
        Assert.False(s.IsDefault(ShortcutAction.PlayPause));
        Assert.Null(s.TryMatch(Press(Key.Space)));
        // The freed key can now be given to something else.
        s.Set(ShortcutAction.NextTrack, new KeyGesture(Key.Space));
        Assert.Equal(ShortcutAction.NextTrack, s.TryMatch(Press(Key.Space)));
    }

    [Fact]
    public void Reset_RestoresDefault_AndResetAllRestoresEverything()
    {
        var s = Win();
        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));
        s.Set(ShortcutAction.NextTrack, new KeyGesture(Key.N, KeyModifiers.Alt));

        s.Reset(ShortcutAction.PlayPause);
        Assert.True(s.IsDefault(ShortcutAction.PlayPause));
        Assert.False(s.IsDefault(ShortcutAction.NextTrack));

        s.ResetAll();
        Assert.True(s.IsDefault(ShortcutAction.NextTrack));
    }

    [Fact]
    public void TryMatch_RequiresExactModifiers()
    {
        var s = Win();
        Assert.Equal(ShortcutAction.ToggleFullscreen, s.TryMatch(Press(Key.F11)));
        Assert.Null(s.TryMatch(Press(Key.F11, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.VolumeUp, s.TryMatch(Press(Key.Up, KeyModifiers.Control)));
        Assert.Null(s.TryMatch(Press(Key.Up)));
        Assert.Equal(ShortcutAction.DebugPanel, s.TryMatch(Press(Key.D, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Null(s.TryMatch(Press(Key.D, KeyModifiers.Control)));
    }

    [Fact]
    public void SaveTo_WritesOnlyOverrides_NullWhenAllDefault()
    {
        var s = Win();
        var settings = new AppSettings();

        s.SaveTo(settings);
        Assert.Null(settings.Shortcuts);

        s.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P));
        s.Set(ShortcutAction.NextTrack, null);
        s.SaveTo(settings);

        Assert.NotNull(settings.Shortcuts);
        Assert.Equal(2, settings.Shortcuts!.Count);
        Assert.Equal("P", settings.Shortcuts["PlayPause"]);
        Assert.Equal("", settings.Shortcuts["NextTrack"]);
    }

    [Fact]
    public void Load_AppliesOverrides_AndSkipsGarbage()
    {
        var s = Win();
        var settings = new AppSettings
        {
            Shortcuts = new Dictionary<string, string>
            {
                ["PlayPause"] = "P",
                ["NextTrack"] = "",
                ["Bogus"] = "Q",
                ["PreviousTrack"] = "not a key at all",
                ["VolumeUp"] = "Ctrl+Up",   // equals the default → not an override
            },
        };

        s.Load(settings);

        Assert.Equal(new KeyGesture(Key.P), s.Get(ShortcutAction.PlayPause));
        Assert.Null(s.Get(ShortcutAction.NextTrack));
        Assert.True(s.IsDefault(ShortcutAction.PreviousTrack));
        Assert.True(s.IsDefault(ShortcutAction.VolumeUp));
    }

    [Fact]
    public void GestureStrings_RoundTrip_ThroughSaveAndLoad()
    {
        var a = Win();
        a.Set(ShortcutAction.PlayPause, new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Alt));
        a.Set(ShortcutAction.SearchLibrary, new KeyGesture(Key.OemQuestion, KeyModifiers.Shift));
        var settings = new AppSettings();
        a.SaveTo(settings);

        var b = Win();
        b.Load(settings);

        Assert.Equal(a.Get(ShortcutAction.PlayPause), b.Get(ShortcutAction.PlayPause));
        Assert.Equal(a.Get(ShortcutAction.SearchLibrary), b.Get(ShortcutAction.SearchLibrary));
    }

    [Fact]
    public void MacService_UsesCommandForTransport()
    {
        var s = new ShortcutService(isMac: true);
        Assert.Equal(new KeyGesture(Key.Right, KeyModifiers.Meta), s.Get(ShortcutAction.NextTrack));
        Assert.Equal(ShortcutAction.NextTrack, s.TryMatch(Press(Key.Right, KeyModifiers.Meta)));
        Assert.Null(s.TryMatch(Press(Key.Right, KeyModifiers.Control)));
    }
}
