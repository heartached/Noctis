using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Services.Plugins;

namespace Noctis.ViewModels;

/// <summary>
/// Content packs in Settings: pack themes in Appearance, lyrics presets in Lyrics, pack languages
/// in the language picker. The plugin host's <see cref="ContentCatalog"/> is the source; when a
/// pack goes away (switched off, removed) whatever it provided falls back to the default.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Themes of the switched-on content packs (Appearance → Themes).</summary>
    public ObservableCollection<PackThemeTile> PackThemes { get; } = new();
    public bool HasPackThemes => PackThemes.Count > 0;

    /// <summary>"&lt;pack id&gt;/&lt;theme id&gt;" of the active pack theme, or null.</summary>
    [ObservableProperty] private string? _activePackThemeKey;

    /// <summary>Lyrics-page presets of the switched-on content packs (Lyrics tab).</summary>
    public ObservableCollection<LyricsPresetItem> LyricsPresets { get; } = new();
    public bool HasLyricsPresets => LyricsPresets.Count > 0;

    /// <summary>Transient line under the presets ("Applied Karaoke").</summary>
    [ObservableProperty] private string _lyricsPresetStatus = string.Empty;

    private ContentCatalog? PackContent => Plugins?.Content;

    [RelayCommand]
    private void ApplyPackTheme(string? key)
    {
        if (key is null || PackContent?.FindTheme(key) is not { } theme) return;

        ActiveCustomThemeId = null;
        foreach (var t in CustomThemes) t.IsActive = false;
        ActivePackThemeKey = theme.Key;
        foreach (var t in PackThemes) t.IsActive = string.Equals(t.Key, theme.Key, StringComparison.OrdinalIgnoreCase);
        SetActiveThemeFlags("__Custom"); // clears every built-in flag

        // A pack theme may name its accent; it seeds the accent picker like a custom theme does.
        if (theme.Accent is { } accent) ApplyAccent(accent, "Custom");
        ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
        if (_settingsLoaded) _ = SaveAsync();
    }

    [RelayCommand]
    private void ApplyLyricsPreset(string? key)
    {
        if (key is null || PackContent?.FindLyricsPreset(key) is not { } preset) return;
        ApplyLyricsPresetValues(preset.Values);
        TransientStatus.Show(nameof(LyricsPresetStatus), v => LyricsPresetStatus = v,
            Loc.T("Settings.LyricsPresets.Applied", preset.Name), TimeSpan.FromSeconds(4));
    }

    /// <summary>Sets the lyrics display settings a preset names (others stay), then applies and saves once.</summary>
    internal void ApplyLyricsPresetValues(LyricsPresetValues v)
    {
        var wasSuspended = _suspendSettingPersistence;
        _suspendSettingPersistence = true; // one save at the end, not one per property
        try
        {
            if (v.MinLineOpacity is { } opacity) LyricsMinLineOpacity = opacity;
            if (v.FullScreenFocus is { } focus) LyricsFullScreenFocusEnabled = focus;
            if (v.JoinSplitWords is { } join) LyricsJoinSplitWords = join;
            if (v.ShowTranslations is { } translations) LyricsShowTranslations = translations;
            if (v.ShowRomanization is { } romanization) LyricsShowRomanization = romanization;
            if (v.ShowBackgroundVocals is { } vocals) LyricsShowBackgroundVocals = vocals;
            if (v.TitleMarquee is { } titleMarquee) LyricsTitleMarqueeEnabled = titleMarquee;
            if (v.ArtistMarquee is { } artistMarquee) LyricsArtistMarqueeEnabled = artistMarquee;
            if (v.FlowingBackground is { } flowing)
            {
                if (flowing == LyricsPresetValues.FlowingOff) LyricsFlowingLightEnabled = false;
                else
                {
                    LyricsFlowingStyle = flowing;
                    LyricsFlowingLightEnabled = true;
                }
            }
            if (v.KawarpWarp is { } warp) LyricsKawarpWarp = warp;
            if (v.KawarpBlur is { } blur) LyricsKawarpBlur = blur;
            if (v.Visualizer is { } viz) LyricsVisualizerEnabled = viz;
            if (v.VisualizerStyle is { } style) LyricsVisualizerStyle = style;
            if (v.VisualizerArtworkColor is { } artColor) LyricsVisualizerArtworkColor = artColor;
        }
        finally { _suspendSettingPersistence = wasSuspended; }
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    private void OnPluginContentChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshPackContent();
        else Dispatcher.UIThread.Post(RefreshPackContent);
    }

    /// <summary>Re-reads the catalog: rebuilds the tiles, presets and language list, and falls
    /// back from a pack theme or pack language that is no longer available.</summary>
    internal void RefreshPackContent()
    {
        RefreshPackItems();

        if (ActivePackThemeKey is { } key)
        {
            if (PackContent?.FindTheme(key) is null)
            {
                // The pack was switched off or removed: back to the default theme and accent.
                ActivePackThemeKey = null;
                SetActiveThemeFlags("Gray");
                ApplyAccent("#E74856", "Crimson");
                ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
                if (_settingsLoaded) _ = SaveAsync();
            }
            else
            {
                // Same theme, maybe new colours (the pack was updated).
                ThemeChanged?.Invoke(this, ResolveActiveThemeKey());
            }
        }

        // Before the first load finishes the stored language is applied by LoadAsync itself.
        if (!_settingsLoaded)
        {
            LanguageOptions = BuildLanguageOptions(PackContent);
            return;
        }
        var code = LanguageChoice?.Code ?? string.Empty;
        var options = BuildLanguageOptions(PackContent);
        var match = options.FirstOrDefault(o => o.Code == code);
        _relabelingLanguages = true;
        try
        {
            LanguageOptions = options;
            LanguageChoice = match ?? options[0];
        }
        finally { _relabelingLanguages = false; }
        if (match is null)
        {
            // A pack language went away: follow the system language again (English fallback).
            _settings.Language = Loc.SystemLanguage;
            Loc.Instance.SetCulture(Loc.SystemLanguage);
            RelabelCachedStrings(Loc.SystemLanguage);
            _ = SaveAsync();
        }
    }

    /// <summary>Tiles and preset rows from the catalog (also after a language switch, for "by …").</summary>
    private void RefreshPackItems()
    {
        PackThemes.Clear();
        foreach (var theme in PackContent?.Themes ?? Array.Empty<PackTheme>())
            PackThemes.Add(new PackThemeTile(theme)
            {
                IsActive = string.Equals(theme.Key, ActivePackThemeKey, StringComparison.OrdinalIgnoreCase),
            });
        OnPropertyChanged(nameof(HasPackThemes));

        LyricsPresets.Clear();
        foreach (var preset in PackContent?.LyricsPresets ?? Array.Empty<PackLyricsPreset>())
            LyricsPresets.Add(new LyricsPresetItem(preset));
        OnPropertyChanged(nameof(HasLyricsPresets));
    }

    /// <summary>LoadAsync: a stored "Pack:…" theme is kept only when an enabled pack provides it.</summary>
    private bool RestorePackTheme(string storedTheme)
    {
        if (!storedTheme.StartsWith(App.PackThemePrefix, StringComparison.Ordinal)) return false;
        var key = storedTheme.Substring(App.PackThemePrefix.Length);
        if (PackContent?.FindTheme(key) is { } theme)
        {
            ActivePackThemeKey = theme.Key;
            SetActiveThemeFlags("__Custom");
        }
        else
        {
            ActivePackThemeKey = null;
            SetActiveThemeFlags("Gray");
            _settings.Theme = "Gray";
        }
        RefreshPackItems();
        return true;
    }
}
