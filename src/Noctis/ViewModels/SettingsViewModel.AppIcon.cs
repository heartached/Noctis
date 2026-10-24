using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One tile of Settings › Appearance › App Icon.</summary>
public partial class AppIconOption : ObservableObject
{
    public AppIconOption(string id)
    {
        Id = id;
        // Live re-label on a language switch, like {loc:T} does for static text.
        Loc.Instance.CultureChanged += (_, _) => OnPropertyChanged(nameof(Name));
    }

    public string Id { get; }

    /// <summary>"Settings.AppIcon.Blush", ...: localized tile caption.</summary>
    public string NameKey => "Settings.AppIcon." + char.ToUpperInvariant(Id[0]) + Id[1..];

    public string Name => Loc.T(NameKey);

    [ObservableProperty] private bool _isSelected;

    private Bitmap? _preview;
    private bool _previewTried;

    /// <summary>The real rendered icon (Assets/AppIcons/&lt;id&gt;.png), loaded on first use.</summary>
    public Bitmap? Preview
    {
        get
        {
            if (_previewTried) return _preview;
            _previewTried = true;
            try
            {
                using var s = AssetLoader.Open(new Uri(AppIconService.PreviewUri(Id)));
                _preview = new Bitmap(s);
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.UI, "AppIcon.Preview", $"{Id}: {ex.Message}");
            }
            return _preview;
        }
    }
}

public partial class SettingsViewModel
{
    public IReadOnlyList<AppIconOption> AppIconOptions { get; } =
        AppIconService.Ids.Select(id => new AppIconOption(id) { IsSelected = id == AppIconService.DefaultId }).ToList();

    /// <summary>Chosen app icon id (window, taskbar, tray). Persisted as <c>AppSettings.AppIcon</c>.</summary>
    [ObservableProperty] private string _appIcon = AppIconService.DefaultId;

    partial void OnAppIconChanged(string value)
    {
        var id = AppIconService.Normalize(value);
        if (id != value)
        {
            AppIcon = id; // re-enters with the normalised id
            return;
        }
        foreach (var o in AppIconOptions) o.IsSelected = o.Id == id;
        AppIconService.Apply(id);
        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private void SetAppIcon(string? id) => AppIcon = AppIconService.Normalize(id);
}
