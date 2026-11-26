using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Lyrics › Music videos (Discord #feedback 11-25): "Find music videos
/// automatically" streams a song's music video from YouTube behind the lyrics. Off by
/// default — it sends the song's artist and title to YouTube. It needs yt-dlp, which the row
/// offers to install (the same copy the lyrics background video's YouTube panel installs).
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMusicVideoToolInstall))]
    private bool _musicVideoAutoFind;

    /// <summary>yt-dlp could not be found when the setting was last looked at.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMusicVideoToolInstall))]
    private bool _musicVideoToolMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMusicVideoToolInstall))]
    private bool _isInstallingMusicVideoTool;

    [ObservableProperty] private string _musicVideoToolStatus = string.Empty;

    /// <summary>The "yt-dlp is needed" row under the toggle.</summary>
    public bool ShowMusicVideoToolInstall => MusicVideoAutoFind && (MusicVideoToolMissing || IsInstallingMusicVideoTool);

    /// <summary>Tests hand their own tool in; the app uses the shared one.</summary>
    internal YtDlpTool? MusicVideoTool { get; set; }

    private YtDlpTool? ResolveMusicVideoTool() => MusicVideoTool ?? App.Services?.GetService<YtDlpTool>();

    partial void OnMusicVideoAutoFindChanged(bool value)
    {
        RefreshMusicVideoToolState();
        ApplyPlayerSettings();
        if (_settingsLoaded) _ = SaveAsync();
    }

    /// <summary>Re-checks whether yt-dlp is there (a file lookup, no process run).</summary>
    public void RefreshMusicVideoToolState()
    {
        var tool = ResolveMusicVideoTool();
        MusicVideoToolMissing = MusicVideoAutoFind && tool is not null && !tool.IsAvailable;
    }

    [RelayCommand]
    private async Task InstallMusicVideoTool()
    {
        var tool = ResolveMusicVideoTool();
        if (tool is null || IsInstallingMusicVideoTool) return;
        IsInstallingMusicVideoTool = true;
        MusicVideoToolStatus = Loc.T("LyricsVideo.InstallingTool");
        try
        {
            await tool.InstallAsync(null, CancellationToken.None);
            MusicVideoToolStatus = string.Empty;
            RefreshMusicVideoToolState();
            // The song playing now gets its video without waiting for the next one.
            if (_player is { } player)
            {
                player.MusicVideoAutoFindEnabled = false;
                player.MusicVideoAutoFindEnabled = MusicVideoAutoFind;
            }
        }
        catch (Exception ex)
        {
            MusicVideoToolStatus = Loc.T("LyricsVideo.InstallFailed", ex.Message);
        }
        finally { IsInstallingMusicVideoTool = false; }
    }
}
