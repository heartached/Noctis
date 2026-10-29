using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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
    }

    [Fact]
    public void Fullscreen_IsF11_OnWindows_AndCtrlCmdF_OnMac()
    {
        Assert.Equal(new KeyGesture(Key.F11), ShortcutDefaults.For(ShortcutAction.ToggleFullscreen, isMac: false));
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control), ShortcutDefaults.For(ShortcutAction.ToggleFullscreen, isMac: true));
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
                     ShortcutAction.CommandPalette, ShortcutAction.ToggleFavorite, ShortcutAction.ToggleLyrics })
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

    // ── GitHub #131 ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoTwoActions_ShareADefault(bool isMac)
    {
        var clashes = ShortcutDefaults.All
            .GroupBy(d => ShortcutDefaults.For(d.Action, isMac))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g.Select(d => d.Action)))
            .ToList();
        Assert.True(clashes.Count == 0, "Shared defaults: " + string.Join(", ", clashes));
    }

    /// <summary>The fixed keys no rebindable default may take: page / queue selection keys,
    /// dialog keys, clipboard + undo, and the OS window keys.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Defaults_StayOffFixedAndSystemKeys(bool isMac)
    {
        var primary = isMac ? KeyModifiers.Meta : KeyModifiers.Control;
        var reserved = new List<KeyGesture>
        {
            new(Key.Escape), new(Key.Enter), new(Key.Tab), new(Key.Delete), new(Key.Back),
            new(Key.Left), new(Key.Right), new(Key.Up), new(Key.Down),
            new(Key.OemOpenBrackets), new(Key.OemCloseBrackets), // Lyrics Studio nudge
            new(Key.A, primary), new(Key.C, primary), new(Key.V, primary), new(Key.X, primary),
            new(Key.Z, primary), new(Key.Y, primary), new(Key.Q, primary), new(Key.W, primary),
            new(Key.F4, KeyModifiers.Alt),
        };
        if (isMac)
            reserved.AddRange(new KeyGesture[] { new(Key.H, KeyModifiers.Meta), new(Key.M, KeyModifiers.Meta) });

        foreach (var d in ShortcutDefaults.All)
            Assert.DoesNotContain(ShortcutDefaults.For(d.Action, isMac), reserved);
    }

    [Fact]
    public void Issue131_Defaults()
    {
        void Both(ShortcutAction a, KeyGesture win, KeyGesture mac)
        {
            Assert.Equal(win, ShortcutDefaults.For(a, isMac: false));
            Assert.Equal(mac, ShortcutDefaults.For(a, isMac: true));
        }
        const KeyModifiers C = KeyModifiers.Control, M = KeyModifiers.Meta, S = KeyModifiers.Shift;
        Both(ShortcutAction.OpenSettings, new(Key.OemComma, C), new(Key.OemComma, M));
        Both(ShortcutAction.ScanReplayGain, new(Key.R, C | S), new(Key.R, M | S));
        Both(ShortcutAction.ToggleShuffle, new(Key.S, C), new(Key.S, M));
        Both(ShortcutAction.CycleRepeat, new(Key.R, C), new(Key.R, M));
        Both(ShortcutAction.ToggleMute, new(Key.Down, C | S), new(Key.Down, M | S));
        Both(ShortcutAction.SeekForward, new(Key.Right, S), new(Key.Right, S));
        Both(ShortcutAction.SeekBackward, new(Key.Left, S), new(Key.Left, S));
        Both(ShortcutAction.ToggleMiniPlayer, new(Key.M, C | S), new(Key.M, M | S));
        Both(ShortcutAction.GoBack, new(Key.Left, KeyModifiers.Alt), new(Key.OemOpenBrackets, M));
        Both(ShortcutAction.GoForward, new(Key.Right, KeyModifiers.Alt), new(Key.OemCloseBrackets, M));
        Both(ShortcutAction.GoHome, new(Key.D1, C), new(Key.D1, M));
        Both(ShortcutAction.GoSongs, new(Key.D2, C), new(Key.D2, M));
        Both(ShortcutAction.GoAlbums, new(Key.D3, C), new(Key.D3, M));
        Both(ShortcutAction.GoArtists, new(Key.D4, C), new(Key.D4, M));
    }

    /// <summary>Seek / mute / back-forward use caret keys, so a focused edit box keeps them
    /// (Shift+← selects, Alt+← is the box's own); the letter / digit / comma keys work from
    /// the search box like Ctrl+U does.</summary>
    [Fact]
    public void Issue131_TextBoxKeys()
    {
        foreach (var a in new[] { ShortcutAction.SeekForward, ShortcutAction.SeekBackward, ShortcutAction.ToggleMute,
                     ShortcutAction.GoBack, ShortcutAction.GoForward })
        {
            var g = ShortcutDefaults.For(a, isMac: false);
            Assert.True(ShortcutDefaults.IsTextBoxKey(g.Key, g.KeyModifiers), a.ToString());
        }
        foreach (var a in new[] { ShortcutAction.OpenSettings, ShortcutAction.ScanReplayGain, ShortcutAction.ToggleShuffle,
                     ShortcutAction.CycleRepeat, ShortcutAction.ToggleMiniPlayer, ShortcutAction.GoHome })
        {
            var g = ShortcutDefaults.For(a, isMac: false);
            Assert.False(ShortcutDefaults.IsTextBoxKey(g.Key, g.KeyModifiers), a.ToString());
        }
    }

    /// <summary>Settings › Shortcuts shows these through Loc.T: a key missing from
    /// Strings.resx renders as the raw key. Russian ships complete, so it needs them too.</summary>
    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.ru.resx")]
    public void EveryLabelAndGroup_IsInTheStrings(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var keys = XDocument.Load(Path.Combine(dir!, "src", "Noctis.UI", "Localization", file)).Root!
            .Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

        var needed = ShortcutDefaults.All.SelectMany(d => new[] { d.LabelKey, d.GroupKey })
            .Append("Shortcuts.NotSet").Append("Shortcuts.AlreadyUsedBy").Distinct();
        var missing = needed.Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"{file} is missing: {string.Join(", ", missing)}");
    }

    /// <summary>Overrides persist as KeyGesture strings. Every default must survive
    /// ToString → Parse, or a rebind back to a neighbour's default (Ctrl+, / ⌘[ / Ctrl+1)
    /// would load as garbage and be dropped.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryDefault_RoundTripsThroughItsString(bool isMac)
    {
        foreach (var d in ShortcutDefaults.All)
        {
            var g = ShortcutDefaults.For(d.Action, isMac);
            Assert.Equal(g, KeyGesture.Parse(g.ToString()));
        }
    }
}
