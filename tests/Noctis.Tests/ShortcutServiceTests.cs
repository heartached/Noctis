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
    public void ToggleFavorite_DefaultsToPrimaryL_OnBothPlatforms()
    {
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Control), Win().Get(ShortcutAction.ToggleFavorite));
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Meta), new ShortcutService(isMac: true).Get(ShortcutAction.ToggleFavorite));
    }

    [Fact]
    public void ToggleLyrics_DefaultsToPrimaryShiftL_AndPrimaryLStaysFavorite()
    {
        // GitHub #103: the lyrics page gets its own key next to Favorite's Ctrl/⌘+L.
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift), Win().Get(ShortcutAction.ToggleLyrics));
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Meta | KeyModifiers.Shift), new ShortcutService(isMac: true).Get(ShortcutAction.ToggleLyrics));

        var win = Win();
        Assert.Equal(ShortcutAction.ToggleLyrics, win.TryMatch(Press(Key.L, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal(ShortcutAction.ToggleFavorite, win.TryMatch(Press(Key.L, KeyModifiers.Control)));
    }

    [Fact]
    public void EveryAction_HasADescriptor_AndADistinctDefault()
    {
        // A new action that forgets its descriptor never shows in Settings › Shortcuts; one
        // that reuses a default gesture silently shadows another action.
        var actions = Enum.GetValues<ShortcutAction>();
        Assert.Equal(actions.Length, ShortcutDefaults.All.Count);
        Assert.All(actions, a => Assert.Contains(ShortcutDefaults.All, d => d.Action == a));

        var win = Win();
        var gestures = new List<KeyGesture>();
        foreach (var a in actions) gestures.Add(win.Get(a));
        Assert.Equal(gestures.Count, new HashSet<KeyGesture>(gestures).Count);
    }

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
        Assert.Equal(ShortcutAction.SearchLibrary, s.TryMatch(Press(Key.F, KeyModifiers.Control)));
        Assert.Null(s.TryMatch(Press(Key.F, KeyModifiers.Control | KeyModifiers.Shift)));
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

    /// <summary>
    /// Settings saved before a default existed can already hold that chord on another
    /// action: Ctrl+Shift+L on Play/Pause from before GitHub #103 gave it to Lyrics. Load
    /// kept both, so one key belonged to two actions: the earlier one in display order won
    /// and the tab listed the chord twice. The user's own binding wins; the newer default
    /// stays out of the way until the key is free again.
    /// </summary>
    [Fact]
    public void Load_OverrideOnANewerDefault_ShadowsThatDefault_UntilFreed()
    {
        var ctrlShiftL = new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift);
        var s = Win();
        s.Load(new AppSettings { Shortcuts = new Dictionary<string, string> { ["PlayPause"] = "Ctrl+Shift+L" } });

        Assert.Equal(ctrlShiftL, s.Get(ShortcutAction.PlayPause));
        Assert.Null(s.Get(ShortcutAction.ToggleLyrics));
        Assert.Equal(ShortcutAction.PlayPause, s.TryMatch(Press(Key.L, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Null(s.FindConflict(new KeyGesture(Key.F9), ShortcutAction.ToggleLyrics));

        s.Reset(ShortcutAction.PlayPause);
        Assert.Equal(ctrlShiftL, s.Get(ShortcutAction.ToggleLyrics));
        Assert.Equal(ShortcutAction.ToggleLyrics, s.TryMatch(Press(Key.L, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    // ── GitHub #131 ──

    [Fact]
    public void EveryAction_HasADistinctDefault_OnMacToo()
    {
        var mac = new ShortcutService(isMac: true);
        var gestures = new List<KeyGesture>();
        foreach (var a in Enum.GetValues<ShortcutAction>()) gestures.Add(mac.Get(a)!);
        Assert.Equal(gestures.Count, new HashSet<KeyGesture>(gestures).Count);
    }

    /// <summary>Every action rebound at once (to its macOS default, which no Windows default
    /// uses) survives Save → Load, including the OemComma / bracket / digit keys.</summary>
    [Fact]
    public void EveryAction_Rebound_RoundTripsThroughSaveAndLoad()
    {
        var a = Win();
        foreach (var d in ShortcutDefaults.All)
            a.Set(d.Action, ShortcutDefaults.For(d.Action, isMac: true));
        var settings = new AppSettings();
        a.SaveTo(settings);

        var b = Win();
        b.Load(settings);
        foreach (var d in ShortcutDefaults.All)
        {
            Assert.Equal(ShortcutDefaults.For(d.Action, isMac: true), b.Get(d.Action));
            Assert.Equal(a.IsDefault(d.Action), b.IsDefault(d.Action));
        }
    }

    // ── Disabled actions (a Built-in Feature switched off, e.g. the Mini Player) ──

    private static readonly KeyGesture MiniDefault = new(Key.M, KeyModifiers.Control | KeyModifiers.Shift);
    private static readonly KeyGesture CtrlP = new(Key.P, KeyModifiers.Control);

    private static ShortcutService WithMiniOff()
    {
        var s = Win();
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        return s;
    }

    [Fact]
    public void DisabledAction_IsNotMatched_SoTheKeyFallsThrough()
    {
        var s = WithMiniOff();
        Assert.True(s.IsDisabled(ShortcutAction.ToggleMiniPlayer));
        Assert.Null(s.TryMatch(Press(Key.M, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Null(s.Get(ShortcutAction.ToggleMiniPlayer));
    }

    [Fact]
    public void DisabledAction_DoesNotBlockItsChord_ForAnotherAction()
    {
        var s = WithMiniOff();
        Assert.Null(s.FindConflict(MiniDefault, ShortcutAction.ToggleQueue));
        s.Set(ShortcutAction.ToggleQueue, MiniDefault); // no ShortcutConflictException
        Assert.Equal(ShortcutAction.ToggleQueue, s.TryMatch(Press(Key.M, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [Fact]
    public void DisabledAction_KeepsItsBinding_AcrossResetAll_AndGetsItBack()
    {
        var s = Win();
        s.Set(ShortcutAction.ToggleMiniPlayer, CtrlP);
        s.Set(ShortcutAction.NextTrack, new KeyGesture(Key.F8));
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });

        s.ResetAll();
        Assert.True(s.IsDefault(ShortcutAction.NextTrack));

        // Stored (and saved) while hidden.
        var settings = new AppSettings();
        s.SaveTo(settings);
        Assert.Equal("Ctrl+P", settings.Shortcuts!["ToggleMiniPlayer"]);

        s.SetDisabled(Array.Empty<ShortcutAction>());
        Assert.False(s.IsDisabled(ShortcutAction.ToggleMiniPlayer));
        Assert.Equal(CtrlP, s.Get(ShortcutAction.ToggleMiniPlayer));
        Assert.Equal(ShortcutAction.ToggleMiniPlayer, s.TryMatch(Press(Key.P, KeyModifiers.Control)));
    }

    [Fact]
    public void ComingBack_AfterItsChordWasGivenAway_TheOtherActionKeepsIt_AndItFallsBackToItsFreeDefault()
    {
        var s = Win();
        s.Set(ShortcutAction.ToggleMiniPlayer, CtrlP);
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        s.Set(ShortcutAction.ToggleQueue, CtrlP);

        s.SetDisabled(Array.Empty<ShortcutAction>());
        Assert.Equal(CtrlP, s.Get(ShortcutAction.ToggleQueue));
        Assert.Equal(MiniDefault, s.Get(ShortcutAction.ToggleMiniPlayer));
        Assert.Equal(ShortcutAction.ToggleQueue, s.TryMatch(Press(Key.P, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.ToggleMiniPlayer, s.TryMatch(Press(Key.M, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [Fact]
    public void ComingBack_WhenItsDefaultWasGivenAway_ItComesBackUnbound()
    {
        var s = WithMiniOff();
        s.Set(ShortcutAction.ToggleQueue, MiniDefault);

        s.SetDisabled(Array.Empty<ShortcutAction>());
        Assert.Equal(MiniDefault, s.Get(ShortcutAction.ToggleQueue));
        Assert.Null(s.Get(ShortcutAction.ToggleMiniPlayer));
        Assert.Equal(ShortcutAction.ToggleQueue, s.TryMatch(Press(Key.M, KeyModifiers.Control | KeyModifiers.Shift)));
    }

    [Fact]
    public void ComingBack_WhenBothItsChordAndDefaultWereGivenAway_ItComesBackUnbound_NeverTwoOnOneChord()
    {
        var s = Win();
        s.Set(ShortcutAction.ToggleMiniPlayer, CtrlP);
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        s.Set(ShortcutAction.ToggleQueue, CtrlP);
        s.Set(ShortcutAction.ToggleLyrics, MiniDefault);

        s.SetDisabled(Array.Empty<ShortcutAction>());
        Assert.Null(s.Get(ShortcutAction.ToggleMiniPlayer));
        var bound = new List<KeyGesture>();
        foreach (var d in ShortcutDefaults.All)
            if (s.Get(d.Action) is { } g) bound.Add(g);
        Assert.Equal(bound.Count, new HashSet<KeyGesture>(bound).Count);
    }

    [Fact]
    public void HiddenOverride_DoesNotShadowAnotherActionsDefault()
    {
        // Mini player bound to Ctrl+U (the queue's default) earlier... impossible while both
        // are live, so build it from a stored file: the hidden binding must not take Ctrl+U
        // away from the queue while the mini player is off.
        var s = Win();
        s.Load(new AppSettings { Shortcuts = new Dictionary<string, string> { ["ToggleMiniPlayer"] = "Ctrl+U" } });
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), s.Get(ShortcutAction.ToggleQueue));
        Assert.Equal(ShortcutAction.ToggleQueue, s.TryMatch(Press(Key.U, KeyModifiers.Control)));
    }

    [Fact]
    public void SetDisabled_RaisesChanged_OnlyWhenTheSetChanges()
    {
        var s = Win();
        var raised = 0;
        s.Changed += (_, _) => raised++;
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        s.SetDisabled(new[] { ShortcutAction.ToggleMiniPlayer });
        Assert.Equal(1, raised);
        s.SetDisabled(Array.Empty<ShortcutAction>());
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Issue131_Keys_MatchTheirActions()
    {
        var s = Win();
        Assert.Equal(ShortcutAction.OpenSettings, s.TryMatch(Press(Key.OemComma, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.ScanReplayGain, s.TryMatch(Press(Key.R, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal(ShortcutAction.CycleRepeat, s.TryMatch(Press(Key.R, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.SeekForward, s.TryMatch(Press(Key.Right, KeyModifiers.Shift)));
        Assert.Equal(ShortcutAction.NextTrack, s.TryMatch(Press(Key.Right, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.GoBack, s.TryMatch(Press(Key.Left, KeyModifiers.Alt)));
        Assert.Equal(ShortcutAction.ToggleMute, s.TryMatch(Press(Key.Down, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal(ShortcutAction.VolumeDown, s.TryMatch(Press(Key.Down, KeyModifiers.Control)));
        Assert.Equal(ShortcutAction.GoArtists, s.TryMatch(Press(Key.D4, KeyModifiers.Control)));

        var mac = new ShortcutService(isMac: true);
        Assert.Equal(ShortcutAction.OpenSettings, mac.TryMatch(Press(Key.OemComma, KeyModifiers.Meta)));
        Assert.Equal(ShortcutAction.GoBack, mac.TryMatch(Press(Key.OemOpenBrackets, KeyModifiers.Meta)));
        Assert.Null(mac.TryMatch(Press(Key.OemComma, KeyModifiers.Control)));
    }
}
