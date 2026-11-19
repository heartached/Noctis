using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One section of the Shortcuts tab (Playback, Window, …). <see cref="Id"/> is the
/// stable <see cref="ShortcutDefaults"/> group id; <see cref="Name"/> is its localized title.</summary>
public sealed class ShortcutGroup : ObservableObject
{
    public ShortcutGroup(string id, IReadOnlyList<ShortcutRowViewModel> rows)
    {
        Id = id;
        Rows = rows;
    }

    public string Id { get; }
    public string Name => Loc.T(ShortcutDefaults.GroupKey(Id));
    public IReadOnlyList<ShortcutRowViewModel> Rows { get; }

    internal void Relabel() => OnPropertyChanged(nameof(Name));
}

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

    private readonly ShortcutDescriptor _descriptor;

    public ShortcutAction Action { get; }
    public string Label => _descriptor.Label;
    public string Group { get; }

    [ObservableProperty] private IReadOnlyList<string> _gestureParts = Array.Empty<string>();
    [ObservableProperty] private bool _isUnbound;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isDefault = true;
    [ObservableProperty] private string? _conflictMessage;

    /// <summary>False while the action's feature is switched off (Built-in Features): the
    /// row is hidden, which also keeps Settings search from finding it.</summary>
    [ObservableProperty] private bool _isAvailable = true;

    public bool HasConflict => ConflictMessage is not null;

    internal ShortcutRowViewModel(ShortcutsSettingsViewModel owner, ShortcutService service, ShortcutDescriptor descriptor, bool isMac)
    {
        _owner = owner;
        _service = service;
        _isMac = isMac;
        _descriptor = descriptor;
        Action = descriptor.Action;
        Group = descriptor.Group;
        Refresh();
    }

    partial void OnConflictMessageChanged(string? value) => OnPropertyChanged(nameof(HasConflict));

    /// <summary>A live language switch: re-read the label and the "Not set" chip text.</summary>
    internal void Relabel()
    {
        OnPropertyChanged(nameof(Label));
        Refresh();
    }

    internal void Refresh()
    {
        IsAvailable = !_service.IsDisabled(Action);
        if (!IsAvailable && IsRecording) CancelRecord();
        var gesture = _service.Get(Action);
        IsUnbound = gesture is null;
        GestureParts = gesture is null ? new[] { Loc.T("Shortcuts.NotSet") } : ShortcutKeyFormatter.Parts(gesture, _isMac);
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
        ConflictMessage = Loc.T("Shortcuts.AlreadyUsedBy", label);

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
/// shared <see cref="ShortcutService"/>.
/// </summary>
public sealed partial class ShortcutsSettingsViewModel : ObservableObject
{
    private readonly ShortcutService _service;

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();
    public IReadOnlyList<ShortcutGroup> Groups { get; }

    public ShortcutsSettingsViewModel(ShortcutService service, bool? isMac = null)
    {
        _service = service;
        var mac = isMac ?? OperatingSystem.IsMacOS();

        foreach (var d in ShortcutDefaults.All)
            Rows.Add(new ShortcutRowViewModel(this, service, d, mac));

        Groups = Rows
            .GroupBy(r => r.Group)
            .Select(g => new ShortcutGroup(g.Key, g.ToList()))
            .ToList();

        _service.Changed += (_, _) =>
        {
            foreach (var row in Rows) row.Refresh();
        };
        Loc.Instance.CultureChanged += (_, _) =>
        {
            foreach (var group in Groups) group.Relabel();
            foreach (var row in Rows) row.Relabel();
        };
    }

    /// <summary>True while any row is waiting for a chord (the page ignores Escape then).</summary>
    public bool IsRecording => Rows.Any(r => r.IsRecording);

    internal void StopRecordingExcept(ShortcutRowViewModel keep)
    {
        foreach (var row in Rows)
            if (!ReferenceEquals(row, keep) && row.IsRecording) row.CancelRecordCommand.Execute(null);
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var row in Rows)
            if (row.IsRecording) row.CancelRecordCommand.Execute(null);
        _service.ResetAll();
    }
}
