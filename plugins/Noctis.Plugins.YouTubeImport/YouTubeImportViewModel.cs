using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// "Add from YouTube": search or paste a link, pick a format, download the audio into the
/// library folder, tagged and with cover art. Deliberately not a YouTube browser — results
/// exist only to be added to the user's own library. Moved from the app's
/// YouTubeDownloadViewModel (10-29) with three additions: the format choice, a download folder
/// that can be changed here, and the recent downloads.
/// </summary>
public partial class YouTubeImportViewModel : ObservableObject
{
    private readonly IYouTubeImporter _service;
    private readonly HttpClient _http;
    private readonly ImportState _state;
    private readonly CancellationToken _lifetime;
    private readonly Func<string, bool>? _showAlbum;
    private readonly Func<string, bool>? _showTrack;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    private string _destinationFolder = string.Empty;
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationFolder);

    /// <summary>The user picked a folder here (Reset goes back to the default next to the music folders).</summary>
    public bool HasCustomFolder => !string.IsNullOrWhiteSpace(_state.DownloadFolder);

    public ObservableCollection<ResultRow> Results { get; } = new();
    public bool HasResults => Results.Count > 0;
    public bool ShowSetup => !ToolInstalled;

    // Body states (exactly one shows): setup, skeleton while searching, the result cards, or
    // the one-line empty hint.
    public bool ShowSkeleton => !ShowSetup && IsSearching;
    public bool ShowResults => !ShowSetup && !IsSearching && HasResults;
    public bool ShowEmpty => !ShowSetup && !IsSearching && !HasResults;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public bool HasToolVersion => !ShowSetup && !string.IsNullOrEmpty(ToolVersionText);

    /// <summary>The formats on offer: M4A always, MP3 and Opus only with ffmpeg.</summary>
    public IReadOnlyList<AudioFormat> Formats { get; }
    [ObservableProperty] private AudioFormat _selectedFormat;

    public ObservableCollection<RecentDownload> Recent { get; } = new();
    public bool HasRecent => Recent.Count > 0;

    /// <summary>
    /// A newer yt-dlp exists than the copy in use. The plugin keeps its own copy current by itself,
    /// so this only stays true for a copy it may not replace (PATH, e.g. Chocolatey/winget, or a
    /// custom path): the footer then offers Update, which installs the plugin's own copy — it takes
    /// priority over PATH (YtDlpTool.Resolve). An old PATH copy can quietly return no results.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate))]
    private bool _toolUpdateAvailable;
    public bool ShowToolUpdate => ToolUpdateAvailable && ToolInstalled && !IsInstallingTool;
    partial void OnIsInstallingToolChanged(bool value) => OnPropertyChanged(nameof(ShowToolUpdate));

    /// <summary>Set by the view: opens a folder picker and returns the chosen folder, or null.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <param name="lifetime">Cancelled when the plugin stops: downloads still running end then.</param>
    /// <param name="showAlbum">Shows a library album in Noctis (<see cref="IPluginHost.ShowAlbum"/>); null = no Show button.</param>
    /// <param name="showTrack">Shows a library song in Noctis, scrolled to (<see cref="IPluginHost.ShowTrack"/>, API 1.10).</param>
    public YouTubeImportViewModel(IYouTubeImporter service, HttpClient http, ImportState state, CancellationToken lifetime = default, string? initialQuery = null,
        Func<string, bool>? showAlbum = null, Func<string, bool>? showTrack = null)
    {
        _showAlbum = showAlbum;
        _showTrack = showTrack;
        _service = service;
        _http = http;
        _state = state;
        _lifetime = lifetime;
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(ShowResults));
            OnPropertyChanged(nameof(ShowEmpty));
        };
        Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));
        Formats = service.HasFfmpeg ? AudioFormat.All : new[] { AudioFormat.M4a };
        _selectedFormat = Formats.FirstOrDefault(f => f.Key == _state.Format) ?? AudioFormat.Default;
        ToolInstalled = service.Tool.IsAvailable;
        DestinationFolder = service.ResolveDownloadFolder();
        StatusMessage = HasDestination ? string.Empty : NoFolderMessage;
        ReloadRecent();
        _state.RecentChanged += OnRecentChanged;
        _ = CheckToolUpdateAsync();
        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            Query = initialQuery;
            _ = Search();
        }
    }

    internal const string NoFolderMessage = "Add a music folder in Settings → Library first, or choose a download folder.";

    partial void OnSelectedFormatChanged(AudioFormat value)
    {
        if (value is not null) _state.SetFormat(value.Key);
    }

    partial void OnToolInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSetup));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasToolVersion));
        OnPropertyChanged(nameof(ShowToolUpdate));
    }

    private void OnRecentChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) ReloadRecent();
        else Dispatcher.UIThread.Post(ReloadRecent);
    }

    private void ReloadRecent()
    {
        Recent.Clear();
        foreach (var r in _state.Recent) Recent.Add(r);
    }

    /// <summary>The view went away: stop listening to the shared state, end a search.</summary>
    public void Detach()
    {
        _state.RecentChanged -= OnRecentChanged;
        _searchCts?.Cancel();
    }

    private async Task RefreshVersionAsync()
    {
        if (!ToolInstalled) { ToolVersionText = string.Empty; ToolUpdateAvailable = false; return; }
        var v = await _service.Tool.GetVersionAsync(CancellationToken.None);
        ToolVersionText = YtDlpParsing.VersionLabel(v, _service.Tool.LatestKnownVersion);
        ToolUpdateAvailable = !string.IsNullOrWhiteSpace(v) && YtDlpParsing.IsNewer(_service.Tool.LatestKnownVersion, v);
    }

    /// <summary>Opening the window: show the version, then the quiet once-per-session update check.</summary>
    private async Task CheckToolUpdateAsync()
    {
        try
        {
            await RefreshVersionAsync();
            if (!ToolInstalled) return;
            await _service.Tool.EnsureSessionUpdateCheckAsync();
            await RefreshVersionAsync();
        }
        catch { /* the version note is cosmetic */ }
    }

    [RelayCommand]
    private async Task InstallTool()
    {
        if (IsInstallingTool) return;
        IsInstallingTool = true;
        InstallProgress = 0;
        StatusMessage = "Downloading yt-dlp…";
        try
        {
            // Progress<T> made here already reports on the UI thread: no second Dispatcher hop.
            // A report still queued when the install returns must not overwrite "yt-dlp is ready."
            var downloading = true;
            var detail = new Progress<DownloadReport>(r =>
            {
                if (downloading) StatusMessage = $"Downloading yt-dlp · {DownloadFormat.Format(r.BytesPerSec, r.Eta)}";
            });
            try { await _service.Tool.InstallAsync(new Progress<double>(p => InstallProgress = p), _lifetime, detail); }
            finally { downloading = false; }
            ToolInstalled = true;
            StatusMessage = "yt-dlp is ready.";
            await RefreshVersionAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't install yt-dlp — {ex.Message}";
        }
        finally
        {
            IsInstallingTool = false;
        }
    }

    [RelayCommand]
    private async Task ChangeFolder()
    {
        if (PickFolder is null) return;
        string? picked;
        try { picked = await PickFolder(); }
        catch (Exception ex) { StatusMessage = "Couldn't open the folder picker — " + ex.Message; return; }
        if (string.IsNullOrWhiteSpace(picked)) return;
        _state.SetDownloadFolder(picked);
        RefreshDestination();
    }

    [RelayCommand]
    private void ResetFolder()
    {
        _state.SetDownloadFolder(null);
        RefreshDestination();
    }

    private void RefreshDestination()
    {
        DestinationFolder = _service.ResolveDownloadFolder();
        OnPropertyChanged(nameof(HasCustomFolder));
        if (HasDestination && StatusMessage == NoFolderMessage) StatusMessage = string.Empty;
        else if (!HasDestination) StatusMessage = NoFolderMessage;
    }

    [RelayCommand]
    private void ClearRecent() => _state.ClearRecent();

    [RelayCommand]
    private async Task Search()
    {
        var q = Query?.Trim() ?? string.Empty;
        if (q.Length == 0) return;
        if (!ToolInstalled) { StatusMessage = "Install yt-dlp first."; return; }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        StatusMessage = YtDlpParsing.LooksLikeYouTubeUrl(q) ? "Reading link…" : "Searching…";
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
            {
                var row = new ResultRow(info, this);
                // Downloaded before: say so instead of offering it again.
                if (_state.FindDownloaded(info.Id) is { } earlier) MarkAlreadyThere(row, earlier);
                Results.Add(row);
            }
            StatusMessage = found.Count == 0 ? "Nothing found." : string.Empty;
            foreach (var row in Results.ToList())
                _ = LoadThumbnailAsync(row, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed — {ex.Message}";
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
            var bytes = await HttpBounded.ReadBytesAsync(response.Content, HttpBounded.MaxImageBytes, ct);
            if (!HttpBounded.LooksLikeImage(bytes)) return;
            using var ms = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(ms, 192);
            await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
        }
        catch { /* a missing thumbnail is cosmetic */ }
    }

    private static void MarkAlreadyThere(ResultRow row, DownloadedVideo earlier)
    {
        row.IsDone = true;
        row.Failed = false;
        row.AlbumId = earlier.AlbumId;
        row.TrackId = earlier.TrackId;
        row.StatusText = earlier.InLibrary ? "Already in your library" : "Already downloaded";
    }

    internal const string NotInLibraryMessage = "Couldn't find it in your library. It may have been moved or removed.";

    /// <summary>
    /// Show: the song itself (API 1.10: its album page, scrolled to it), else its album (a record
    /// from 1.0.3 has no song id). Before 1.0.4 a false answer was dropped, so a Show that found
    /// nothing looked like a dead button: it now says so.
    /// </summary>
    internal bool ShowInLibrary(ResultRow row)
    {
        Exception? error = null;
        if (row.TrackId.Length > 0 && TryShow(_showTrack, row.TrackId, ref error)) return true;
        if (row.AlbumId.Length > 0 && TryShow(_showAlbum, row.AlbumId, ref error)) return true;
        StatusMessage = error is null ? NotInLibraryMessage : "Couldn't show it — " + error.Message;
        return false;
    }

    private static bool TryShow(Func<string, bool>? show, string id, ref Exception? error)
    {
        if (show is null) return false;
        // A host without the call (MissingMethodException when the lambda compiles) falls through to the album.
        try { return show(id); }
        catch (Exception ex) { error ??= ex; return false; }
    }

    internal bool CanShowInLibrary => _showAlbum is not null || _showTrack is not null;

    internal async Task DownloadAsync(ResultRow row)
    {
        if (row.IsBusy || row.IsDone) return;
        if (!HasDestination) { StatusMessage = NoFolderMessage; return; }
        var id = row.Info.Id ?? string.Empty;
        if (_state.FindDownloaded(id) is { } earlier)
        {
            MarkAlreadyThere(row, earlier);
            return;
        }
        // The same video downloading in this window or another one: never twice at once.
        if (id.Length > 0 && !_state.TryStartDownload(id))
        {
            StatusMessage = "This video is already downloading.";
            return;
        }
        var run = row.BeginRun();
        // Reports come from yt-dlp's reader thread: onto the UI thread, and only into this run
        // while it is still running. Before, each one took two hops (Progress<T> posted, then
        // Dispatcher.Post again), so the import's last "Done" could land after the card had
        // finished and overwrite "In your library".
        var progress = new InlineProgress<YouTubeImportProgress>(p =>
        {
            if (Dispatcher.UIThread.CheckAccess()) row.Report(run, p);
            else Dispatcher.UIThread.Post(() => row.Report(run, p));
        });
        try
        {
            var result = await _service.ImportAsync(row.Info, SelectedFormat ?? AudioFormat.Default, progress, _lifetime);
            row.IsDone = true;
            row.AlbumId = result.AlbumId;
            row.TrackId = result.TrackId;
            row.Title = result.Title;
            row.Subtitle = result.Artist;
            if (id.Length > 0) _state.AddDownloaded(new DownloadedVideo(id, result.FilePath, result.AlbumId, result.AddedToLibrary, result.TrackId));
            if (result.AlreadyDownloaded)
                row.StatusText = result.AddedToLibrary ? "Already in your library" : "Already downloaded";
            else
            {
                row.StatusText = result.AddedToLibrary ? "In your library" : "Saved to folder";
                _state.AddRecent(new RecentDownload(result.Title, result.Artist, result.FilePath, DateTimeOffset.Now, result.AddedToLibrary));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            row.Failed = true;
            row.StatusText = "Stopped.";
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
            if (id.Length > 0) _state.EndDownload(id);
            row.IsBusy = false;
            _ = RefreshVersionAsync(); // a blocked download may have updated yt-dlp
        }
    }

    public sealed partial class ResultRow : ObservableObject
    {
        private readonly YouTubeImportViewModel _owner;

        public ResultRow(YouTubeTrackInfo info, YouTubeImportViewModel owner)
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
            ShowCommand = new RelayCommand(() => _owner.ShowInLibrary(this));
        }

        /// <summary>Shows the song in Noctis (its album page, scrolled to it).</summary>
        public IRelayCommand ShowCommand { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanShow))]
        private string _albumId = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanShow))]
        private string _trackId = string.Empty;

        /// <summary>Done (or downloaded before) and the song or its album is known.</summary>
        public bool CanShow => IsDone && (TrackId.Length > 0 || AlbumId.Length > 0) && _owner.CanShowInLibrary;

        public YouTubeTrackInfo Info { get; }
        public IAsyncRelayCommand DownloadCommand { get; }

        [ObservableProperty] private string _title = string.Empty;
        [ObservableProperty] private string _subtitle = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasThumbnail))]
        private Bitmap? _thumbnail;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDownload), nameof(IsIndeterminate), nameof(ShowPercent), nameof(HasStatusLine), nameof(BusyText))]
        private bool _isBusy;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDownload), nameof(CanShow), nameof(DoneText))]
        private bool _isDone;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusLine), nameof(FailedText))]
        private bool _failed;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressText), nameof(IsIndeterminate), nameof(ShowPercent))]
        private double _progress;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneText), nameof(BusyText), nameof(FailedText))]
        private string _statusText = string.Empty;

        public bool ShowDownload => !IsBusy && !IsDone;
        public bool HasThumbnail => Thumbnail is not null;
        /// <summary>"42%" on the card while it downloads.</summary>
        public string ProgressText => $"{(int)Math.Round(Math.Clamp(Progress, 0, 1) * 100)}%";
        /// <summary>Busy with no number yet (reading details, yt-dlp starting): the bar sweeps.</summary>
        public bool IsIndeterminate => IsBusy && Progress <= 0;
        /// <summary>The percent beside the bar: only once there is a number.</summary>
        public bool ShowPercent => IsBusy && Progress > 0;
        /// <summary>The card's third line is in use: the bar and its stage, or the error.</summary>
        public bool HasStatusLine => IsBusy || Failed;
        /// <summary>"In your library" beside the check: only when done (the stage line never shows there).</summary>
        public string DoneText => IsDone ? StatusText : string.Empty;
        /// <summary>The stage under the bar: only while busy (the error and "In your library" have their own places).</summary>
        public string BusyText => IsBusy ? StatusText : string.Empty;
        /// <summary>The error on the third line: only when it failed.</summary>
        public string FailedText => Failed ? StatusText : string.Empty;

        private int _run;

        /// <summary>A download starts: busy, no number yet; reports of an earlier run are ignored from now.</summary>
        internal int BeginRun()
        {
            IsBusy = true;
            Failed = false;
            Progress = 0;
            StatusText = "Starting…";
            return ++_run;
        }

        /// <summary>
        /// One report of run <paramref name="run"/>: the stage line, and the bar only forward (a
        /// retry's download starting over at 0%, or a stage without a number, holds it where it is).
        /// Ignored once the run ended or another began.
        /// </summary>
        internal void Report(int run, YouTubeImportProgress p)
        {
            if (run != _run || !IsBusy || IsDone) return;
            if (!string.IsNullOrEmpty(p.Stage)) StatusText = p.Stage;
            var fraction = double.IsFinite(p.Fraction) ? Math.Clamp(p.Fraction, 0, 1) : 0;
            if (fraction > Progress) Progress = fraction;
        }
    }
}
