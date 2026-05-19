using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

public sealed partial class CustomThemeTile : ObservableObject
{
    public string Id { get; init; } = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _accentHex = "#E74856";
    [ObservableProperty] private string _sidebarHex = "#1C1C1C";
    [ObservableProperty] private string _mainHex = "#121212";
    [ObservableProperty] private string _baseMode = "Dark";
    [ObservableProperty] private bool _isActive;
}
