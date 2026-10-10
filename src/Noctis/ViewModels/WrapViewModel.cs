using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the Noctis Wrap dialog: a Last.fm-report-style yearly/monthly recap from the play
/// log (top album hero, cover grid, artist portraits, listening clock, timeline, comparison
/// with the previous period) and the share card.
///
/// Everything heavy runs off the UI thread: the archive check, the track index and each
/// period's build happen on the thread pool and land through <see cref="Loading"/>, so a
/// large play log never stalls the dialog's open animation.
/// </summary>
public partial class WrapViewModel : ViewModelBase, IDisposable
{
    private readonly IPlayHistoryService _playHistory;
    private readonly ILibraryService _library;
    private readonly IWrapArchiveService _archive;
    private readonly Func<string, string?> _artistImageLookup;
    private readonly int _currentYear = DateTime.Now.Year;
    private readonly IReadOnlyList<Models.PlayHistoryEvent> _events;
    private readonly IReadOnlyList<Models.Track> _tracks;
    private Dictionary<Guid, Models.Track> _tracksById = new();
    private readonly Task _initialized;
    private WrapStats _stats = new();
    private int _loadGeneration;
    private bool _disposed;

    [ObservableProperty] private List<int> _availableYears = new();
    [ObservableProperty] private int _selectedYear;
    [ObservableProperty] private bool _isYearMode = true;
    [ObservableProperty] private bool _isMonthMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSpinner), nameof(ShowEmpty))]
    private bool _isLoading = true;

    /// <summary>True when the live current year is selected (enables the month toggle).</summary>
    public bool IsCurrentYear => SelectedYear == _currentYear;

    [ObservableProperty] private string _periodLabel = "";
    /// <summary>Under the title: what the recap covers ("8,470 plays · recorded since Jun 11").</summary>
    [ObservableProperty] private string _coverageText = "";
    [ObservableProperty] private string _totalPlaysText = "0";
    [ObservableProperty] private string _totalMinutesText = "0";
    [ObservableProperty] private string _uniqueTracksText = "0";
    [ObservableProperty] private string _uniqueArtistsText = "0";
    [ObservableProperty] private string _newArtistsText = "";
    [ObservableProperty] private string _uniqueAlbumsText = "0";
    [ObservableProperty] private string _losslessText = "0%";
    [ObservableProperty] private string _hiResText = "0%";
    [ObservableProperty] private string _topGenreText = "—";
    [ObservableProperty] private string _dailyAverageText = "";
    [ObservableProperty] private string _activeDaysText = "";
    [ObservableProperty] private string _longestStreakText = "";
    [ObservableProperty] private string _longestStreakRangeText = "";
    [ObservableProperty] private string _mostActiveDayText = "";
    [ObservableProperty] private string _mostActiveDayDetailText = "";
    [ObservableProperty] private string _minutesDeltaText = "";
    [ObservableProperty] private bool _isMinutesDeltaDown;
    [ObservableProperty] private string _playsDeltaText = "";
    [ObservableProperty] private bool _isPlaysDeltaDown;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSpinner), nameof(ShowEmpty))]
    private bool _hasData;

    /// <summary>First build still running: a spinner where the report goes.</summary>
    public bool ShowSpinner => IsLoading && !HasData;
    /// <summary>Built, and nothing was played in the period.</summary>
    public bool ShowEmpty => !IsLoading && !HasData;

    // Hero: the most-played album.
    [ObservableProperty] private WrapEntry? _topAlbum;

    [ObservableProperty] private List<WrapEntry> _topTracks = new();
    [ObservableProperty] private List<WrapEntry> _topArtists = new();
    [ObservableProperty] private List<WrapEntry> _topAlbums = new();
    [ObservableProperty] private List<WrapEntry> _topGenres = new();

    // Charts
    [ObservableProperty] private IReadOnlyList<int> _clockValues = Array.Empty<int>();
    [ObservableProperty] private string _peakHourText = "";
    [ObservableProperty] private IReadOnlyList<int> _timelineValues = Array.Empty<int>();
    [ObservableProperty] private IReadOnlyList<string> _timelineLabels = Array.Empty<string>();
    [ObservableProperty] private int _timelineHighlight = -1;
    [ObservableProperty] private string _timelineTitle = "";
    [ObservableProperty] private bool _hasCharts;

    // Share card export
    [ObservableProperty] private bool _isSquare = true;
    [ObservableProperty] private bool _isStory;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Last rendered PNG — what Save/Copy exports.</summary>
    public byte[]? CurrentPng { get; private set; }

    /// <summary>The build in flight for the current selection (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The stats on screen.</summary>
    public WrapStats Stats => _stats;

    public string SuggestedFileName => $"Noctis Wrap {_stats.PeriodLabel}.png";

    public WrapViewModel(IPlayHistoryService playHistory, ILibraryService library,
        IWrapArchiveService? archive = null, Func<string, string?>? artistImageLookup = null)
    {
        _playHistory = playHistory;
        _library = library;
        _archive = archive ?? new WrapArchiveService();
        _artistImageLookup = artistImageLookup ?? CachedArtistImage;

        // Snapshots only (both are swapped, never mutated, by their services): the reads
        // below happen on the thread pool.
        _events = _playHistory.Events;
        _tracks = _library.Tracks;

        AvailableYears = new List<int> { _currentYear };
        _selectedYear = _currentYear;
        _initialized = Task.Run(Initialize);
        Loading = LoadAsync();
    }

    /// <summary>Track index, plus freezing finished years before the 10k-event play log
    /// trims them away. Thread pool.</summary>
    private void Initialize()
    {
        var byId = new Dictionary<Guid, Models.Track>(_tracks.Count);
        foreach (var t in _tracks)
            byId[t.Id] = t;
        _tracksById = byId;
        _archive.EnsureArchived(_events, _tracksById, _currentYear);
    }

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        IsLoading = true;
        var year = SelectedYear;
        var month = year == _currentYear && IsMonthMode ? DateTime.Now.Month : (int?)null;

        WrapStats stats;
        IReadOnlyList<int>? archivedYears = null;
        try
        {
            await _initialized;
            (stats, archivedYears) = await Task.Run(() =>
            {
                var built = year == _currentYear
                    ? WrapStatsBuilder.Build(_events, _tracksById, year, month)
                    : _archive.GetYear(year) ?? new WrapStats { PeriodLabel = year.ToString() };
                Decorate(built);
                return (built, _archive.ArchivedYears);
            });
        }
        catch (Exception ex)
        {
            DebugLogger.Log(DebugLogger.Category.UI, DebugLogger.Level.Error, "Wrap build failed", ex.Message);
            stats = new WrapStats { PeriodLabel = year.ToString() };
        }

        if (_disposed || generation != _loadGeneration) return;
        if (archivedYears != null)
        {
            var years = new List<int> { _currentYear };
            years.AddRange(archivedYears.Where(y => y != _currentYear));
            if (!years.SequenceEqual(AvailableYears))
            {
                AvailableYears = years;
                // A new ItemsSource can drop the drop-down's selection; hand it the year again.
                OnPropertyChanged(nameof(SelectedYear));
            }
        }
        Apply(stats, partial: year != _currentYear && !_archive.IsYearComplete(year));
        IsLoading = false;
    }

    /// <summary>Display-only extras: artist portraits from the app's cache (file checks, so
    /// off the UI thread) and the genre bars' lengths.</summary>
    private void Decorate(WrapStats stats)
    {
        foreach (var artist in stats.TopArtists)
        {
            try { artist.ImagePath = _artistImageLookup(artist.Name); }
            catch { artist.ImagePath = null; }
        }
        var maxGenre = stats.TopGenres.Count > 0 ? stats.TopGenres.Max(g => g.Plays) : 0;
        foreach (var genre in stats.TopGenres)
            genre.BarFraction = maxGenre > 0 ? genre.Plays / (double)maxGenre : 0;
    }

    /// <summary>
    /// The portrait the Artists page and Home's Top Artists show: ArtistImageService's cache,
    /// keyed by the artist id (custom pictures included). Nothing is downloaded here.
    /// </summary>
    private static string? CachedArtistImage(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (App.Services?.GetService(typeof(ArtistImageService)) is not ArtistImageService images) return null;
        var id = LibraryService.ComputeArtistId(name);
        if (images.IsImageRemoved(id)) return null;
        var path = images.GetCachedImagePath(id);
        return File.Exists(path) ? path : null;
    }

    private void Apply(WrapStats stats, bool partial = false)
    {
        var culture = Loc.Instance.Culture;
        _stats = stats;
        PeriodLabel = stats.PeriodLabel;
        HasData = stats.TotalPlays > 0;

        TotalPlaysText = stats.TotalPlays.ToString("N0", culture);
        TotalMinutesText = stats.TotalMinutes.ToString("N0", culture);
        UniqueTracksText = stats.UniqueTracks.ToString("N0", culture);
        UniqueArtistsText = stats.UniqueArtists.ToString("N0", culture);
        NewArtistsText = stats.NewArtists is int n && n > 0
            ? Loc.T("Wrap.NewCount", n.ToString("N0", culture))
            : string.Empty;
        UniqueAlbumsText = stats.UniqueAlbums.ToString("N0", culture);
        LosslessText = $"{stats.LosslessPercent:0}%";
        HiResText = $"{stats.HiResPercent:0}%";
        TopGenreText = stats.TopGenre;

        var coverage = new List<string>();
        if (stats.RecordedSince is { } since)
            coverage.Add(Loc.T("Wrap.RecordedSince", since.ToString("MMM d", culture)));
        if (stats.ShortPlays > 0)
            coverage.Add(stats.ShortPlays == 1
                ? Loc.T("Wrap.OneShortPlayLeftOut")
                : Loc.T("Wrap.ShortPlaysLeftOut", stats.ShortPlays.ToString("N0", culture)));
        if (partial)
            coverage.Add(Loc.T("Wrap.PartialYear"));
        CoverageText = string.Join(" · ", coverage);

        DailyAverageText = stats.DaysCovered > 0
            ? Loc.T("Wrap.MinutesShort", Math.Round(stats.AverageMinutesPerDay).ToString("N0", culture))
            : "—";
        ActiveDaysText = stats.DaysCovered > 0
            ? Loc.T("Wrap.ActiveDaysOf", stats.ActiveDays.ToString("N0", culture), stats.DaysCovered.ToString("N0", culture))
            : "—";
        LongestStreakText = stats.LongestStreakDays > 0 ? FormatDays(stats.LongestStreakDays) : "—";
        LongestStreakRangeText = stats.LongestStreakStart is { } streakStart && stats.LongestStreakDays > 1
            ? $"{streakStart.ToString("MMM d", culture)} – {streakStart.AddDays(stats.LongestStreakDays - 1).ToString("MMM d", culture)}"
            : string.Empty;
        MostActiveDayText = stats.MostActiveDay is { } busiest ? busiest.ToString("MMM d", culture) : "—";
        MostActiveDayDetailText = stats.MostActiveDay != null
            ? $"{WrapStatsBuilder.FormatPlays(stats.MostActiveDayPlays)} · {Loc.T("Wrap.MinutesShort", stats.MostActiveDayMinutes.ToString("N0", culture))}"
            : string.Empty;

        (MinutesDeltaText, IsMinutesDeltaDown) = Delta(stats.TotalMinutes, stats.Previous?.Minutes, stats, culture);
        (PlaysDeltaText, IsPlaysDeltaDown) = Delta(stats.TotalPlays, stats.Previous?.Plays, stats, culture);

        TopAlbum = stats.TopAlbums.Count > 0 ? stats.TopAlbums[0] : null;
        TopTracks = stats.TopTracks.ToList();
        TopArtists = stats.TopArtists.ToList();
        TopAlbums = stats.TopAlbums.ToList();
        TopGenres = stats.TopGenres.ToList();

        var clock = stats.PlaysByHour is { Length: 24 } hours ? hours : new int[24];
        ClockValues = clock;
        var peak = clock.Max() > 0 ? Array.IndexOf(clock, clock.Max()) : -1;
        PeakHourText = peak >= 0
            ? Loc.T("Wrap.PeakHour", DateTime.Today.AddHours(peak).ToString("t", culture))
            : string.Empty;

        TimelineValues = stats.Timeline.Select(b => b.Plays).ToList();
        TimelineLabels = stats.Timeline
            .Select((b, i) => stats.IsMonth
                ? (i == 0 || (i + 1) % 5 == 0 ? b.Start.Day.ToString(culture) : string.Empty)
                : culture.DateTimeFormat.GetAbbreviatedMonthName(b.Start.Month))
            .ToList();
        TimelineHighlight = stats.Timeline.Count > 0 && stats.Timeline.Any(b => b.Plays > 0)
            ? stats.Timeline.Select((b, i) => (b.Plays, i)).MaxBy(x => x.Plays).i
            : -1;
        TimelineTitle = Loc.T(stats.IsMonth ? "Wrap.PlaysPerDay" : "Wrap.PlaysPerMonth");
        HasCharts = HasData && (clock.Any(v => v > 0) || stats.Timeline.Count > 0);

        StatusText = string.Empty;
        InvalidateCards();
        RefreshPreview();
    }

    private string FormatDays(int days) =>
        days == 1 ? Loc.T("Wrap.OneDay") : Loc.T("Wrap.DaysCount", days.ToString("N0", Loc.Instance.Culture));

    /// <summary>"+12% vs Sep 1–9": the change against the same stretch of the previous period.
    /// Empty when there is nothing honest to compare (no data back then, or none at all).</summary>
    private static (string Text, bool Down) Delta(long current, long? previous, WrapStats stats, CultureInfo culture)
    {
        if (previous is not > 0 || stats.Previous is not { } prev) return (string.Empty, false);
        var change = (current - previous.Value) * 100.0 / previous.Value;
        var label = PreviousLabel(prev, stats.IsMonth, culture);
        var rounded = Math.Round(change);
        if (rounded == 0) return (Loc.T("Wrap.SameAs", label), false);
        var sign = rounded > 0 ? "+" : "−";
        return (Loc.T("Wrap.DeltaVs", $"{sign}{Math.Abs(rounded).ToString("N0", culture)}%", label), rounded < 0);
    }

    private static string PreviousLabel(WrapComparison prev, bool isMonth, CultureInfo culture)
    {
        var lastDay = prev.End.AddDays(-1);
        var whole = isMonth ? prev.End == prev.Start.AddMonths(1) : prev.End == prev.Start.AddYears(1);
        if (whole)
            return isMonth ? prev.Start.ToString("MMMM", culture) : prev.Start.Year.ToString(culture);
        return isMonth
            ? $"{prev.Start.ToString("MMM d", culture)}–{lastDay.Day.ToString(culture)}"
            : $"{prev.Start.ToString("MMM d", culture)} – {lastDay.ToString("MMM d", culture)}, {prev.Start.Year.ToString(culture)}";
    }

    partial void OnSelectedYearChanged(int value)
    {
        OnPropertyChanged(nameof(IsCurrentYear));
        // Archived years are full-year snapshots only; drop month granularity.
        if (value != _currentYear && IsMonthMode) { IsYearMode = true; return; }
        Loading = LoadAsync();
    }

    partial void OnIsYearModeChanged(bool value)
    {
        if (value) { IsMonthMode = false; Loading = LoadAsync(); }
    }

    partial void OnIsMonthModeChanged(bool value)
    {
        if (value) { IsYearMode = false; Loading = LoadAsync(); }
    }

    [RelayCommand]
    private void SelectYear() => IsYearMode = true;

    [RelayCommand]
    private void SelectMonth() => IsMonthMode = true;

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

    /// <summary>What the share card shows for the stats on screen.</summary>
    internal WrapCardSpec BuildCardSpec() => BuildCardSpec(IsStory ? ShareCardFormat.Story : ShareCardFormat.Square);

    private WrapCardSpec BuildCardSpec(ShareCardFormat format) => new()
    {
        PeriodLabel = _stats.PeriodLabel,
        TopArtists = _stats.TopArtists.Select(e => e.Name).ToList(),
        TopTracks = _stats.TopTracks.Select(e => e.Name).ToList(),
        TotalMinutes = _stats.TotalMinutes,
        TotalPlays = _stats.TotalPlays,
        LosslessPercent = _stats.LosslessPercent,
        TopGenre = _stats.TopGenre,
        ArtworkPath = _stats.TopAlbumArtworkPath,
        TopAlbum = _stats.TopAlbums.Count > 0 ? _stats.TopAlbums[0].Name : null,
        TopAlbumArtist = _stats.TopAlbums.Count > 0 ? _stats.TopAlbums[0].Subtitle : null,
        TopAlbumPlays = _stats.TopAlbums.Count > 0 ? _stats.TopAlbums[0].Plays : 0,
        AlbumCoverPaths = _stats.TopAlbums.Select(a => a.ArtworkPath).ToList(),
        Format = format,
    };

    // Both card shapes, rendered once per set of stats. The 1:1 / 9:16 segments used to
    // re-render on every click — ~0.4 s for 1:1 and ~1.2 s for 9:16 (owner 10-09: "isn't
    // changing in real time"); now the second shape renders in the background right after the
    // first, so a click swaps to it at once. The cache owns the bitmaps (Preview borrows one).
    private readonly Dictionary<ShareCardFormat, (byte[] Png, Bitmap Bitmap)> _cards = new();
    private readonly HashSet<ShareCardFormat> _rendering = new();
    private int _cardEpoch;

    private ShareCardFormat SelectedFormat => IsStory ? ShareCardFormat.Story : ShareCardFormat.Square;

    /// <summary>Drops both cached cards (the stats changed); in-flight renders are ignored.</summary>
    private void InvalidateCards()
    {
        _cardEpoch++;
        _rendering.Clear();
        Preview = null;
        CurrentPng = null;
        foreach (var card in _cards.Values) card.Bitmap.Dispose();
        _cards.Clear();
    }

    private void RefreshPreview()
    {
        if (!HasData)
        {
            InvalidateCards();
            return;
        }

        var format = SelectedFormat;
        if (_cards.TryGetValue(format, out var card))
        {
            CurrentPng = card.Png;
            Preview = card.Bitmap;
        }
        else
        {
            RenderCard(format);
        }
        // Warm the other shape so the next click is instant.
        var other = format == ShareCardFormat.Story ? ShareCardFormat.Square : ShareCardFormat.Story;
        RenderCard(other);
    }

    private void RenderCard(ShareCardFormat format)
    {
        if (_cards.ContainsKey(format) || !_rendering.Add(format)) return;
        var epoch = _cardEpoch;
        var spec = BuildCardSpec(format);
        Task.Run(() =>
        {
            try
            {
                var png = ShareCardRenderer.RenderWrapCard(spec);
                using var ms = new MemoryStream(png);
                // The preview well is ~330 px wide: a 720 px decode looks the same there and
                // scales far faster than the 1080-wide card on every swap. Save/Copy keep the PNG.
                var bitmap = Bitmap.DecodeToWidth(ms, 720, BitmapInterpolationMode.HighQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || epoch != _cardEpoch)
                    {
                        bitmap.Dispose();
                        return;
                    }
                    _rendering.Remove(format);
                    _cards[format] = (png, bitmap);
                    if (SelectedFormat == format)
                    {
                        CurrentPng = png;
                        Preview = bitmap;
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => { if (epoch == _cardEpoch) _rendering.Remove(format); });
                DebugLogger.Log(DebugLogger.Category.UI, DebugLogger.Level.Error,
                    "Wrap card render failed", ex.Message);
            }
        });
    }

    public void ReportStatus(string message) => StatusText = message;

    /// <summary>
    /// Releases the rendered share card. A fresh WrapViewModel is created per OpenWrap and
    /// nothing released the final Preview when the dialog closed, so every open/close cycle
    /// leaked a full-size bitmap plus its PNG byte array until the finalizer ran.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        // In-flight renders see a new card epoch (InvalidateCards) and drop their bitmap.
        _loadGeneration++;
        InvalidateCards();
    }
}
