using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>One YouTube video in the music video dialog (a search result or a pasted link).</summary>
public partial class MusicVideoCandidateRow : ObservableObject
{
    public MusicVideoCandidateRow(YouTubeVideoInfo info) => Info = info;

    public YouTubeVideoInfo Info { get; }
    public string Title => Info.Title;
    public string Detail => Info.Detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail != null;

    /// <summary>The video this song plays now.</summary>
    [ObservableProperty] private bool _isCurrent;

    public override string ToString() => string.IsNullOrEmpty(Detail) ? Title : $"{Title}, {Detail}";
}

/// <summary>
/// "Music Video from YouTube" for one song (Discord #feedback 11-25): paste a link (its title,
/// thumbnail and length show) and Stream it or Download it, or pick another result of the
/// automatic search ("Wrong video?"), or say "Don't use a video for this song". A choice made
/// here is the user's: it wins over the automatic match from then on (persisted per song).
/// Opened from the lyrics page (island ⋯ and the "Wrong video?" button) and the song menu.
/// </summary>
public partial class MusicVideoPickerViewModel : ObservableObject
{
    private const int LinkDebounceMs = 400;

    private readonly Track _track;
    private readonly MusicVideoStreamService _service;
    private readonly Func<string, CancellationToken, Task<Bitmap?>>? _thumbnail;
    private readonly Func<string, IProgress<double>?, CancellationToken, Task>? _download;
    private readonly Func<IProgress<double>?, CancellationToken, Task>? _installTool;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _linkCts;
    private CancellationTokenSource? _downloadCts;

    public event EventHandler? Closed;

    public ObservableCollection<MusicVideoCandidateRow> Results { get; } = new();

    /// <param name="download">Downloads a link as the song's own lyrics background video
    /// (the "Download" path, saved like the Lyrics Background Video picker saves); null hides it.</param>
    /// <param name="installTool">Installs yt-dlp (Noctis's own copy); null when it can't.</param>
    public MusicVideoPickerViewModel(Track track, MusicVideoStreamService service,
        Func<string, CancellationToken, Task<Bitmap?>>? thumbnail = null,
        Func<string, IProgress<double>?, CancellationToken, Task>? download = null,
        Func<IProgress<double>?, CancellationToken, Task>? installTool = null)
    {
        _track = track;
        _service = service;
        _thumbnail = thumbnail;
        _download = download;
        _installTool = installTool;
        ToolInstalled = service.IsToolAvailable;
        RefreshCurrent();
    }

    public string TrackTitle => _track.TitleDisplay;
    public string TrackArtist => _track.ArtistDisplay;
    public bool CanDownload => _download != null;
    public bool CanInstallTool => _installTool != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolMissing))]
    private bool _toolInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolMissing))]
    private bool _isInstallingTool;

    public bool ShowToolMissing => !ToolInstalled;

    /// <summary>What the song plays now ("Your choice: …", "Automatic match: …", "No video for this song").</summary>
    [ObservableProperty] private string _currentText = string.Empty;

    /// <summary>The user picked something for this song (offers "Back to automatic").</summary>
    [ObservableProperty] private bool _hasUserChoice;

    [ObservableProperty] private string _linkText = string.Empty;

    /// <summary>The pasted link's video, once looked up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkPreview))]
    [NotifyCanExecuteChangedFor(nameof(StreamLinkCommand), nameof(DownloadLinkCommand))]
    private MusicVideoCandidateRow? _linkPreview;

    public bool HasLinkPreview => LinkPreview != null;

    [ObservableProperty] private bool _isLookingUp;
    [ObservableProperty] private bool _isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StreamLinkCommand), nameof(DownloadLinkCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkStatus))]
    private string _linkStatus = string.Empty;

    public bool HasLinkStatus => !string.IsNullOrEmpty(LinkStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchStatus))]
    private string _searchStatus = string.Empty;

    public bool HasSearchStatus => !string.IsNullOrEmpty(SearchStatus);

    /// <summary>The last link lookup (tests await it).</summary>
    internal Task LinkLookup { get; private set; } = Task.CompletedTask;

    private void RefreshCurrent()
    {
        var choice = _service.GetChoice(_track);
        HasUserChoice = choice?.Source == MusicVideoChoiceSource.User;
        CurrentText = choice switch
        {
            null => Loc.T("MusicVideo.CurrentAutomatic"),
            { Kind: MusicVideoChoiceKind.NoVideo, Source: MusicVideoChoiceSource.User } => Loc.T("MusicVideo.CurrentNone"),
            { Kind: MusicVideoChoiceKind.NoVideo } => Loc.T("MusicVideo.CurrentNoMatch"),
            { Source: MusicVideoChoiceSource.User } => Loc.T("MusicVideo.CurrentUser", choice.VideoTitle ?? choice.VideoId ?? string.Empty),
            _ => Loc.T("MusicVideo.CurrentAuto", choice.VideoTitle ?? choice.VideoId ?? string.Empty),
        };
        var currentId = choice?.Kind == MusicVideoChoiceKind.Video ? choice.VideoId : null;
        foreach (var row in Results) row.IsCurrent = row.Info.Id == currentId;
    }

    // ── Paste a link ──

    partial void OnLinkTextChanged(string value)
    {
        _linkCts?.Cancel();
        _linkCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _linkCts = cts;
        LinkPreview = null;
        LinkStatus = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) { LinkLookup = Task.CompletedTask; return; }
        LinkLookup = LookUpLinkAsync(value, cts.Token);
    }

    private async Task LookUpLinkAsync(string text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(LinkDebounceMs, ct);
            var id = YouTubeLinks.TryGetVideoId(text);
            if (id is null) { LinkStatus = Loc.T("MusicVideo.NotALink"); return; }
            if (!ToolInstalled) { LinkStatus = Loc.T("LyricsVideo.InstallFirst"); return; }
            IsLookingUp = true;
            LinkStatus = Loc.T("MusicVideo.LookingUp");
            var info = await Task.Run(() => _service.LookUpLinkAsync(YouTubeLinks.WatchUrl(id), ct), ct);
            if (ct.IsCancellationRequested) return;
            if (info is null) { LinkStatus = Loc.T("MusicVideo.LinkNotFound"); return; }
            var row = new MusicVideoCandidateRow(info);
            LinkPreview = row;
            LinkStatus = string.Empty;
            _ = LoadThumbnailAsync(row, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) LinkStatus = Explain(ex) ?? Loc.T("MusicVideo.LinkFailed", ex.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLookingUp = false;
        }
    }

    private bool CanUseLink() => LinkPreview != null && !IsDownloading;

    /// <summary>Stream: no download, the video plays from YouTube behind the lyrics.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLink))]
    private void StreamLink()
    {
        if (LinkPreview is not { } row) return;
        _service.SetUserVideo(_track, row.Info);
        Close();
    }

    /// <summary>Download: saved as the song's own lyrics background video, as the Lyrics
    /// Background Video picker saves a YouTube clip; the streaming choice is dropped.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLink))]
    private async Task DownloadLink()
    {
        if (LinkPreview is not { } row || _download is null) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCts = cts;
        IsDownloading = true;
        DownloadProgress = 0;
        LinkStatus = Loc.T("LyricsVideo.Downloading");
        try
        {
            await _download(row.Info.WatchUrl,
                new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p)), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            // The song now has its own clip; a streamed choice would cover it.
            if (_service.GetChoice(_track) is not null) _service.ClearChoice(_track);
            LinkStatus = string.Empty;
            Close();
        }
        catch (OperationCanceledException)
        {
            LinkStatus = Loc.T("LyricsVideo.Cancelled");
        }
        catch (Exception ex)
        {
            _service.NoteFailure(ex);
            LinkStatus = Explain(ex) ?? Loc.T("LyricsVideo.DownloadFailed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    // ── Search results ("Wrong video?") ──

    [RelayCommand]
    private async Task Search()
    {
        if (IsSearching) return;
        Results.Clear();
        if (!ToolInstalled) { SearchStatus = Loc.T("LyricsVideo.InstallFirst"); return; }
        IsSearching = true;
        SearchStatus = Loc.T("MusicVideo.Searching");
        var ct = _lifetime.Token;
        try
        {
            var found = await Task.Run(() => _service.SearchCandidatesAsync(_track, ct), ct);
            if (ct.IsCancellationRequested) return;
            foreach (var info in found) Results.Add(new MusicVideoCandidateRow(info));
            RefreshCurrent();
            SearchStatus = Results.Count == 0 ? Loc.T("MusicVideo.NoResults") : string.Empty;
            foreach (var row in Results) _ = LoadThumbnailAsync(row, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) SearchStatus = Explain(ex) ?? Loc.T("MusicVideo.SearchFailed", ex.Message);
        }
        finally { IsSearching = false; }
    }

    /// <summary>A search result picked as this song's video.</summary>
    [RelayCommand]
    private void UseCandidate(MusicVideoCandidateRow? row)
    {
        if (row is null) return;
        _service.SetUserVideo(_track, row.Info);
        Close();
    }

    [RelayCommand]
    private void NoVideo()
    {
        _service.SetNoVideo(_track);
        Close();
    }

    /// <summary>Forget the user's choice: the automatic match decides again.</summary>
    [RelayCommand]
    private void BackToAutomatic()
    {
        _service.ClearChoice(_track);
        Close();
    }

    [RelayCommand]
    private async Task InstallTool()
    {
        if (_installTool is null || IsInstallingTool) return;
        IsInstallingTool = true;
        SearchStatus = Loc.T("LyricsVideo.InstallingTool");
        try
        {
            await _installTool(null, _lifetime.Token);
            ToolInstalled = _service.IsToolAvailable;
            SearchStatus = string.Empty;
            if (ToolInstalled) await Search();
        }
        catch (OperationCanceledException) { SearchStatus = string.Empty; }
        catch (Exception ex) { SearchStatus = Loc.T("LyricsVideo.InstallFailed", ex.Message); }
        finally { IsInstallingTool = false; }
    }

    [RelayCommand]
    private void Close()
    {
        Stop();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window is going away by any route: stop lookups, searches and a download.</summary>
    public void Stop()
    {
        _linkCts?.Cancel();
        _downloadCts?.Cancel();
        _lifetime.Cancel();
    }

    /// <summary>A bot check / browser sign-in failure says what to do on its own (no "Search failed:" in front).</summary>
    private static string? Explain(Exception ex) => YtDlpAccessException.Find(ex)?.Message;

    private async Task LoadThumbnailAsync(MusicVideoCandidateRow row, CancellationToken ct)
    {
        if (_thumbnail is null) return;
        try
        {
            var bitmap = await _thumbnail(row.Info.ThumbnailUrl, ct);
            if (bitmap is null) return;
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
            await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
        }
        catch { /* a missing thumbnail is cosmetic */ }
    }
}
