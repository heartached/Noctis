using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Filter chip entry for the release-type strip on the Albums view. Filter == null
/// means "All"; otherwise the chip filters to that <see cref="ReleaseType"/>.
/// </summary>
public sealed partial class ReleaseTypeChip : ObservableObject
{
    public ReleaseType? Filter { get; init; }
    public string Label { get; init; } = string.Empty;
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// Toggleable audio-quality chip for the Albums view ("Lossless" / "Hi-Res").
/// Clicking an active chip clears the filter.
/// </summary>
public sealed partial class QualityChip : ObservableObject
{
    /// <summary>Filter key: "lossless" or "hires".</summary>
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    [ObservableProperty] private bool _isActive;
}
