using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Noctis Wrap dialog: yearly/monthly recap from the play log,
/// random test data for previewing, and share-card export.
/// </summary>
public partial class WrapViewModel : ViewModelBase
{
    private readonly IPlayHistoryService _playHistory;
    private readonly ILibraryService _library;
    private WrapStats _stats = new();
    private int _renderGeneration;

    [ObservableProperty] private bool _isYearMode = true;
    [ObservableProperty] private bool _isMonthMode;
    [ObservableProperty] private bool _isTestData;

    [ObservableProperty] private string _periodLabel = "";
    [ObservableProperty] private string _totalPlaysText = "0";
    [ObservableProperty] private string _totalMinutesText = "0";
    [ObservableProperty] private string _uniqueTracksText = "0";
    [ObservableProperty] private string _uniqueArtistsText = "0";
    [ObservableProperty] private string _uniqueAlbumsText = "0";
    [ObservableProperty] private string _losslessText = "0%";
    [ObservableProperty] private string _hiResText = "0%";
    [ObservableProperty] private string _topGenreText = "—";
    [ObservableProperty] private bool _hasData;

    [ObservableProperty] private List<WrapEntry> _topTracks = new();
    [ObservableProperty] private List<WrapEntry> _topArtists = new();
    [ObservableProperty] private List<WrapEntry> _topAlbums = new();
    [ObservableProperty] private List<WrapEntry> _topGenres = new();

    // Share card export
    [ObservableProperty] private bool _isSquare = true;
    [ObservableProperty] private bool _isStory;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Last rendered PNG — what Save/Copy exports.</summary>
    public byte[]? CurrentPng { get; private set; }

    public string SuggestedFileName => $"Noctis Wrap {_stats.PeriodLabel}.png";

    public WrapViewModel(IPlayHistoryService playHistory, ILibraryService library)
    {
        _playHistory = playHistory;
        _library = library;
        LoadReal();
    }

    private void LoadReal()
    {
        var now = DateTime.Now;
        var tracksById = new Dictionary<Guid, Models.Track>();
        foreach (var t in _library.Tracks)
            tracksById[t.Id] = t;

        var stats = WrapStatsBuilder.Build(
            _playHistory.Events, tracksById, now.Year, IsMonthMode ? now.Month : null);
        IsTestData = false;
        Apply(stats);
    }

    private void Apply(WrapStats stats)
    {
        _stats = stats;
        PeriodLabel = stats.PeriodLabel;
        TotalPlaysText = stats.TotalPlays.ToString("N0");
        TotalMinutesText = stats.TotalMinutes.ToString("N0");
        UniqueTracksText = stats.UniqueTracks.ToString("N0");
        UniqueArtistsText = stats.UniqueArtists.ToString("N0");
        UniqueAlbumsText = stats.UniqueAlbums.ToString("N0");
        LosslessText = $"{stats.LosslessPercent:0}%";
        HiResText = $"{stats.HiResPercent:0}%";
        TopGenreText = stats.TopGenre;
        TopTracks = stats.TopTracks.ToList();
        TopArtists = stats.TopArtists.ToList();
        TopAlbums = stats.TopAlbums.ToList();
        TopGenres = stats.TopGenres.ToList();
        HasData = stats.TotalPlays > 0;
        StatusText = string.Empty;
        RefreshPreview();
    }

    partial void OnIsYearModeChanged(bool value)
    {
        if (value) { IsMonthMode = false; LoadReal(); }
    }

    partial void OnIsMonthModeChanged(bool value)
    {
        if (value) { IsYearMode = false; LoadReal(); }
    }

    [RelayCommand]
    private void SelectYear() => IsYearMode = true;

    [RelayCommand]
    private void SelectMonth() => IsMonthMode = true;

    /// <summary>Fills the dialog with random placeholder stats for UI/card tuning.</summary>
    [RelayCommand]
    private void UseTestData()
    {
        IsTestData = true;
        Apply(WrapStatsBuilder.BuildTestData());
    }

    [RelayCommand]
    private void UseRealData() => LoadReal();

    partial void OnIsSquareChanged(bool value)
    {
        if (value) { IsStory = false; RefreshPreview(); }
    }

    partial void OnIsStoryChanged(bool value)
    {
        if (value) { IsSquare = false; RefreshPreview(); }
    }

    [RelayCommand]
    private void SelectSquare() => IsSquare = true;

    [RelayCommand]
    private void SelectStory() => IsStory = true;

    private void RefreshPreview()
    {
        var generation = ++_renderGeneration;
        if (!HasData)
        {
            CurrentPng = null;
            var stale = Preview;
            Preview = null;
            stale?.Dispose();
            return;
        }

        var spec = new WrapCardSpec
        {
            PeriodLabel = _stats.PeriodLabel,
            TopArtists = _stats.TopArtists.Select(e => e.Name).ToList(),
            TopTracks = _stats.TopTracks.Select(e => e.Name).ToList(),
            TotalMinutes = _stats.TotalMinutes,
            TotalPlays = _stats.TotalPlays,
            LosslessPercent = _stats.LosslessPercent,
            TopGenre = _stats.TopGenre,
            ArtworkPath = _stats.TopAlbumArtworkPath,
            Format = IsStory ? ShareCardFormat.Story : ShareCardFormat.Square,
        };

        Task.Run(() =>
        {
            try
            {
                var png = ShareCardRenderer.RenderWrapCard(spec);
                using var ms = new MemoryStream(png);
                var bitmap = new Bitmap(ms);
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _renderGeneration)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    var old = Preview;
                    CurrentPng = png;
                    Preview = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.Category.UI, DebugLogger.Level.Error,
                    "Wrap card render failed", ex.Message);
            }
        });
    }

    public void ReportStatus(string message) => StatusText = message;
}
