using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

/// <summary>
/// One entry in the Settings accent-colour picker. The active flag drives the
/// selection ring shown in the swatch grid.
/// </summary>
public partial class AccentSwatch : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Hex { get; init; } = string.Empty;
    [ObservableProperty] private bool _isActive;
}
