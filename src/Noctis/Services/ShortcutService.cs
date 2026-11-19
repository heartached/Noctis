using System;
using System.Collections.Generic;
using Avalonia.Input;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>Thrown by <see cref="ShortcutService.Set"/> when the gesture already belongs to another action.</summary>
public sealed class ShortcutConflictException : Exception
{
    public ShortcutAction Other { get; }

    public ShortcutConflictException(ShortcutAction other)
        : base($"Gesture is already bound to {other}.")
    {
        Other = other;
    }
}

/// <summary>
/// Owns the action → gesture map for every rebindable shortcut. Defaults come from
/// <see cref="ShortcutDefaults"/>; user overrides live in <see cref="AppSettings.Shortcuts"/>
/// as gesture strings. Matching is exact on both key and modifiers, so a rebound
/// <c>F11</c> does not also fire on <c>Ctrl+F11</c>.
/// </summary>
public sealed class ShortcutService
{
    private readonly bool _isMac;
    // Present key = user override. A null value means "deliberately unbound".
    private readonly Dictionary<ShortcutAction, KeyGesture?> _overrides = new();
    // Actions whose feature is switched off: hidden, unmatched, chord free; override kept.
    private readonly HashSet<ShortcutAction> _disabled = new();

    public event EventHandler? Changed;

    public ShortcutService(bool? isMac = null)
    {
        _isMac = isMac ?? OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// Effective gesture, or null when the user unbound the action. A default that a user
    /// override on another action already claims is shadowed (null): saved settings from
    /// before a new default existed (Ctrl+Shift+L bound to Play/Pause, then GitHub #103
    /// gave it to Lyrics) would otherwise put two actions on one key, with the first in
    /// display order winning and the tab showing the same chord twice. The user's explicit
    /// choice wins; freeing the key brings the default back.
    /// </summary>
    public KeyGesture? Get(ShortcutAction action)
    {
        if (_disabled.Contains(action)) return null;
        if (_overrides.TryGetValue(action, out var g)) return g;
        var fallback = ShortcutDefaults.For(action, _isMac);
        foreach (var (other, gesture) in _overrides)
            if (other != action && !_disabled.Contains(other) && fallback.Equals(gesture)) return null;
        return fallback;
    }

    public KeyGesture Default(ShortcutAction action) => ShortcutDefaults.For(action, _isMac);

    public bool IsDefault(ShortcutAction action) => !_overrides.ContainsKey(action);

    /// <summary>The other action that already owns <paramref name="gesture"/>, if any.</summary>
    public ShortcutAction? FindConflict(KeyGesture gesture, ShortcutAction self)
    {
        foreach (var d in ShortcutDefaults.All)
        {
            if (d.Action == self) continue;
            if (Get(d.Action) is { } g && g.Equals(gesture)) return d.Action;
        }
        return null;
    }

    /// <summary>
    /// Bind <paramref name="action"/> to <paramref name="gesture"/> (null unbinds it).
    /// Rejects modifier-only gestures and gestures owned by another action — a clash is
    /// never stored silently.
    /// </summary>
    public void Set(ShortcutAction action, KeyGesture? gesture)
    {
        if (gesture is not null)
        {
            if (!ShortcutDefaults.IsValid(gesture))
                throw new ArgumentException("A shortcut needs a non-modifier key.", nameof(gesture));
            if (FindConflict(gesture, action) is { } other)
                throw new ShortcutConflictException(other);
        }

        var current = Get(action);
        if (Equals(current, gesture)) return;

        if (gesture is not null && gesture.Equals(Default(action)))
            _overrides.Remove(action);
        else
            _overrides[action] = gesture;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(ShortcutAction action)
    {
        if (_overrides.Remove(action))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True while the action's feature is switched off (see <see cref="SetDisabled"/>).</summary>
    public bool IsDisabled(ShortcutAction action) => _disabled.Contains(action);

    /// <summary>
    /// The actions whose feature is switched off (Settings › Plugins › Built-in Features),
    /// supplied by the settings layer. A disabled action has no effective gesture: it is
    /// never matched (its key falls through) and its chord is free for another action. The
    /// stored binding is kept and returns when the action comes back, unless another action
    /// took that chord meanwhile: then the other action keeps it and the returning one falls
    /// back to its default, or to unbound when that is taken too.
    /// </summary>
    public void SetDisabled(IEnumerable<ShortcutAction> actions)
    {
        var next = new HashSet<ShortcutAction>(actions);
        if (next.SetEquals(_disabled)) return;

        foreach (var action in _disabled)
        {
            if (next.Contains(action)) continue;
            // Checked while it is still disabled, so Get on the others ignores it.
            if (_overrides.TryGetValue(action, out var stored) && stored is not null
                && FindConflict(stored, action) is not null)
                _overrides.Remove(action); // back to the default; Get shadows it if that is taken
        }

        _disabled.Clear();
        _disabled.UnionWith(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resets what Settings › Shortcuts shows: a disabled action's binding is kept.</summary>
    public void ResetAll()
    {
        var removed = false;
        foreach (var action in new List<ShortcutAction>(_overrides.Keys))
            if (!_disabled.Contains(action)) removed |= _overrides.Remove(action);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The action whose gesture exactly matches this key event, if any.</summary>
    public ShortcutAction? TryMatch(KeyEventArgs e)
    {
        foreach (var d in ShortcutDefaults.All)
        {
            if (Get(d.Action) is { } g && g.Key == e.Key && g.KeyModifiers == e.KeyModifiers)
                return d.Action;
        }
        return null;
    }

    /// <summary>Replace the override set with what <paramref name="settings"/> holds.
    /// Unknown actions and unparsable gestures are ignored rather than failing the load.</summary>
    public void Load(AppSettings settings)
    {
        _overrides.Clear();
        if (settings.Shortcuts is { } stored)
        {
            foreach (var (name, text) in stored)
            {
                if (!Enum.TryParse<ShortcutAction>(name, ignoreCase: false, out var action)) continue;
                if (string.IsNullOrEmpty(text))
                {
                    _overrides[action] = null;
                    continue;
                }
                KeyGesture gesture;
                try { gesture = KeyGesture.Parse(text); }
                catch { continue; }
                if (!ShortcutDefaults.IsValid(gesture)) continue;
                if (gesture.Equals(Default(action))) continue;
                _overrides[action] = gesture;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Write only the overrides; a fully-default map leaves the setting null.</summary>
    public void SaveTo(AppSettings settings)
    {
        if (_overrides.Count == 0)
        {
            settings.Shortcuts = null;
            return;
        }

        var map = new Dictionary<string, string>();
        foreach (var (action, gesture) in _overrides)
            map[action.ToString()] = gesture?.ToString() ?? string.Empty;
        settings.Shortcuts = map;
    }
}
