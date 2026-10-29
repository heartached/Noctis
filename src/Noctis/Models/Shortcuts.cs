using System;
using System.Collections.Generic;
using Avalonia.Input;
using Noctis.Localization;

namespace Noctis.Models;

/// <summary>
/// Every keyboard shortcut the user can rebind from Settings › Shortcuts. These are the
/// window-level keys only; per-page selection keys (Ctrl+A / Escape), dialog keys and OS
/// media keys are fixed and never appear here.
/// </summary>
public enum ShortcutAction
{
    PlayPause,
    NextTrack,
    PreviousTrack,
    VolumeUp,
    VolumeDown,
    ToggleFavorite,
    ToggleFullscreen,
    SearchLibrary,
    CommandPalette,
    NewPlaylist,
    ToggleQueue,
    ToggleLyrics,
    // GitHub #131. Overrides are stored by name (AppSettings.Shortcuts), so renaming a
    // member drops the user's binding for it.
    OpenSettings,
    ScanReplayGain,
    ToggleShuffle,
    CycleRepeat,
    ToggleMute,
    SeekForward,
    SeekBackward,
    ToggleMiniPlayer,
    GoBack,
    GoForward,
    GoHome,
    GoSongs,
    GoAlbums,
    GoArtists,
}

/// <summary>
/// Display metadata for one rebindable action. <see cref="Group"/> is a stable id; the
/// label and group name the Shortcuts tab shows are localized (Strings.resx
/// <c>Shortcuts.Action.*</c> / <c>Shortcuts.Group.*</c>) and read at display time, so a
/// live language switch relabels the tab.
/// </summary>
public sealed record ShortcutDescriptor(ShortcutAction Action, string Group)
{
    public string LabelKey => "Shortcuts.Action." + Action;
    public string GroupKey => ShortcutDefaults.GroupKey(Group);
    public string Label => Loc.T(LabelKey);
}

public static class ShortcutDefaults
{
    public const string GroupPlayback = "Playback";
    public const string GroupWindow = "Window";
    public const string GroupNavigation = "Navigation";
    public const string GroupLibrary = "Library";

    /// <summary>Strings.resx key of a group's title.</summary>
    public static string GroupKey(string group) => "Shortcuts.Group." + group;

    /// <summary>All rebindable actions in display order.</summary>
    public static IReadOnlyList<ShortcutDescriptor> All { get; } = new[]
    {
        new ShortcutDescriptor(ShortcutAction.PlayPause, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.NextTrack, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.PreviousTrack, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.SeekForward, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.SeekBackward, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.VolumeUp, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.VolumeDown, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleMute, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleShuffle, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.CycleRepeat, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleFavorite, GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleFullscreen, GroupWindow),
        new ShortcutDescriptor(ShortcutAction.ToggleQueue, GroupWindow),
        new ShortcutDescriptor(ShortcutAction.ToggleLyrics, GroupWindow),
        new ShortcutDescriptor(ShortcutAction.ToggleMiniPlayer, GroupWindow),
        new ShortcutDescriptor(ShortcutAction.OpenSettings, GroupWindow),
        new ShortcutDescriptor(ShortcutAction.SearchLibrary, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.CommandPalette, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoBack, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoForward, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoHome, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoSongs, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoAlbums, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.GoArtists, GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.NewPlaylist, GroupLibrary),
        new ShortcutDescriptor(ShortcutAction.ScanReplayGain, GroupLibrary),
    };

    /// <summary>
    /// Default gesture per platform. macOS uses ⌘ where Windows/Linux use Ctrl, mirroring
    /// the native menu gestures (⌘→ / ⌘← like Music.app). Desktops that swallow F11 can
    /// rebind fullscreen to any free key from Settings › Shortcuts.
    /// </summary>
    public static KeyGesture For(ShortcutAction action, bool isMac)
    {
        var primary = isMac ? KeyModifiers.Meta : KeyModifiers.Control;
        return action switch
        {
            ShortcutAction.PlayPause => new KeyGesture(Key.Space),
            ShortcutAction.NextTrack => new KeyGesture(Key.Right, primary),
            ShortcutAction.PreviousTrack => new KeyGesture(Key.Left, primary),
            ShortcutAction.VolumeUp => new KeyGesture(Key.Up, primary),
            ShortcutAction.VolumeDown => new KeyGesture(Key.Down, primary),
            ShortcutAction.ToggleFavorite => new KeyGesture(Key.L, primary),
            // A Mac keyboard's F11 is volume up (fn+F11 is Show Desktop), so the key never
            // reaches the app there; ⌃⌘F is the macOS Enter Full Screen key.
            ShortcutAction.ToggleFullscreen => isMac ? new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control) : new KeyGesture(Key.F11),
            ShortcutAction.SearchLibrary => new KeyGesture(Key.F, primary),
            ShortcutAction.CommandPalette => new KeyGesture(Key.K, primary),
            ShortcutAction.NewPlaylist => new KeyGesture(Key.N, primary),
            // U for Up Next. Not Ctrl/⌘+Q (quit on macOS / Linux desktops) and not a bare
            // letter; Ctrl+U is no TextBox editing key, so it works from the search box too.
            ShortcutAction.ToggleQueue => new KeyGesture(Key.U, primary),
            // GitHub #103. Ctrl/⌘+L is Favorite, so the Shift variant of the same letter.
            ShortcutAction.ToggleLyrics => new KeyGesture(Key.L, primary | KeyModifiers.Shift),
            // GitHub #131. Ctrl/⌘+, is the Preferences key on every platform (macOS app
            // menu item, VS Code, browsers); the macOS native menu item follows this binding.
            ShortcutAction.OpenSettings => new KeyGesture(Key.OemComma, primary),
            // R for ReplayGain; plain Ctrl/⌘+R is Repeat, so the Shift variant.
            ShortcutAction.ScanReplayGain => new KeyGesture(Key.R, primary | KeyModifiers.Shift),
            // Spotify's keys: Ctrl/⌘+S shuffle, Ctrl/⌘+R repeat, Shift+←/→ seek,
            // Ctrl/⌘+Shift+↓ mute (next to Ctrl/⌘+↑/↓ volume).
            ShortcutAction.ToggleShuffle => new KeyGesture(Key.S, primary),
            ShortcutAction.CycleRepeat => new KeyGesture(Key.R, primary),
            ShortcutAction.ToggleMute => new KeyGesture(Key.Down, primary | KeyModifiers.Shift),
            ShortcutAction.SeekForward => new KeyGesture(Key.Right, KeyModifiers.Shift),
            ShortcutAction.SeekBackward => new KeyGesture(Key.Left, KeyModifiers.Shift),
            ShortcutAction.ToggleMiniPlayer => new KeyGesture(Key.M, primary | KeyModifiers.Shift),
            // Browser history keys: Alt+←/→ on Windows/Linux, ⌘[ / ⌘] on macOS (Safari, Finder).
            ShortcutAction.GoBack => isMac ? new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta) : new KeyGesture(Key.Left, KeyModifiers.Alt),
            ShortcutAction.GoForward => isMac ? new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta) : new KeyGesture(Key.Right, KeyModifiers.Alt),
            // Sidebar order: Ctrl/⌘+1…4 for Home, Songs, Albums, Artists.
            ShortcutAction.GoHome => new KeyGesture(Key.D1, primary),
            ShortcutAction.GoSongs => new KeyGesture(Key.D2, primary),
            ShortcutAction.GoAlbums => new KeyGesture(Key.D3, primary),
            ShortcutAction.GoArtists => new KeyGesture(Key.D4, primary),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    /// <summary>
    /// A gesture needs a real key: <see cref="Key.None"/> and bare modifier keys
    /// (Shift, Ctrl, Alt, Win/⌘ pressed on their own) are not bindable.
    /// </summary>
    public static bool IsValid(KeyGesture gesture) => gesture.Key switch
    {
        Key.None => false,
        Key.LeftShift or Key.RightShift => false,
        Key.LeftCtrl or Key.RightCtrl => false,
        Key.LeftAlt or Key.RightAlt => false,
        Key.LWin or Key.RWin => false,
        _ => true,
    };

    /// <summary>
    /// True when a focused edit box needs this key itself, so a window-level shortcut must
    /// let it through: an unmodified key types, and the caret / delete keys keep their
    /// meaning under any modifier (Ctrl+←/→ jumps words, Ctrl+Backspace deletes one,
    /// ⌘←/→ goes to line start/end on macOS) even though Previous / Next use them.
    /// </summary>
    public static bool IsTextBoxKey(Key key, KeyModifiers modifiers) =>
        modifiers == KeyModifiers.None
        || key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.Back or Key.Delete;
}
