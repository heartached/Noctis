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

    public event EventHandler? Changed;

    public ShortcutService(bool? isMac = null)
    {
        _isMac = isMac ?? OperatingSystem.IsMacOS();
    }

    /// <summary>Effective gesture, or null when the user unbound the action.</summary>
    public KeyGesture? Get(ShortcutAction action)
        => _overrides.TryGetValue(action, out var g) ? g : ShortcutDefaults.For(action, _isMac);

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

    public void ResetAll()
    {
        if (_overrides.Count == 0) return;
        _overrides.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
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
