using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

/// <summary>
/// One reviewable metadata change in the "Search metadata" panel: a field, its current value,
/// the proposed value, and whether the user wants to apply it. <see cref="ApplyIfChecked"/> runs
/// the stored action (which writes the new value into the live edit field).
/// </summary>
public sealed partial class MetadataChangeRow : ObservableObject
{
    private readonly Action _applyAction;

    public string Field { get; }
    public string OldValue { get; }
    public string NewValue { get; }

    /// <summary>"old → new" display string for the row template.</summary>
    public string Display => $"{(string.IsNullOrEmpty(OldValue) ? "—" : OldValue)}  →  {NewValue}";

    [ObservableProperty] private bool _apply = true;

    private MetadataChangeRow(string field, string oldValue, string newValue, Action applyAction)
    {
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
        _applyAction = applyAction;
    }

    public void ApplyIfChecked()
    {
        if (Apply) _applyAction();
    }

    /// <summary>
    /// Builds a row only when <paramref name="newValue"/> is non-empty and differs (trimmed,
    /// ordinal) from <paramref name="oldValue"/>; otherwise returns null.
    /// </summary>
    public static MetadataChangeRow? TryCreate(string field, string? oldValue, string? newValue, Action applyAction)
    {
        var nv = newValue?.Trim() ?? string.Empty;
        if (nv.Length == 0) return null;
        var ov = oldValue?.Trim() ?? string.Empty;
        if (string.Equals(nv, ov, StringComparison.Ordinal)) return null;
        return new MetadataChangeRow(field, oldValue?.Trim() ?? string.Empty, nv, applyAction);
    }
}
