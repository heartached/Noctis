using System;
using System.Collections.Generic;
using Avalonia.Input;

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
}

/// <summary>Display metadata for one rebindable action.</summary>
public sealed record ShortcutDescriptor(ShortcutAction Action, string Label, string Group);

public static class ShortcutDefaults
{
    public const string GroupPlayback = "Playback";
    public const string GroupWindow = "Window";
    public const string GroupNavigation = "Navigation";

    /// <summary>All rebindable actions in display order.</summary>
    public static IReadOnlyList<ShortcutDescriptor> All { get; } = new[]
    {
        new ShortcutDescriptor(ShortcutAction.PlayPause, "Play / Pause", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.NextTrack, "Next track", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.PreviousTrack, "Previous track", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.VolumeUp, "Volume up", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.VolumeDown, "Volume down", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleFavorite, "Favorite current track", GroupPlayback),
        new ShortcutDescriptor(ShortcutAction.ToggleFullscreen, "Toggle fullscreen", GroupWindow),
        new ShortcutDescriptor(ShortcutAction.ToggleQueue, "Show / hide queue", GroupWindow),
        new ShortcutDescriptor(ShortcutAction.SearchLibrary, "Search library", GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.CommandPalette, "Command palette", GroupNavigation),
        new ShortcutDescriptor(ShortcutAction.NewPlaylist, "New playlist", GroupNavigation),
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
            ShortcutAction.ToggleFullscreen => new KeyGesture(Key.F11),
            ShortcutAction.SearchLibrary => new KeyGesture(Key.F, primary),
            ShortcutAction.CommandPalette => new KeyGesture(Key.K, primary),
            ShortcutAction.NewPlaylist => new KeyGesture(Key.N, primary),
            // U for Up Next. Not Ctrl/⌘+Q (quit on macOS / Linux desktops) and not a bare
            // letter; Ctrl+U is no TextBox editing key, so it works from the search box too.
            ShortcutAction.ToggleQueue => new KeyGesture(Key.U, primary),
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
