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
