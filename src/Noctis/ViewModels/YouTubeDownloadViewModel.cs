using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>
/// "Add from YouTube": search or paste a link, download the audio into the library folder,
/// tagged and with cover art. Deliberately not a YouTube browser — results exist only to be
/// added to the user's own library.
/// </summary>
public partial class YouTubeDownloadViewModel : ViewModelBase
{
    private readonly IYouTubeImportService _service;
    private readonly HttpClient _http;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton), nameof(ShowResults), nameof(ShowEmpty))]
    private bool _isSearching;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _toolInstalled;
    [ObservableProperty] private bool _isInstallingTool;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolVersion))]
    private string _toolVersionText = string.Empty;

    [ObservableProperty] private string _destinationFolder = string.Empty;
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationFolder);

    public ObservableCollection<ResultRow> Results { get; } = new();
    public bool HasResults => Results.Count > 0;
    public bool ShowSetup => !ToolInstalled;

    // Pill dialog body states (exactly one shows): setup, skeleton while searching, the result
    // cards, or the one-line empty hint.
    public bool ShowSkeleton => !ShowSetup && IsSearching;
    public bool ShowResults => !ShowSetup && !IsSearching && HasResults;
    public bool ShowEmpty => !ShowSetup && !IsSearching && !HasResults;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public bool HasToolVersion => !ShowSetup && !string.IsNullOrEmpty(ToolVersionText);

    /// <summary>
    /// A newer yt-dlp exists than the copy in use. Noctis keeps its own copy current by itself,
    /// so this only stays true for a copy it may not replace (PATH, e.g. Chocolatey/winget, or a
    /// custom path): the footer then offers Update, which installs Noctis's own copy — it takes
    /// priority over PATH (YtDlpTool.Resolve). This was the Settings "Install / update" button
    /// until that row went (owner 10-08); an old PATH copy can quietly return no results.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate))]
    private bool _toolUpdateAvailable;
    public bool ShowToolUpdate => ToolUpdateAvailable && ToolInstalled && !IsInstallingTool;
    partial void OnIsInstallingToolChanged(bool value) => OnPropertyChanged(nameof(ShowToolUpdate));

    public event EventHandler? Closed;

    public YouTubeDownloadViewModel(IYouTubeImportService service, HttpClient http, string? initialQuery = null)
    {
        _service = service;
        _http = http;
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowResults));
            OnPropertyChanged(nameof(ShowEmpty));
        };
        ToolInstalled = service.Tool.IsAvailable;
        DestinationFolder = service.ResolveDownloadFolder();
        StatusMessage = HasDestination ? string.Empty : Loc.T("YouTube.Status.NoFolder");
        _ = CheckToolUpdateAsync();
        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            Query = initialQuery;
            _ = Search();
        }
    }

    partial void OnDestinationFolderChanged(string value) => OnPropertyChanged(nameof(HasDestination));
    partial void OnToolInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSetup));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasToolVersion));
        OnPropertyChanged(nameof(ShowToolUpdate));
    }

    private async Task RefreshVersionAsync()
    {
        if (!ToolInstalled) { ToolVersionText = string.Empty; ToolUpdateAvailable = false; return; }
        var v = await _service.Tool.GetVersionAsync(CancellationToken.None);
        ToolVersionText = YtDlpParsing.VersionLabel(v, _service.Tool.LatestKnownVersion);
        ToolUpdateAvailable = !string.IsNullOrWhiteSpace(v) && YtDlpParsing.IsNewer(_service.Tool.LatestKnownVersion, v);
    }

    /// <summary>Opening the downloader: show the version, then the quiet once-per-session update check.</summary>
    private async Task CheckToolUpdateAsync()
    {
        try
        {
            await RefreshVersionAsync();
            if (!ToolInstalled) return;
            await _service.Tool.EnsureSessionUpdateCheckAsync();
            await RefreshVersionAsync();
        }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "YtDlp.DialogCheckFailed", ex.Message); }
    }

    [RelayCommand]
    private async Task InstallTool()
    {
        if (IsInstallingTool) return;
        IsInstallingTool = true;
        InstallProgress = 0;
        StatusMessage = Loc.T("YouTube.Status.InstallingTool");
        try
        {
            await _service.Tool.InstallAsync(new Progress<double>(p => Dispatcher.UIThread.Post(() => InstallProgress = p)), CancellationToken.None);
            ToolInstalled = true;
            StatusMessage = Loc.T("YouTube.Status.Ready");
            await RefreshVersionAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("YouTube.Status.InstallFailed", ex.Message);
        }
        finally
        {
            IsInstallingTool = false;
        }
    }

    [RelayCommand]
    private async Task Search()
    {
        var q = Query?.Trim() ?? string.Empty;
        if (q.Length == 0) return;
        if (!ToolInstalled) { StatusMessage = Loc.T("YouTube.Status.InstallFirst"); return; }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        StatusMessage = Loc.T(YtDlpParsing.LooksLikeYouTubeUrl(q) ? "YouTube.Status.ReadingLink" : "YouTube.Status.Searching");
        try
        {
            List<YouTubeTrackInfo> found;
            if (YtDlpParsing.LooksLikeYouTubeUrl(q))
            {
                var info = await _service.ResolveAsync(q, cts.Token);
                found = info is null ? new List<YouTubeTrackInfo>() : new List<YouTubeTrackInfo> { info };
            }
            else
            {
                found = await _service.SearchAsync(q, cts.Token);
            }
            if (cts.IsCancellationRequested) return;

            Results.Clear();
            foreach (var info in found)
                Results.Add(new ResultRow(info, this));
            StatusMessage = found.Count == 0 ? Loc.T("YouTube.Status.NothingFound") : string.Empty;
            foreach (var row in Results.ToList())
                _ = LoadThumbnailAsync(row, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("YouTube.Status.SearchFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    private async Task LoadThumbnailAsync(ResultRow row, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.Info.ThumbnailUrl)) return;
        try
        {
            using var response = await _http.GetAsync(row.Info.ThumbnailUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;
            var bytes = await HttpSafety.ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, ct);
            if (!HttpSafety.LooksLikeImage(bytes)) return;
            using var ms = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(ms, 192);
            await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
        }
        catch { /* a missing thumbnail is cosmetic */ }
    }

    internal async Task DownloadAsync(ResultRow row)
    {
        if (row.IsBusy || row.IsDone) return;
        if (!HasDestination) { StatusMessage = Loc.T("YouTube.Status.NoFolder"); return; }
        row.IsBusy = true;
        row.Failed = false;
        row.Progress = 0;
        row.StatusText = Loc.T("YouTube.Row.Starting");
        var progress = new Progress<YouTubeImportProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            row.Progress = p.Fraction;
            row.StatusText = p.Stage;
        }));
        try
        {
            var result = await _service.ImportAsync(row.Info, progress, CancellationToken.None);
            row.IsDone = true;
            row.StatusText = Loc.T(result.AddedToLibrary ? "YouTube.Row.InLibrary" : "YouTube.Row.SavedToFolder");
            row.Title = result.Title;
            row.Subtitle = result.Artist;
        }
        catch (Exception ex)
        {
            row.Failed = true;
            row.StatusText = ex.Message.Length > 90 ? ex.Message[..90] + "…" : ex.Message;
            // The row clips long messages; the full one (e.g. "YouTube blocked this download…") goes up top.
            if (ex.Message.Length > 90) StatusMessage = ex.Message;
        }
        finally
        {
            row.IsBusy = false;
            _ = RefreshVersionAsync(); // a blocked download may have updated yt-dlp
        }
    }

    [RelayCommand]
    private void Close()
    {
        _searchCts?.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public sealed partial class ResultRow : ObservableObject
    {
        private readonly YouTubeDownloadViewModel _owner;

        public ResultRow(YouTubeTrackInfo info, YouTubeDownloadViewModel owner)
        {
            Info = info;
            _owner = owner;
            var (artist, title) = YtDlpParsing.InferTags(info);
            Title = title;
            var parts = new List<string> { artist };
            if (!string.IsNullOrWhiteSpace(info.Album) && !string.Equals(info.Album, title, StringComparison.OrdinalIgnoreCase)) parts.Add(info.Album!);
            if (info.DurationText.Length > 0) parts.Add(info.DurationText);
            Subtitle = string.Join(" · ", parts);
            DownloadCommand = new AsyncRelayCommand(() => _owner.DownloadAsync(this));
        }

        public YouTubeTrackInfo Info { get; }
        public IAsyncRelayCommand DownloadCommand { get; }

        [ObservableProperty] private string _title = string.Empty;
        [ObservableProperty] private string _subtitle = string.Empty;
        [ObservableProperty] private Bitmap? _thumbnail;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDownload))]
        private bool _isBusy;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDownload))]
        private bool _isDone;
        [ObservableProperty] private bool _failed;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressText))]
        private double _progress;
        [ObservableProperty] private string _statusText = string.Empty;

        public bool ShowDownload => !IsBusy && !IsDone;
        public bool HasThumbnail => Thumbnail is not null;
        /// <summary>"42%" on the card while it downloads.</summary>
        public string ProgressText => $"{(int)Math.Round(Math.Clamp(Progress, 0, 1) * 100)}%";

        partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    }
}
