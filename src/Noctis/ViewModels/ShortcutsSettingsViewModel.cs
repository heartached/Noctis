using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One section of the Shortcuts tab (Playback, Window, …).</summary>
public sealed record ShortcutGroup(string Name, IReadOnlyList<ShortcutRowViewModel> Rows);

/// <summary>
/// Turns a gesture into the key caps the chip draws: <c>Ctrl+Right</c> → ["Ctrl", "→"].
/// macOS gets the symbol set its users read natively (⌘ ⌥ ⇧ ⌃).
/// </summary>
public static class ShortcutKeyFormatter
{
    public static IReadOnlyList<string> Parts(KeyGesture gesture, bool isMac)
    {
        var parts = new List<string>(4);
        var m = gesture.KeyModifiers;
        if (m.HasFlag(KeyModifiers.Control)) parts.Add(isMac ? "⌃" : "Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add(isMac ? "⌥" : "Alt");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add(isMac ? "⇧" : "Shift");
        if (m.HasFlag(KeyModifiers.Meta)) parts.Add(isMac ? "⌘" : "Win");
        parts.Add(KeyName(gesture.Key));
        return parts;
    }

    public static string KeyName(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Space => "Space",
        Key.Escape => "Esc",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Tab => "Tab",
        Key.Delete => "Del",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemBackslash or Key.OemPipe => "\\",
        Key.OemTilde => "`",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        _ => key.ToString(),
    };
}

/// <summary>
/// One row of the Shortcuts tab. Recording is a row-local state: click the chip, the
/// next chord is assigned. Escape cancels, Backspace unbinds, bare modifiers are
/// ignored, and a chord another action already owns is refused with a short message
/// while the row keeps recording.
/// </summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    private readonly ShortcutService _service;
    private readonly ShortcutsSettingsViewModel _owner;
    private readonly bool _isMac;
    private CancellationTokenSource? _conflictClearCts;

    public ShortcutAction Action { get; }
    public string Label { get; }
    public string Group { get; }
    public bool DeveloperOnly { get; }

    [ObservableProperty] private IReadOnlyList<string> _gestureParts = Array.Empty<string>();
    [ObservableProperty] private bool _isUnbound;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isDefault = true;
    [ObservableProperty] private string? _conflictMessage;
    [ObservableProperty] private bool _isVisible = true;

    public bool HasConflict => ConflictMessage is not null;

    internal ShortcutRowViewModel(ShortcutsSettingsViewModel owner, ShortcutService service, ShortcutDescriptor descriptor, bool isMac)
    {
        _owner = owner;
        _service = service;
        _isMac = isMac;
        Action = descriptor.Action;
        Label = descriptor.Label;
        Group = descriptor.Group;
        DeveloperOnly = descriptor.DeveloperOnly;
        Refresh();
    }

    partial void OnConflictMessageChanged(string? value) => OnPropertyChanged(nameof(HasConflict));

    internal void Refresh()
    {
        var gesture = _service.Get(Action);
        IsUnbound = gesture is null;
        GestureParts = gesture is null ? new[] { "Not set" } : ShortcutKeyFormatter.Parts(gesture, _isMac);
        IsDefault = _service.IsDefault(Action);
    }

    [RelayCommand]
    private void BeginRecord()
    {
        _owner.StopRecordingExcept(this);
        ConflictMessage = null;
        IsRecording = true;
    }

    [RelayCommand]
    private void CancelRecord()
    {
        IsRecording = false;
        ConflictMessage = null;
    }

    [RelayCommand]
    private void Reset()
    {
        CancelRecord();
        _service.Reset(Action);
    }

    /// <summary>
    /// Feed a key press while recording. Returns true when the press was consumed
    /// (always, while recording — the chip must never let a key leak to the page).
    /// </summary>
    public bool TryAssign(Key key, KeyModifiers modifiers)
    {
        if (!IsRecording) return false;

        switch (key)
        {
            case Key.Escape:
                CancelRecord();
                return true;
            case Key.Back when modifiers == KeyModifiers.None:
                IsRecording = false;
                ConflictMessage = null;
                _service.Set(Action, null);
                return true;
        }

        var gesture = new KeyGesture(key, modifiers);
        if (!ShortcutDefaults.IsValid(gesture))
            return true; // a bare modifier: wait for the real key

        try
        {
            _service.Set(Action, gesture);
        }
        catch (ShortcutConflictException ex)
        {
            ShowConflict(ex.Other);
            return true;
        }

        IsRecording = false;
        ConflictMessage = null;
        return true;
    }

    private void ShowConflict(ShortcutAction other)
    {
        var label = ShortcutDefaults.All.First(d => d.Action == other).Label;
        ConflictMessage = $"Already used by {label}";

        _conflictClearCts?.Cancel();
        var cts = _conflictClearCts = new CancellationTokenSource();
        _ = ClearConflictLaterAsync(cts.Token);
    }

    private async Task ClearConflictLaterAsync(CancellationToken ct)
    {
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested) ConflictMessage = null;
    }
}

/// <summary>
/// The Shortcuts tab: every rebindable action grouped for display, backed by the
/// shared <see cref="ShortcutService"/>. Developer-only rows hide until Developer Mode.
/// </summary>
public sealed partial class ShortcutsSettingsViewModel : ObservableObject
{
    private readonly ShortcutService _service;
    private readonly Func<bool> _developerMode;

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();
    public IReadOnlyList<ShortcutGroup> Groups { get; }

    public ShortcutsSettingsViewModel(ShortcutService service, Func<bool> developerMode, bool? isMac = null)
    {
        _service = service;
        _developerMode = developerMode;
        var mac = isMac ?? OperatingSystem.IsMacOS();

        foreach (var d in ShortcutDefaults.All)
            Rows.Add(new ShortcutRowViewModel(this, service, d, mac));

        Groups = Rows
            .GroupBy(r => r.Group)
            .Select(g => new ShortcutGroup(g.Key, g.ToList()))
            .ToList();

        RefreshVisibility();
        _service.Changed += (_, _) =>
        {
            foreach (var row in Rows) row.Refresh();
        };
    }

    /// <summary>True while any row is waiting for a chord (the page ignores Escape then).</summary>
    public bool IsRecording => Rows.Any(r => r.IsRecording);

    internal void StopRecordingExcept(ShortcutRowViewModel keep)
    {
        foreach (var row in Rows)
            if (!ReferenceEquals(row, keep) && row.IsRecording) row.CancelRecordCommand.Execute(null);
    }

    public void RefreshVisibility()
    {
        var dev = _developerMode();
        foreach (var row in Rows)
            row.IsVisible = !row.DeveloperOnly || dev;
        OnPropertyChanged(nameof(VisibleGroups));
    }

    /// <summary>Groups that still have at least one visible row (hides "Developer" outside Developer Mode).</summary>
    public IReadOnlyList<ShortcutGroup> VisibleGroups => Groups.Where(g => g.Rows.Any(r => r.IsVisible)).ToList();

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var row in Rows)
            if (row.IsRecording) row.CancelRecordCommand.Execute(null);
        _service.ResetAll();
    }
}
