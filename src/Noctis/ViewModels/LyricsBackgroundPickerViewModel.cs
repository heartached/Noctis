using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.YouTube;

namespace Noctis.ViewModels;

/// <summary>A resolution cap for the YouTube backdrop download (0 = best available).</summary>
public sealed record BackdropQuality(string Label, int MaxHeight)
{
    public override string ToString() => Label;
}

/// <summary>One row of the lyrics background picker: a song or an album.</summary>
public partial class LyricsBackgroundPickItem : ObservableObject
{
    public required string Key { get; init; }
    public required bool IsAlbum { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? ArtworkPath { get; init; }
    /// <summary>Song rows: their album's key. A song without a clip of its own plays its
    /// album's (PlayerViewModel.ResolveLyricsBackgroundFor: song, then album, then default).</summary>
    public string? AlbumKey { get; init; }

    /// <summary>File name of the item's own clip, or empty when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnVideo), nameof(UsesAlbumVideo), nameof(StatusText), nameof(ChooseLabel))]
    private string _videoName = string.Empty;

    /// <summary>Song rows: the album has a clip of its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesAlbumVideo), nameof(StatusText))]
    private bool _albumHasVideo;

    /// <summary>A YouTube download for this row is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isDownloading;

    public bool HasOwnVideo => !string.IsNullOrEmpty(VideoName);
    public bool UsesAlbumVideo => !HasOwnVideo && AlbumHasVideo;

    /// <summary>The stored clip is named after the key (album_&lt;id&gt;.mp4), which means nothing
    /// to the user (09-19), so the row says which clip plays for it instead.</summary>
    public string StatusText => Loc.T(
        IsDownloading ? "LyricsVideo.RowDownloading"
        : HasOwnVideo ? "LyricsBackground.OwnVideo"
        : UsesAlbumVideo ? "LyricsVideo.UsesAlbumVideo"
        : "LyricsBackground.UsesDefault");

    /// <summary>The row's one text action: "Choose video", or "Change" once it has one.</summary>
    public string ChooseLabel => Loc.T(HasOwnVideo ? "LyricsVideo.Change" : "LyricsBackground.ChooseVideo");
}

/// <summary>
/// Settings › Appearance › Lyrics Background Video › Modify (user ask 2026-09-07), also opened
/// from Lyrics Studio. One place for the default clip and for the songs and albums that carry
/// their own: search the library, give any row a video (a file or a YouTube link), or send it
/// back to the default. Writes go straight through <see cref="SettingsViewModel"/> so the
/// lyrics page follows immediately.
/// <para>
/// The user's latest choice wins (10-08): Remove or a picked file for the row a YouTube
/// download is for cancels that download, and a Cancel or close that lands as yt-dlp exits 0
/// still stops the clip from being applied.
/// </para>
/// </summary>
public partial class LyricsBackgroundPickerViewModel : ObservableObject
{
    private const int MaxAlbumRows = 20;
    private const int MaxTrackRows = 80;

    private readonly SettingsViewModel _settings;
    private readonly ILibraryService _library;
    private readonly YtDlpTool? _ytDlp;
    private readonly Func<string?>? _ffmpegPath;
    /// <summary>Cancelled when the dialog closes: a yt-dlp install stops with it.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _downloadCts;
    /// <summary>What the running download will set: the default, or the row with this key.</summary>
    private bool _downloadForDefault;
    private string? _downloadKey;
    /// <summary>The download's clip being copied into place (a short local copy that can't be
    /// cancelled): a Remove or pick for the same target waits for it, then replaces it.</summary>
    private Task _applying = Task.CompletedTask;

    public static readonly IReadOnlyList<BackdropQuality> Qualities = new[]
    {
        new BackdropQuality(Loc.T("LyricsVideo.BestQuality"), 0),
        new BackdropQuality("1080p", 1080),
        new BackdropQuality("720p", 720),
        new BackdropQuality("480p", 480),
        new BackdropQuality("360p", 360),
    };

    /// <summary>Opens the video file picker; set by the dialog so the picker parents to it.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    public event EventHandler? CloseRequested;

    public ObservableCollection<LyricsBackgroundPickItem> Results { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The stored default clip (under the data root), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultVideo), nameof(DefaultChooseLabel))]
    private string _defaultVideoPath = string.Empty;

    /// <summary>"MP4 · 12.4 MB": the stored copy is always background.&lt;ext&gt;, so its name
    /// says nothing; its kind and size do.</summary>
    [ObservableProperty]
    private string _defaultVideoInfo = string.Empty;

    public bool HasDefaultVideo => !string.IsNullOrEmpty(DefaultVideoPath);
    public string DefaultChooseLabel => Loc.T(HasDefaultVideo ? "LyricsVideo.Change" : "LyricsBackground.ChooseVideo");

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    /// <summary>No search: the list shows the songs and albums that already have their own clip.</summary>
    public bool ShowOverridesHeader => !IsSearching && Results.Count > 0;
    public bool ShowPrompt => !IsSearching && Results.Count == 0;
    public bool ShowNoResults => IsSearching && Results.Count == 0;

    // ── From YouTube (user ask 09-17): paste a link, pick a resolution, the clip lands as
    //    the default or as one song's/album's own backdrop. Video only — the backdrop is muted.
    [ObservableProperty] private bool _showYouTube;
    [ObservableProperty] private string _youTubeUrl = string.Empty;
    [ObservableProperty] private BackdropQuality _selectedQuality = Qualities[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate), nameof(CanCancelDownload))]
    private bool _isDownloading;

    /// <summary>The downloaded clip is being copied into place; too late to cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelDownload))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadPercentText))]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYouTubeStatus))]
    private string _youTubeStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate), nameof(HasToolVersion))]
    private bool _toolInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate))]
    private bool _isInstallingTool;

    [ObservableProperty] private double _installProgress;

    /// <summary>"yt-dlp 2026.08.19" (+ "update available"), small secondary text in the YouTube panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolVersion))]
    private string _toolVersionText = string.Empty;

    /// <summary>A newer yt-dlp exists than the copy in use. Noctis updates its own copy by
    /// itself, so this stays true only for a copy it may not replace (PATH, a custom path):
    /// Update then installs Noctis's own copy, which takes priority (YtDlpTool.Resolve). Same
    /// rule as the YouTube downloader.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowToolUpdate))]
    private bool _toolUpdateAvailable;

    private bool _toolUpdateChecked;

    /// <summary>Row the download is for; null = the default video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeTargetLabel))]
    private LyricsBackgroundPickItem? _youTubeTarget;

    public IReadOnlyList<BackdropQuality> QualityOptions => Qualities;
    public bool CanUseYouTube => _ytDlp is not null;
    public string YouTubeTargetLabel => YouTubeTarget is { } t ? t.Title : Loc.T("LyricsBackground.DefaultVideo");
    public bool CanCancelDownload => IsDownloading && !IsApplying;
    public bool HasYouTubeStatus => !string.IsNullOrEmpty(YouTubeStatus);
    public bool HasToolVersion => ToolInstalled && !string.IsNullOrEmpty(ToolVersionText);
    public bool ShowToolUpdate => ToolUpdateAvailable && ToolInstalled && !IsInstallingTool && !IsDownloading;
    public string DownloadPercentText =>
        ((int)Math.Round(Math.Clamp(DownloadProgress, 0, 1) * 100)).ToString(CultureInfo.CurrentCulture) + "%";

    /// <summary>Where each download gets a folder of its own (tests point it elsewhere).</summary>
    internal string ScratchRoot { get; set; } = Path.Combine(Path.GetTempPath(), "noctis-backdrops");

    /// <summary>The last download's scratch clean-up (tests await it).</summary>
    internal Task Cleanup { get; private set; } = Task.CompletedTask;

    public LyricsBackgroundPickerViewModel(SettingsViewModel settings, ILibraryService library,
        YtDlpTool? ytDlp = null, Func<string?>? ffmpegPath = null)
    {
        _settings = settings;
        _library = library;
        _ytDlp = ytDlp;
        _ffmpegPath = ffmpegPath;
        ToolInstalled = ytDlp?.IsAvailable == true;
        RefreshDefault();
        RefreshResults();
    }

    // Debounced, like Add Songs: RefreshResults scans the whole library on the UI thread
    // (Take only stops early on a broad query), and it ran for every character typed.
    private const int SearchDebounceMs = 250;
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>The last debounced search refresh (tests await it).</summary>
    internal Task SearchRefresh { get; private set; } = Task.CompletedTask;

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        SearchRefresh = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, token);
            if (token.IsCancellationRequested) return;
            RefreshResults();
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    private void RefreshResults()
    {
        Results.Clear();
        var query = (SearchText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            foreach (var item in OverrideRows()) Results.Add(item);
        }
        else
        {
            // Normalize the query once and match the cached keys, not every field per row.
            var parsed = SearchQuery.Parse(query);
            foreach (var album in _library.Albums
                         .Where(parsed.MatchesAlbum)
                         .Take(MaxAlbumRows))
                Results.Add(RowForAlbum(album));
            foreach (var track in _library.Tracks
                         .Where(t => parsed.Matches(t))
                         .Take(MaxTrackRows))
                Results.Add(RowForTrack(track));
        }
        RaiseStateProperties();
    }

    /// <summary>Albums first, then songs, each by title (the stored keys are GUIDs, so their
    /// own order meant nothing).</summary>
    private IEnumerable<LyricsBackgroundPickItem> OverrideRows()
    {
        var albums = new List<LyricsBackgroundPickItem>();
        var tracks = new List<LyricsBackgroundPickItem>();
        foreach (var key in _settings.LyricsBackgroundOverrideKeys)
        {
            if (key.StartsWith("album:", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(key.AsSpan(6), "N", out var albumId))
            {
                var album = _library.Albums.FirstOrDefault(a => a.Id == albumId);
                if (album != null) albums.Add(RowForAlbum(album));
            }
            else if (key.StartsWith("track:", StringComparison.OrdinalIgnoreCase)
                     && Guid.TryParseExact(key.AsSpan(6), "N", out var trackId))
            {
                var track = _library.Tracks.FirstOrDefault(t => t.Id == trackId);
                if (track != null) tracks.Add(RowForTrack(track));
            }
        }
        return albums.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .Concat(tracks.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
    }

    private LyricsBackgroundPickItem RowForAlbum(Album album)
    {
        var row = new LyricsBackgroundPickItem
        {
            Key = LyricsBackgroundOverrides.KeyForAlbum(album),
            IsAlbum = true,
            Title = album.Name,
            Subtitle = Loc.T("LyricsVideo.AlbumBy", album.Artist),
            ArtworkPath = album.ArtworkPath,
        };
        ApplyRowState(row);
        return row;
    }

    private LyricsBackgroundPickItem RowForTrack(Track track)
    {
        var row = new LyricsBackgroundPickItem
        {
            Key = LyricsBackgroundOverrides.KeyForTrack(track),
            IsAlbum = false,
            Title = track.TitleDisplay,
            Subtitle = string.IsNullOrEmpty(track.Album) ? track.ArtistDisplay : $"{track.ArtistDisplay} · {track.Album}",
            ArtworkPath = track.AlbumArtworkPath,
            AlbumKey = track.AlbumId == Guid.Empty ? null : LyricsBackgroundOverrides.KeyForAlbumId(track.AlbumId),
        };
        ApplyRowState(row);
        return row;
    }

    private string VideoNameFor(string key)
    {
        var path = _settings.GetLyricsBackgroundOverridePath(key);
        return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
    }

    /// <summary>A row's state from the stored settings, by key: rows are rebuilt on every
    /// search, so a change must never be written to one row object and assumed visible.</summary>
    private void ApplyRowState(LyricsBackgroundPickItem row)
    {
        row.VideoName = VideoNameFor(row.Key);
        row.AlbumHasVideo = row.AlbumKey is { } album && _settings.HasLyricsBackgroundOverride(album);
        row.IsDownloading = IsDownloading && !_downloadForDefault
                            && string.Equals(row.Key, _downloadKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>After a clip changed: the overrides list (no search) gains or loses the row;
    /// a search keeps its rows and refreshes what they say (an album's clip changes what its
    /// songs say too).</summary>
    private void SyncRows()
    {
        if (!IsSearching)
        {
            RefreshResults();
            return;
        }
        foreach (var row in Results) ApplyRowState(row);
        RaiseStateProperties();
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowOverridesHeader));
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private void RefreshDefault()
    {
        var path = _settings.HasLyricsBackgroundMedia ? _settings.LyricsBackgroundMediaPath : string.Empty;
        DefaultVideoPath = path;
        DefaultVideoInfo = DescribeClip(path);
    }

    /// <summary>"MP4 · 12.4 MB"; empty for no clip.</summary>
    internal static string DescribeClip(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var kind = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        long bytes;
        try { bytes = new FileInfo(path).Length; }
        catch { return kind; }
        var size = bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : Math.Max(1, (int)Math.Round(bytes / 1024.0)).ToString(CultureInfo.CurrentCulture) + " KB";
        return kind.Length == 0 ? size : kind + " · " + size;
    }

    // ── Choose / remove ──

    [RelayCommand]
    private async Task ChooseDefaultAsync()
    {
        var path = await PickAsync();
        if (path is null) return;
        await SupersedeDownloadAsync(null);
        await _settings.SetLyricsBackgroundMediaAsync(path);
        RefreshDefault();
    }

    [RelayCommand]
    private async Task ClearDefaultAsync()
    {
        await SupersedeDownloadAsync(null);
        _settings.ClearLyricsBackgroundMediaCommand.Execute(null);
        RefreshDefault();
    }

    [RelayCommand]
    private async Task ChooseForItemAsync(LyricsBackgroundPickItem? item)
    {
        if (item is null) return;
        var path = await PickAsync();
        if (path is null) return;
        await SupersedeDownloadAsync(item.Key);
        await _settings.SetLyricsBackgroundOverrideAsync(item.Key, path);
        SyncRows();
    }

    /// <summary>Drops a row's own clip so it falls back to its album's or the default. The
    /// overrides list (no search) drops the row; a search keeps it with its new state.</summary>
    [RelayCommand]
    private async Task ClearForItemAsync(LyricsBackgroundPickItem? item)
    {
        if (item is null) return;
        await SupersedeDownloadAsync(item.Key);
        _settings.ClearLyricsBackgroundOverride(item.Key);
        SyncRows();
    }

    /// <summary>
    /// The user just set or removed the clip of the target a YouTube download is for: their
    /// choice wins. Before the file lands the download is cancelled; while its (local) copy
    /// is already being saved, wait for that, so this choice replaces it rather than racing it.
    /// </summary>
    private async Task SupersedeDownloadAsync(string? key)
    {
        if (!IsDownloading || _downloadCts is null) return;
        var same = key is null
            ? _downloadForDefault
            : !_downloadForDefault && string.Equals(key, _downloadKey, StringComparison.OrdinalIgnoreCase);
        if (!same) return;
        _downloadCts.Cancel();
        try { await _applying; } catch { /* the download reports its own failure */ }
    }

    [RelayCommand]
    private void Close()
    {
        CancelForClose();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The dialog is closing, by any route (Close, Esc, Alt+F4, the owner going
    /// away): stop a running download or yt-dlp install. A clip already being copied into
    /// place (a short local copy) still finishes.</summary>
    public void CancelForClose()
    {
        _searchDebounceCts?.Cancel();
        _downloadCts?.Cancel();
        _lifetimeCts.Cancel();
    }

    // ── From YouTube ──

    [RelayCommand]
    private void YouTubeForDefault() => OpenYouTube(null);

    [RelayCommand]
    private void YouTubeForItem(LyricsBackgroundPickItem? item) => OpenYouTube(item);

    private static bool SameTarget(LyricsBackgroundPickItem? a, LyricsBackgroundPickItem? b)
        => a is null ? b is null : b is not null && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    private void OpenYouTube(LyricsBackgroundPickItem? target)
    {
        if (!CanUseYouTube) return;
        // While a download runs the panel stays on the target it will land on: retargeting
        // the title to another row would say the clip goes somewhere it doesn't.
        if (IsDownloading)
        {
            ShowYouTube = true;
            return;
        }
        // Same target again folds the panel; a different one retargets it. By key: a search
        // rebuilds the rows, so the row object behind an open panel may be an old one.
        if (ShowYouTube && SameTarget(YouTubeTarget, target)) { ShowYouTube = false; return; }
        YouTubeTarget = target;
        YouTubeStatus = string.Empty;
        ShowYouTube = true;
        if (!_toolUpdateChecked)
        {
            _toolUpdateChecked = true;
            _ = CheckToolUpdateAsync();
        }
    }

    /// <summary>First use of the YouTube panel: show the version, then the quiet session update check.</summary>
    private async Task CheckToolUpdateAsync()
    {
        if (_ytDlp is null) return;
        try
        {
            await RefreshToolVersionAsync();
            if (!ToolInstalled) return;
            await _ytDlp.EnsureSessionUpdateCheckAsync();
            await RefreshToolVersionAsync();
        }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.State, "YtDlp.PanelCheckFailed", ex.Message); }
    }

    private async Task RefreshToolVersionAsync()
    {
        if (_ytDlp is null || !ToolInstalled) { ToolVersionText = string.Empty; ToolUpdateAvailable = false; return; }
        var version = await _ytDlp.GetVersionAsync(CancellationToken.None);
        ToolVersionText = YtDlpParsing.VersionLabel(version, _ytDlp.LatestKnownVersion);
        ToolUpdateAvailable = !string.IsNullOrWhiteSpace(version) && YtDlpParsing.IsNewer(_ytDlp.LatestKnownVersion, version);
    }

    /// <summary>Cancel in the YouTube panel: stops a running download (not one already being
    /// saved), otherwise folds the panel.</summary>
    [RelayCommand]
    private void CloseYouTube()
    {
        if (IsDownloading)
        {
            if (!IsApplying) _downloadCts?.Cancel();
            return;
        }
        ShowYouTube = false;
    }

    [RelayCommand]
    private async Task InstallToolAsync()
    {
        if (_ytDlp is null || IsInstallingTool || IsDownloading) return;
        IsInstallingTool = true;
        InstallProgress = 0;
        YouTubeStatus = Loc.T("LyricsVideo.InstallingTool");
        try
        {
            await _ytDlp.InstallAsync(new Progress<double>(p => Dispatcher.UIThread.Post(() => InstallProgress = p)), _lifetimeCts.Token);
            ToolInstalled = true;
            YouTubeStatus = string.Empty;
            await RefreshToolVersionAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            YouTubeStatus = string.Empty; // closed mid-install; YtDlpTool removed its partial file
        }
        catch (Exception ex)
        {
            YouTubeStatus = Loc.T("LyricsVideo.InstallFailed", ex.Message);
        }
        finally { IsInstallingTool = false; }
    }

    [RelayCommand]
    private async Task DownloadFromYouTubeAsync()
    {
        if (_ytDlp is null || IsDownloading) return;
        var url = (YouTubeUrl ?? string.Empty).Trim();
        if (!YtDlpParsing.LooksLikeYouTubeUrl(url)) { YouTubeStatus = Loc.T("LyricsVideo.PasteLinkFirst"); return; }
        if (!ToolInstalled) { YouTubeStatus = Loc.T("LyricsVideo.InstallFirst"); return; }

        var target = YouTubeTarget;
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadForDefault = target is null;
        _downloadKey = target?.Key;
        IsDownloading = true;
        DownloadProgress = 0;
        YouTubeStatus = Loc.T("LyricsVideo.Downloading");
        foreach (var row in Results) ApplyRowState(row);

        // A folder of this run's own: whatever yt-dlp leaves in it — a file it still held when
        // the cancel landed — goes with it, and nothing another run owns does.
        var runDir = Path.Combine(ScratchRoot, "run-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(runDir);
            string? ffmpeg = null;
            try { ffmpeg = _ffmpegPath?.Invoke(); } catch { }
            var produced = await _ytDlp.DownloadVideoAsync(url, runDir, ffmpeg, SelectedQuality.MaxHeight,
                new Progress<double>(p => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_downloadCts, cts)) DownloadProgress = p; })),
                cts.Token,
                status => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_downloadCts, cts)) return;
                    DownloadProgress = 0;
                    YouTubeStatus = status;
                }));

            // yt-dlp can exit 0 the moment Cancel, Remove or closing lands: the process was
            // done, so neither the kill nor the token stopped anything. The cancel still wins.
            cts.Token.ThrowIfCancellationRequested();

            IsApplying = true;
            YouTubeStatus = Loc.T("LyricsVideo.Saving");
            _applying = target is null
                ? _settings.SetLyricsBackgroundMediaAsync(produced)
                : _settings.SetLyricsBackgroundOverrideAsync(target.Key, produced);
            await _applying;

            if (target is null) RefreshDefault();
            YouTubeStatus = string.Empty;
            YouTubeUrl = string.Empty;
            ShowYouTube = false;
        }
        catch (OperationCanceledException)
        {
            YouTubeStatus = Loc.T("LyricsVideo.Cancelled");
        }
        catch (Exception ex)
        {
            YouTubeStatus = Loc.T("LyricsVideo.DownloadFailed", ex.Message);
        }
        finally
        {
            IsApplying = false;
            IsDownloading = false;
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts = null;
                _downloadKey = null;
                _downloadForDefault = false;
            }
            _applying = Task.CompletedTask;
            SyncRows();
            Cleanup = DeleteRunFolderAsync(runDir);
            _ = RefreshToolVersionAsync(); // a blocked download may have updated yt-dlp
        }
    }

    /// <summary>Removes a download's folder. A cancel kills yt-dlp, but Process.Kill returns
    /// before the process (and the ffmpeg it runs) has let go of its files, and YtDlpTool's one
    /// delete attempt then fails quietly; so this retries for a few seconds.</summary>
    private static Task DeleteRunFolderAsync(string dir) => Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100);
        }
        DebugLogger.Warn(DebugLogger.Category.State, "LyricsBackground.ScratchLeft", dir);
    });

    private async Task<string?> PickAsync()
    {
        if (PickFile is null) return null;
        try
        {
            var path = await PickFile();
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsBackground] pick failed: {ex.Message}");
            return null;
        }
    }
}
