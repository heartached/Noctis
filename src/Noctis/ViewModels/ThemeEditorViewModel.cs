using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class ThemeEditorViewModel : ObservableObject
{
    private readonly System.Collections.Generic.IReadOnlyCollection<string> _existingNames;
    private readonly bool _isEdit;

    public ThemeEditorViewModel(
        CustomThemeDefinition? existing,
        IEnumerable<string> existingNamesExcludingThis)
    {
        _existingNames = new HashSet<string>(existingNamesExcludingThis, StringComparer.OrdinalIgnoreCase);
        _isEdit = existing != null;

        var def = existing ?? new CustomThemeDefinition();
        Id = def.Id;
        Name = def.Name;
        IsDarkMode = def.BaseMode != "Light";
        MainHex = def.MainBackgroundHex;
        SidebarHex = def.SidebarBackgroundHex;
        AccentHex = def.AccentHex;
        RebuildPreview();
    }

    public string Id { get; }
    public bool IsEdit => _isEdit;
    public string DialogTitle => _isEdit ? "Edit Custom Theme" : "New Custom Theme";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private string _mainHex = "#121212";
    [ObservableProperty] private string _sidebarHex = "#1C1C1C";
    [ObservableProperty] private string _accentHex = "#E74856";

    // ── Preview brushes consumed by the dialog ──
    [ObservableProperty] private IBrush? _previewMain;
    [ObservableProperty] private IBrush? _previewSidebar;
    [ObservableProperty] private IBrush? _previewAccent;
    [ObservableProperty] private IBrush? _previewPrimaryText;
    [ObservableProperty] private IBrush? _previewSecondaryText;
    [ObservableProperty] private IBrush? _previewIslandBg;
    [ObservableProperty] private IBrush? _previewIslandFg;
    [ObservableProperty] private IBrush? _previewIslandFgSecondary;
    [ObservableProperty] private IBrush? _previewSidebarHover;
    [ObservableProperty] private IBrush? _previewOutline;
    [ObservableProperty] private IBrush? _previewStripe;
    [ObservableProperty] private IBrush? _previewAccentFg;

    // ── Validation ──
    public bool HasInvalidName => string.IsNullOrWhiteSpace(Name);
    public bool HasDuplicateName => !HasInvalidName && _existingNames.Contains(Name.Trim());
    public bool CanSave => !HasInvalidName && !HasDuplicateName;

    public event Action<CustomThemeDefinition>? Saved;
    public event Action? Cancelled;

    partial void OnNameChanged(string value)        { OnPropertyChanged(nameof(HasInvalidName)); OnPropertyChanged(nameof(HasDuplicateName)); OnPropertyChanged(nameof(CanSave)); SaveCommand.NotifyCanExecuteChanged(); }
    partial void OnIsDarkModeChanged(bool value)    => RebuildPreview();
    partial void OnMainHexChanged(string value)     => RebuildPreview();
    partial void OnSidebarHexChanged(string value)  => RebuildPreview();
    partial void OnAccentHexChanged(string value)   => RebuildPreview();

    private void RebuildPreview()
    {
        var def = ToDefinition();
        var dict = ThemeDerivation.Derive(def);
        PreviewMain               = (IBrush)dict["AppMainBackground"];
        PreviewSidebar            = (IBrush)dict["AppSidebarBackground"];
        PreviewAccent             = (IBrush)dict["AccentColorBrush"];
        PreviewPrimaryText        = (IBrush)dict["PrimaryTextBrush"];
        PreviewSecondaryText     = (IBrush)dict["SecondaryTextBrush"];
        PreviewIslandBg           = (IBrush)dict["IslandBackground"];
        PreviewIslandFg           = (IBrush)dict["IslandForeground"];
        PreviewIslandFgSecondary  = (IBrush)dict["IslandForegroundSecondary"];
        PreviewSidebarHover       = (IBrush)dict["SidebarHoverBrush"];
        PreviewStripe             = (IBrush)dict["TrackListStripeBrush"];
        PreviewAccentFg           = (IBrush)dict["AccentForegroundBrush"];
        PreviewOutline            = new SolidColorBrush(Color.Parse(IsDarkMode ? "#1FFFFFFF" : "#26000000"));
    }

    private CustomThemeDefinition ToDefinition() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        BaseMode = IsDarkMode ? "Dark" : "Light",
        MainBackgroundHex = MainHex,
        SidebarBackgroundHex = SidebarHex,
        AccentHex = AccentHex,
    };

    /// <summary>Sets the base mode absolutely ("Dark"/"Light"), so clicking the
    /// already-active pill is a no-op instead of a ToggleButton un-check flip.</summary>
    [RelayCommand]
    private void SetBaseMode(string mode)
        => IsDarkMode = string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Saved?.Invoke(ToDefinition());

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
