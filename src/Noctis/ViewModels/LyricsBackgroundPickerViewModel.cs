using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
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

    /// <summary>File name of the item's own clip, or empty when it uses the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnVideo))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _videoName = string.Empty;

    public bool HasOwnVideo => !string.IsNullOrEmpty(VideoName);
    /// <summary>The stored clip is named after the key (album_&lt;id&gt;.mp4), which means nothing
    /// to the user (09-19), so the row says only whether it has its own clip.</summary>
    public string StatusText => Localization.Loc.T(HasOwnVideo ? "LyricsBackground.OwnVideo" : "LyricsBackground.UsesDefault");
}

/// <summary>
/// Settings › Appearance › Lyrics Background Video › Modify (user ask 2026-09-07). One place
/// for the default clip and for the songs and albums that carry their own: search the
/// library, give any row a video, or send it back to the default. Writes go straight
/// through <see cref="SettingsViewModel"/> so the lyrics page follows immediately.
/// </summary>
public partial class LyricsBackgroundPickerViewModel : ObservableObject
{
    private const int MaxAlbumRows = 20;
    private const int MaxTrackRows = 80;

    private readonly SettingsViewModel _settings;
    private readonly ILibraryService _library;
    private readonly YtDlpTool? _ytDlp;
    private readonly Func<string?>? _ffmpegPath;
    private CancellationTokenSource? _downloadCts;

    public static readonly IReadOnlyList<BackdropQuality> Qualities = new[]
    {
        new BackdropQuality("Best available", 0),
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultVideo))]
    private string _defaultVideoName = string.Empty;

    public bool HasDefaultVideo => !string.IsNullOrEmpty(DefaultVideoName);

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
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _youTubeStatus = string.Empty;
    [ObservableProperty] private bool _toolInstalled;
    [ObservableProperty] private bool _isInstallingTool;
    /// <summary>"yt-dlp 2026.08.19" (+ "update available"), small secondary text in the YouTube panel.</summary>
    [ObservableProperty] private string _toolVersionText = string.Empty;
    private bool _toolUpdateChecked;
    /// <summary>Row the download is for; null = the default video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeTargetLabel))]
    private LyricsBackgroundPickItem? _youTubeTarget;

    public IReadOnlyList<BackdropQuality> QualityOptions => Qualities;
    public bool CanUseYouTube => _ytDlp is not null;
    public string YouTubeTargetLabel => YouTubeTarget is { } t ? t.Title : Localization.Loc.T("LyricsBackground.DefaultVideo");

    public LyricsBackgroundPickerViewModel(SettingsViewModel settings, ILibraryService library,
        YtDlpTool? ytDlp = null, Func<string?>? ffmpegPath = null)
    {
        _settings = settings;
        _library = library;
        _ytDlp = ytDlp;
        _ffmpegPath = ffmpegPath;
        ToolInstalled = ytDlp?.IsAvailable == true;
        DefaultVideoName = settings.HasLyricsBackgroundMedia ? settings.LyricsBackgroundMediaName : string.Empty;
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
            var queryKey = Noctis.Helpers.SearchText.Normalize(query);
            foreach (var album in _library.Albums
                         .Where(a => Noctis.Helpers.SearchText.Matches(a.Name, a.SearchNameKey, query, queryKey)
                                  || Noctis.Helpers.SearchText.Matches(a.Artist, a.SearchArtistKey, query, queryKey))
                         .Take(MaxAlbumRows))
                Results.Add(RowForAlbum(album));
            foreach (var track in _library.Tracks
                         .Where(t => PlaylistViewModel.MatchesSearch(t, query, queryKey))
                         .Take(MaxTrackRows))
                Results.Add(RowForTrack(track));
        }
        RaiseStateProperties();
    }

    private IEnumerable<LyricsBackgroundPickItem> OverrideRows()
    {
        var albums = _library.Albums;
        var tracks = _library.Tracks;
        foreach (var key in _settings.LyricsBackgroundOverrideKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (key.StartsWith("album:", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(key.AsSpan(6), "N", out var albumId))
            {
                var album = albums.FirstOrDefault(a => a.Id == albumId);
                if (album != null) yield return RowForAlbum(album);
            }
            else if (key.StartsWith("track:", StringComparison.OrdinalIgnoreCase)
                     && Guid.TryParseExact(key.AsSpan(6), "N", out var trackId))
            {
                var track = tracks.FirstOrDefault(t => t.Id == trackId);
                if (track != null) yield return RowForTrack(track);
            }
        }
    }

    private LyricsBackgroundPickItem RowForAlbum(Album album)
    {
        var key = LyricsBackgroundOverrides.KeyForAlbum(album);
        return new LyricsBackgroundPickItem
        {
            Key = key,
            IsAlbum = true,
            Title = album.Name,
            Subtitle = album.Artist,
            ArtworkPath = album.ArtworkPath,
            VideoName = VideoNameFor(key),
        };
    }

    private LyricsBackgroundPickItem RowForTrack(Track track)
    {
        var key = LyricsBackgroundOverrides.KeyForTrack(track);
        return new LyricsBackgroundPickItem
        {
            Key = key,
            IsAlbum = false,
            Title = track.TitleDisplay,
            Subtitle = string.IsNullOrEmpty(track.Album) ? track.ArtistDisplay : $"{track.ArtistDisplay} · {track.Album}",
            ArtworkPath = track.AlbumArtworkPath,
            VideoName = VideoNameFor(key),
        };
    }

    private string VideoNameFor(string key)
    {
        var path = _settings.GetLyricsBackgroundOverridePath(key);
        return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowOverridesHeader));
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    [RelayCommand]
    private async Task ChooseDefaultAsync()
    {
        var path = await PickAsync();
        if (path is null) return;
        await _settings.SetLyricsBackgroundMediaAsync(path);
        DefaultVideoName = _settings.HasLyricsBackgroundMedia ? _settings.LyricsBackgroundMediaName : string.Empty;
    }

    [RelayCommand]
    private void ClearDefault()
    {
        _settings.ClearLyricsBackgroundMediaCommand.Execute(null);
        DefaultVideoName = string.Empty;
    }

    [RelayCommand]
    private async Task ChooseForItemAsync(LyricsBackgroundPickItem? item)
    {
        if (item is null) return;
        var path = await PickAsync();
        if (path is null) return;
        await _settings.SetLyricsBackgroundOverrideAsync(item.Key, path);
        item.VideoName = VideoNameFor(item.Key);
        RaiseStateProperties();
    }

    [RelayCommand]
    private void ClearForItem(LyricsBackgroundPickItem? item)
    {
        if (item is null) return;
        _settings.ClearLyricsBackgroundOverride(item.Key);
        item.VideoName = string.Empty;
        // The overrides list (no search) drops the row; a search keeps it with "Uses default".
        if (!IsSearching) Results.Remove(item);
        RaiseStateProperties();
    }

    [RelayCommand]
    private void Close()
    {
        _downloadCts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void YouTubeForDefault() => OpenYouTube(null);

    [RelayCommand]
    private void YouTubeForItem(LyricsBackgroundPickItem? item) => OpenYouTube(item);

    private void OpenYouTube(LyricsBackgroundPickItem? target)
    {
        if (!CanUseYouTube) return;
        // Same target again folds the panel; a different one retargets it.
        if (ShowYouTube && ReferenceEquals(YouTubeTarget, target) && !IsDownloading) { ShowYouTube = false; return; }
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
        if (_ytDlp is null || !ToolInstalled) { ToolVersionText = string.Empty; return; }
        var version = await _ytDlp.GetVersionAsync(CancellationToken.None);
        ToolVersionText = YtDlpParsing.VersionLabel(version, _ytDlp.LatestKnownVersion);
    }

    [RelayCommand]
    private void CloseYouTube()
    {
        if (IsDownloading) { _downloadCts?.Cancel(); return; }
        ShowYouTube = false;
    }

    [RelayCommand]
    private async Task InstallToolAsync()
    {
        if (_ytDlp is null || IsInstallingTool) return;
        IsInstallingTool = true;
        DownloadProgress = 0;
        YouTubeStatus = "Downloading yt-dlp…";
        try
        {
            await _ytDlp.InstallAsync(new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p)), CancellationToken.None);
            ToolInstalled = true;
            YouTubeStatus = string.Empty;
            await RefreshToolVersionAsync();
        }
        catch (Exception ex)
        {
            YouTubeStatus = $"Couldn't install yt-dlp — {ex.Message}";
        }
        finally { IsInstallingTool = false; }
    }

    [RelayCommand]
    private async Task DownloadFromYouTubeAsync()
    {
        if (_ytDlp is null || IsDownloading) return;
        var url = (YouTubeUrl ?? string.Empty).Trim();
        if (!YtDlpParsing.LooksLikeYouTubeUrl(url)) { YouTubeStatus = "Paste a YouTube link first."; return; }
        if (!ToolInstalled) { YouTubeStatus = "Install yt-dlp first."; return; }

        var target = YouTubeTarget;
        var cts = _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        YouTubeStatus = "Downloading…";
        string? produced = null;
        try
        {
            var scratchRoot = Path.Combine(Path.GetTempPath(), "noctis-backdrops");
            Directory.CreateDirectory(scratchRoot);
            string? ffmpeg = null;
            try { ffmpeg = _ffmpegPath?.Invoke(); } catch { }
            produced = await _ytDlp.DownloadVideoAsync(url, scratchRoot, ffmpeg, SelectedQuality.MaxHeight,
                new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p)), cts.Token,
                status => Dispatcher.UIThread.Post(() => { DownloadProgress = 0; YouTubeStatus = status; }));

            if (target is null)
            {
                await _settings.SetLyricsBackgroundMediaAsync(produced);
                DefaultVideoName = _settings.HasLyricsBackgroundMedia ? _settings.LyricsBackgroundMediaName : string.Empty;
            }
            else
            {
                await _settings.SetLyricsBackgroundOverrideAsync(target.Key, produced);
                target.VideoName = VideoNameFor(target.Key);
                RaiseStateProperties();
            }
            YouTubeStatus = string.Empty;
            YouTubeUrl = string.Empty;
            ShowYouTube = false;
        }
        catch (OperationCanceledException)
        {
            YouTubeStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            YouTubeStatus = $"Download failed — {ex.Message}";
        }
        finally
        {
            if (produced is not null) YtDlpTool.CleanupScratch(produced);
            IsDownloading = false;
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            _ = RefreshToolVersionAsync(); // a blocked download may have updated yt-dlp

        }
    }

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
