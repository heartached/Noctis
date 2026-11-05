using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// Per-song and per-album lyrics background videos (user ask 2026-09-07). The default clip
/// lives in Settings › Appearance; a song or album can carry its own through the context
/// menus. Keys are stable across restarts: "track:{id}" / "album:{id}". The commands are
/// static so every view's menu gets them without wiring a command per view (same idea as
/// <see cref="SpectrogramLauncher"/>).
/// </summary>
public static class LyricsBackgroundOverrides
{
    public static string KeyForTrack(Track track) => "track:" + track.Id.ToString("N");
    public static string KeyForAlbum(Album album) => KeyForAlbumId(album.Id);
    public static string KeyForAlbumId(Guid albumId) => "album:" + albumId.ToString("N");

    public static ICommand ChooseForTrackCommand { get; } =
        new AsyncRelayCommand<object?>(p => ChooseAsync(p is Track t ? KeyForTrack(t) : null));
    public static ICommand ChooseForAlbumCommand { get; } =
        new AsyncRelayCommand<object?>(p => ChooseAsync(p is Album a ? KeyForAlbum(a) : null));
    public static ICommand ClearForTrackCommand { get; } =
        new RelayCommand<object?>(p => { if (p is Track t) Settings?.ClearLyricsBackgroundOverride(KeyForTrack(t)); });
    public static ICommand ClearForAlbumCommand { get; } =
        new RelayCommand<object?>(p => { if (p is Album a) Settings?.ClearLyricsBackgroundOverride(KeyForAlbum(a)); });

    /// <summary>True when the key has its own clip on disk (drives the "Use default video" item).</summary>
    public static bool HasOverride(string key) => Settings?.HasLyricsBackgroundOverride(key) == true;

    /// <summary>The picker filter shared with the Settings "Choose" button.</summary>
    public static FilePickerFileType[] MediaFilter => new[]
    {
        new FilePickerFileType(Noctis.Localization.Loc.T("Picker.VideoOrGif")) { Patterns = new[] { "*.mp4", "*.webm", "*.m4v", "*.mov", "*.mkv", "*.gif" } },
        new FilePickerFileType(Noctis.Localization.Loc.T("Picker.AllFiles")) { Patterns = new[] { "*" } },
    };

    private static SettingsViewModel? Settings => App.Services?.GetService<MainWindowViewModel>()?.Settings;

    private static async Task ChooseAsync(string? key)
    {
        if (key is null) return;
        var settings = Settings;
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (settings is null || window is null) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Noctis.Localization.Loc.T("Picker.ChooseLyricsBackground"),
                AllowMultiple = false,
                FileTypeFilter = MediaFilter,
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await settings.SetLyricsBackgroundOverrideAsync(key, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsBackground] pick failed for {key}: {ex.Message}");
        }
    }
}
