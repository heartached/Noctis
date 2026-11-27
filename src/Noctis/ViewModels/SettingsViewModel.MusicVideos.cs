using Avalonia.Threading;
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
/// <para>
/// "Use my browser's YouTube sign-in" (11-25, off by default): yt-dlp reads the chosen
/// browser's YouTube cookies on this computer, past YouTube's "confirm you're not a bot".
/// A note under it says when the last lookup was refused or the sign-in could not be read.
/// </para>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicVideoCookiesFromBrowser))]
    private bool _musicVideoUseBrowserCookies;

    /// <summary>The picker's browser, by its own name ("Firefox").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicVideoCookieBrowserId), nameof(MusicVideoCookiesFromBrowser))]
    private string _musicVideoCookieBrowserName = YtDlpParsing.CookieBrowserName(YtDlpParsing.DefaultCookieBrowser);

    /// <summary>The browsers the picker lists (the ones yt-dlp reads on this OS).</summary>
    public IReadOnlyList<string> MusicVideoCookieBrowserNames { get; } =
        YtDlpParsing.CookieBrowsers().Select(YtDlpParsing.CookieBrowserName).ToList();

    /// <summary>yt-dlp's name for the picked browser ("firefox").</summary>
    public string MusicVideoCookieBrowserId =>
        YtDlpParsing.CookieBrowsers().FirstOrDefault(b => YtDlpParsing.CookieBrowserName(b) == MusicVideoCookieBrowserName)
        ?? YtDlpParsing.DefaultCookieBrowser;

    /// <summary>The picker's name for a saved browser; one this OS's yt-dlp can't read (Safari
    /// off macOS, an unknown name) falls back to the default.</summary>
    private static string CookieBrowserNameFor(string? id)
    {
        var b = YtDlpParsing.NormalizeCookieBrowser(id);
        return YtDlpParsing.CookieBrowserName(b != null && YtDlpParsing.CookieBrowsers().Contains(b) ? b : YtDlpParsing.DefaultCookieBrowser);
    }

    /// <summary>What yt-dlp gets as <c>--cookies-from-browser</c> now: the picked browser while the setting is on, else null.</summary>
    public string? MusicVideoCookiesFromBrowser => MusicVideoUseBrowserCookies ? MusicVideoCookieBrowserId : null;

    /// <summary>The last music video lookup was refused (bot check) or could not read the sign-in; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMusicVideoProblem))]
    private string _musicVideoProblemText = string.Empty;

    public bool HasMusicVideoProblem => !string.IsNullOrEmpty(MusicVideoProblemText);

    private MusicVideoStreamService? _musicVideoStreams;

    /// <summary>Follows the stream service's last problem for the note (set once at start-up, and by tests).</summary>
    public void AttachMusicVideoStreams(MusicVideoStreamService? service)
    {
        if (ReferenceEquals(_musicVideoStreams, service)) return;
        if (_musicVideoStreams != null) _musicVideoStreams.ProblemChanged -= OnMusicVideoProblemChanged;
        _musicVideoStreams = service;
        if (service != null) service.ProblemChanged += OnMusicVideoProblemChanged;
        MusicVideoProblemText = service?.LastProblem?.Message ?? string.Empty;
    }

    private void OnMusicVideoProblemChanged(object? sender, EventArgs e)
    {
        void Apply() => MusicVideoProblemText = _musicVideoStreams?.LastProblem?.Message ?? string.Empty;
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    partial void OnMusicVideoUseBrowserCookiesChanged(bool value) => OnMusicVideoSignInChanged();

    partial void OnMusicVideoCookieBrowserNameChanged(string value)
    {
        if (MusicVideoUseBrowserCookies) OnMusicVideoSignInChanged();
        else if (_settingsLoaded) _ = SaveAsync();
    }

    /// <summary>The sign-in changed: the old message and the bot-check pause no longer apply,
    /// and the song playing now is looked up again with it.</summary>
    private void OnMusicVideoSignInChanged()
    {
        if (!_settingsLoaded) return;
        _ = SaveAsync();
        _musicVideoStreams?.ClearProblem();
        _player?.RetryMusicVideoStream();
    }

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
