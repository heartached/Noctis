using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Services.Plugins;

namespace Noctis.ViewModels;

/// <summary>A content-pack theme in Settings → Appearance, next to the built-in and custom tiles.</summary>
public sealed partial class PackThemeTile : ObservableObject
{
    public PackThemeTile(PackTheme theme)
    {
        Key = theme.Key;
        Name = theme.Name;
        PackName = theme.PackName;
        MainHex = theme.MainHex;
        SidebarHex = theme.SidebarHex;
        AccentHex = theme.AccentHex;
    }

    public string Key { get; }
    public string Name { get; }
    public string PackName { get; }
    public string MainHex { get; }
    public string SidebarHex { get; }
    public string AccentHex { get; }
    /// <summary>"by &lt;pack&gt;" under the tile name.</summary>
    public string ByLabel => Loc.T("Plugins.ByPack", PackName);

    [ObservableProperty] private bool _isActive;
}

/// <summary>A lyrics-page style preset from a content pack (Settings → Lyrics).</summary>
public sealed class LyricsPresetItem
{
    public LyricsPresetItem(PackLyricsPreset preset)
    {
        Key = preset.Key;
        Name = preset.Name;
        PackName = preset.PackName;
    }

    public string Key { get; }
    public string Name { get; }
    public string PackName { get; }
    public string ByLabel => Loc.T("Plugins.ByPack", PackName);
}
