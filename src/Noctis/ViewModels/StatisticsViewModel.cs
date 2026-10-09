using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Listening Statistics page: a listening summary for a chosen period
/// (Overview / Play History tabs) plus the library's static facts.
///
/// Every listening number comes from one source, the play log, through
/// <see cref="ListeningReportBuilder"/>. Total Plays and Top Artists/Albums used to read
/// <see cref="Track.PlayCount"/> while the other tiles read the log, so a re-added music
/// folder (fresh play counts, same log) showed "60 Total Plays" beside "102 Plays This Week".
/// </summary>
public partial class StatisticsViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IPlayHistoryService _playHistory;
    private readonly ArtistImageService? _artistImages;

    // ── Tabs ──

    public const string TabOverview = "Overview";
    public const string TabQuality = "Quality";
    public const string TabHistory = "History";

    [ObservableProperty] private string _selectedTab = TabOverview;

    /// <summary>Raised by the top-bar Back button (shown when opened from
    /// Settings → "View All Stats") so the shell can return to the section the
    /// user was in before opening Settings.</summary>
    public event EventHandler? BackRequested;

    [RelayCommand]
    private void GoBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Settable so the segmented tab pill can bind its radio buttons two-way.</summary>
    public bool IsOverviewTabSelected
    {
        get => SelectedTab == TabOverview;
        set { if (value) SelectedTab = TabOverview; }
    }

    public bool IsQualityTabSelected => SelectedTab == TabQuality;

    public bool IsHistoryTabSelected
    {
        get => SelectedTab == TabHistory;
        set { if (value) SelectedTab = TabHistory; }
    }

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewTabSelected));
        OnPropertyChanged(nameof(IsQualityTabSelected));
        OnPropertyChanged(nameof(IsHistoryTabSelected));
    }

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    /// <summary>Opens the Noctis Wrap recap dialog over the current play log.</summary>
    [RelayCommand]
    private async Task OpenWrap()
    {
        var vm = new WrapViewModel(_playHistory, _library,
            App.Services?.GetService<IWrapArchiveService>());
        await Views.WrapDialog.ShowAsync(vm);
    }

    // ── Period ──

    /// <summary>The window every listening figure covers. Streaks always span the whole log.</summary>
    [ObservableProperty] private ListeningPeriod _period = ListeningPeriod.Last30Days;

    public bool IsLast7DaysSelected
    {
        get => Period == ListeningPeriod.Last7Days;
        set { if (value) Period = ListeningPeriod.Last7Days; }
    }

    public bool IsLast30DaysSelected
    {
        get => Period == ListeningPeriod.Last30Days;
        set { if (value) Period = ListeningPeriod.Last30Days; }
    }

    public bool IsThisYearSelected
    {
        get => Period == ListeningPeriod.ThisYear;
        set { if (value) Period = ListeningPeriod.ThisYear; }
    }

    public bool IsAllTimeSelected
    {
        get => Period == ListeningPeriod.AllTime;
        set { if (value) Period = ListeningPeriod.AllTime; }
    }

    partial void OnPeriodChanged(ListeningPeriod value)
    {
        OnPropertyChanged(nameof(IsLast7DaysSelected));
        OnPropertyChanged(nameof(IsLast30DaysSelected));
        OnPropertyChanged(nameof(IsThisYearSelected));
        OnPropertyChanged(nameof(IsAllTimeSelected));
        Refresh();
    }

    // ── Navigation (wired by the shell the same way as Home) ──

    public event EventHandler<Album>? AlbumOpened;

    private Action<string>? _viewArtistAction;
    public void SetViewArtistAction(Action<string> action) => _viewArtistAction = action;

    [RelayCommand]
    private void OpenArtist(StatsArtistTile? tile)
    {
        if (tile is { CanOpen: true } && !string.IsNullOrWhiteSpace(tile.Name))
            _viewArtistAction?.Invoke(tile.Name);
    }

    [RelayCommand]
    private void OpenAlbum(StatsRankRow? row)
    {
        if (row is not { CanOpen: true }) return;
        var album = _library.GetAlbumById(row.AlbumId);
        if (album != null) AlbumOpened?.Invoke(this, album);
    }

    // ── Listening summary (the selected period) ──

    [ObservableProperty] private bool _hasPlayHistory;
    [ObservableProperty] private bool _hasPeriodPlays;
    [ObservableProperty] private string _periodCaption = "";
    [ObservableProperty] private string _logCoverageText = "";

    [ObservableProperty] private string _playsText = "0";
    [ObservableProperty] private string _playsTip = "";
    [ObservableProperty] private bool _hasDelta;
    [ObservableProperty] private bool _isDeltaUp;
    [ObservableProperty] private bool _isDeltaDown;
    [ObservableProperty] private string _deltaText = "";
    [ObservableProperty] private string _deltaCaption = "";
    [ObservableProperty] private string _deltaTip = "";

    [ObservableProperty] private string _listeningTimeText = "";
    [ObservableProperty] private string _listeningDetailText = "";
    [ObservableProperty] private string _streakText = "";
    [ObservableProperty] private string _longestStreakText = "";
    [ObservableProperty] private string _uniqueArtistsText = "0";
    [ObservableProperty] private string _uniqueTracksText = "";

    [ObservableProperty] private string _peakHourText = "";

    public BulkObservableCollection<StatsArtistTile> TopArtists { get; } = new();
    public BulkObservableCollection<StatsRankRow> TopAlbums { get; } = new();
    public BulkObservableCollection<StatsRankRow> TopTracks { get; } = new();
    public BulkObservableCollection<StatsHourBar> HourBars { get; } = new();
    public BulkObservableCollection<StatsSkipRow> MostSkipped { get; } = new();
    public BulkObservableCollection<StatsRankRow> ForgottenFavorites { get; } = new();

    /// <summary>
    /// The Play History tab as one virtualized list: <see cref="StatsHistoryHeader"/> (hour
    /// chart, Most Skipped, Forgotten Favorites), then day headers and play rows. The old tab
    /// was an ItemsControl in a ScrollViewer, which realizes every row.
    /// </summary>
    public BulkObservableCollection<object> HistoryFeed { get; } = new();

    /// <summary>Number of play rows in <see cref="HistoryFeed"/>.</summary>
    [ObservableProperty] private int _recentPlayCount;

    // ── Library (static facts, not period-bound) ──

    [ObservableProperty] private int _totalTracks;
    [ObservableProperty] private string _totalTracksText = "0";
    [ObservableProperty] private string _totalAlbumsText = "0";
    [ObservableProperty] private string _totalArtistsText = "0";
    [ObservableProperty] private string _totalDuration = "";
    [ObservableProperty] private string _likedText = "0";
    [ObservableProperty] private string _avgTrackLength = "";
    [ObservableProperty] private string _losslessPercentText = "";
    [ObservableProperty] private string _hiResPercentText = "";
    [ObservableProperty] private string _avgSampleRateText = "";
    [ObservableProperty] private string _avgBitDepthText = "";

    public BulkObservableCollection<StatsFormatRow> FormatBreakdown { get; } = new();

    private readonly StatsHistoryHeader _historyHeader;

    public StatisticsViewModel(ILibraryService library, IPlayHistoryService playHistory,
        ArtistImageService? artistImages = null)
    {
        _library = library;
        _playHistory = playHistory;
        _artistImages = artistImages;
        _historyHeader = new StatsHistoryHeader(this);
        HistoryFeed.Add(_historyHeader);
    }

    // Bumped by every refresh: a pass that finishes after a newer one started is dropped.
    private int _refreshGeneration;

    /// <summary>
    /// Recomputes all statistics. Called on every navigation to the view and on a period
    /// change — play counts and the play log change without a LibraryUpdated event,
    /// so caching here would show stale numbers.
    /// </summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>
    /// Recomputes all statistics off the UI thread. Only the snapshot and the property
    /// writes stay on the UI thread; the previous numbers stay up until the new ones land.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;

        // Snapshot on the caller's (UI) thread: the library collections are mutated by
        // scans and watcher batches, so the background pass must not enumerate them live.
        var tracks = _library.Tracks.ToArray();
        var albums = _library.Albums.ToArray();
        var artists = _library.Artists.ToArray();
        var events = _playHistory.Events; // already an immutable published snapshot
        var period = Period;
        var images = _artistImages ?? App.Services?.GetService<ArtistImageService>();

        var stats = await Task.Run(() => Compute(tracks, albums, artists, events, period, DateTime.Now, images))
            .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation == _refreshGeneration)
                Apply(stats);
        });
    }

    /// <summary>Everything the view shows, computed without touching the UI.</summary>
    internal sealed class StatisticsResult
    {
        public bool HasPlayHistory;
        public bool HasPeriodPlays;
        public string PeriodCaption = "";
        public string LogCoverageText = "";
        public string PlaysText = "";
        public string PlaysTip = "";
        public bool HasDelta, IsDeltaUp, IsDeltaDown;
        public string DeltaText = "", DeltaCaption = "", DeltaTip = "";
        public string ListeningTimeText = "", ListeningDetailText = "";
        public string StreakText = "", LongestStreakText = "";
        public string UniqueArtistsText = "", UniqueTracksText = "";
        public string PeakHourText = "";
        public List<StatsArtistTile> TopArtists = new();
        public List<StatsRankRow> TopAlbums = new();
        public List<StatsRankRow> TopTracks = new();
        public List<StatsHourBar> HourBars = new();
        public List<StatsSkipRow> MostSkipped = new();
        public List<StatsRankRow> ForgottenFavorites = new();
        public List<object> HistoryRows = new();
        public int RecentPlayCount;

        public int TotalTracks;
        public string TotalTracksText = "", TotalAlbumsText = "", TotalArtistsText = "";
        public string TotalDuration = "", LikedText = "", AvgTrackLength = "";
        public string LosslessPercentText = "", HiResPercentText = "";
        public string AvgSampleRateText = "", AvgBitDepthText = "";
        public List<StatsFormatRow> FormatBreakdown = new();
    }

    internal static StatisticsResult Compute(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<Album> albums,
        IReadOnlyList<Artist> artists,
        IReadOnlyList<PlayHistoryEvent> events,
        ListeningPeriod period,
        DateTime nowLocal,
        ArtistImageService? artistImages)
    {
        var sw = Stopwatch.StartNew();
        var r = new StatisticsResult();
        ComputeLibrary(r, tracks, albums.Count, artists.Count);

        var resolver = new PlayEventResolver(tracks);
        var report = ListeningReportBuilder.Build(events, resolver, period, nowLocal);
        var albumsById = new Dictionary<Guid, Album>(albums.Count);
        foreach (var a in albums) albumsById[a.Id] = a;

        ComputeSummary(r, report, nowLocal);
        r.TopArtists = ArtistTiles(report, artists, artistImages);
        r.TopAlbums = AlbumRows(report, albumsById);
        r.TopTracks = TrackRows(report, albumsById);
        ComputeHours(r, report);
        r.MostSkipped = SkipRows(report);
        r.ForgottenFavorites = ForgottenFavoriteRows(tracks, report, albumsById, nowLocal);
        BuildHistoryRows(r, report, nowLocal);

        DebugLog.Write("Statistics",
            $"refresh: {events.Count} logged plays, {tracks.Count} tracks, {period}: {report.Plays} plays, " +
            $"computed in {sw.ElapsedMilliseconds} ms");
        return r;
    }

    private void Apply(StatisticsResult r)
    {
        HasPlayHistory = r.HasPlayHistory;
        HasPeriodPlays = r.HasPeriodPlays;
        PeriodCaption = r.PeriodCaption;
        LogCoverageText = r.LogCoverageText;
        PlaysText = r.PlaysText;
        PlaysTip = r.PlaysTip;
        HasDelta = r.HasDelta;
        IsDeltaUp = r.IsDeltaUp;
        IsDeltaDown = r.IsDeltaDown;
        DeltaText = r.DeltaText;
        DeltaCaption = r.DeltaCaption;
        DeltaTip = r.DeltaTip;
        ListeningTimeText = r.ListeningTimeText;
        ListeningDetailText = r.ListeningDetailText;
        StreakText = r.StreakText;
        LongestStreakText = r.LongestStreakText;
        UniqueArtistsText = r.UniqueArtistsText;
        UniqueTracksText = r.UniqueTracksText;
        PeakHourText = r.PeakHourText;

        TopArtists.ReplaceAll(r.TopArtists);
        TopAlbums.ReplaceAll(r.TopAlbums);
        TopTracks.ReplaceAll(r.TopTracks);
        HourBars.ReplaceAll(r.HourBars);
        MostSkipped.ReplaceAll(r.MostSkipped);
        ForgottenFavorites.ReplaceAll(r.ForgottenFavorites);
        RecentPlayCount = r.RecentPlayCount;
        HistoryFeed.ReplaceAll(r.HistoryRows.Prepend(_historyHeader));

        TotalTracks = r.TotalTracks;
        TotalTracksText = r.TotalTracksText;
        TotalAlbumsText = r.TotalAlbumsText;
        TotalArtistsText = r.TotalArtistsText;
        TotalDuration = r.TotalDuration;
        LikedText = r.LikedText;
        AvgTrackLength = r.AvgTrackLength;
        LosslessPercentText = r.LosslessPercentText;
        HiResPercentText = r.HiResPercentText;
        AvgSampleRateText = r.AvgSampleRateText;
        AvgBitDepthText = r.AvgBitDepthText;
        FormatBreakdown.ReplaceAll(r.FormatBreakdown);
    }

    // ── Listening summary ──

    private static void ComputeSummary(StatisticsResult r, ListeningReport report, DateTime nowLocal)
    {
        r.HasPlayHistory = report.LoggedPlays > 0;
        r.HasPeriodPlays = report.Plays > 0;
        r.PeriodCaption = DescribePeriod(report, nowLocal);
        r.LogCoverageText = report.FirstPlayLocal is not { } first ? ""
            : report.LoggedShortPlays > 0
                ? Loc.T("Stats.LogCoverageShort", ShortDate(first), PlaysLabel(report.LoggedPlays), Count(report.LoggedShortPlays))
                : Loc.T("Stats.LogCoverage", ShortDate(first), PlaysLabel(report.LoggedPlays));
        r.PlaysTip = Loc.T("Stats.PlaysTip", Count(report.ShortPlays));

        r.PlaysText = Count(report.Plays);
        if (report.PreviousPlays is { } previous && (previous > 0 || report.Plays > 0))
        {
            r.HasDelta = true;
            r.DeltaCaption = report.Period switch
            {
                ListeningPeriod.Last7Days => Loc.T("Stats.VsPrevious7Days"),
                ListeningPeriod.Last30Days => Loc.T("Stats.VsPrevious30Days"),
                _ => Loc.T("Stats.VsLastYear"),
            };
            r.DeltaTip = Loc.T("Stats.DeltaTip", Count(report.Plays), Count(previous));
            (r.DeltaText, r.IsDeltaUp, r.IsDeltaDown) = FormatDelta(report.Plays, previous);
        }
        else
        {
            r.DeltaCaption = r.PeriodCaption;
        }

        r.ListeningTimeText = FormatListening(TimeSpan.FromTicks(report.ListenedTicks));
        r.ListeningDetailText = Loc.T("Stats.ListenedDetail", Count(report.CompletedPlays), Count(report.SkippedPlays));
        r.StreakText = DaysLabel(report.CurrentStreakDays);
        r.LongestStreakText = Loc.T("Stats.LongestStreak", DaysLabel(report.LongestStreakDays));
        r.UniqueArtistsText = Count(report.UniqueArtists);
        r.UniqueTracksText = report.UniqueTracks == 1
            ? Loc.T("Stats.SongCountOne")
            : Loc.T("Stats.SongCountMany", Count(report.UniqueTracks));
    }

    internal static (string Text, bool Up, bool Down) FormatDelta(int plays, int previous)
    {
        if (plays == previous) return (Loc.T("Stats.DeltaSame"), false, false);
        if (previous == 0) return (Loc.T("Stats.DeltaNew"), true, false);
        var change = (plays - previous) * 100.0 / previous;
        var magnitude = Math.Abs(change) < 1 ? "<1%" : $"{Math.Round(Math.Abs(change)).ToString("N0", CultureInfo.CurrentCulture)}%";
        return change > 0 ? ($"▲ {magnitude}", true, false) : ($"▼ {magnitude}", false, true);
    }

    private static string DescribePeriod(ListeningReport report, DateTime nowLocal) => report.Period switch
    {
        ListeningPeriod.Last7Days => Loc.T("Stats.RangeLast7Days"),
        ListeningPeriod.Last30Days => Loc.T("Stats.RangeLast30Days"),
        ListeningPeriod.ThisYear => Loc.T("Stats.RangeThisYear", nowLocal.Year),
        _ => report.FirstPlayLocal is { } first
            ? Loc.T("Stats.RangeAllTimeSince", ShortDate(first))
            : Loc.T("Stats.RangeAllTime"),
    };

    private static List<StatsArtistTile> ArtistTiles(ListeningReport report, IReadOnlyList<Artist> artists,
        ArtistImageService? images)
    {
        var tiles = new List<StatsArtistTile>(report.TopArtists.Count);
        if (report.TopArtists.Count == 0) return tiles;

        var byName = new Dictionary<string, Artist>(artists.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var a in artists)
            if (!string.IsNullOrWhiteSpace(a.Name)) byName.TryAdd(a.Name.Trim(), a);

        for (var i = 0; i < report.TopArtists.Count; i++)
        {
            var entry = report.TopArtists[i];
            byName.TryGetValue(entry.Name, out var artist);
            tiles.Add(new StatsArtistTile
            {
                Rank = i + 1,
                Name = artist?.Name ?? entry.Name,
                PlaysText = PlaysLabel(entry.Plays),
                ImagePath = artist != null ? CachedArtistPhoto(artist, images) : null,
                CanOpen = artist != null,
            });
        }
        return tiles;
    }

    /// <summary>
    /// The portrait the Artists page and Home already cached — never a download. Prefers the
    /// path the image service put on the library artist, then the cache file the service names
    /// for that artist (a portrait fetched in an earlier session), unless the user removed it.
    /// </summary>
    internal static string? CachedArtistPhoto(Artist artist, ArtistImageService? images)
    {
        if (!string.IsNullOrEmpty(artist.ImagePath)) return artist.ImagePath;
        if (images == null) return null;
        try
        {
            if (images.IsImageRemoved(artist.Id) || !images.HasCachedImage(artist.Id)) return null;
            return images.GetCachedImagePath(artist.Id);
        }
        catch (Exception)
        {
            return null; // an unreadable cache folder just means a placeholder
        }
    }

    private static List<StatsRankRow> AlbumRows(ListeningReport report, IReadOnlyDictionary<Guid, Album> albumsById)
    {
        var max = report.TopAlbums.Count > 0 ? report.TopAlbums[0].Plays : 0;
        return report.TopAlbums.Select((entry, i) =>
        {
            albumsById.TryGetValue(entry.AlbumId, out var album);
            return new StatsRankRow
            {
                Rank = i + 1,
                Title = album?.Name ?? entry.Name,
                Subtitle = album?.Artist ?? entry.Subtitle,
                PlaysText = PlaysLabel(entry.Plays),
                ArtworkPath = NonEmpty(album?.ArtworkPath) ?? NonEmpty(entry.Track?.AlbumArtworkPath),
                Fraction = max > 0 ? (double)entry.Plays / max : 0,
                AlbumId = entry.AlbumId,
                CanOpen = album != null,
            };
        }).ToList();
    }

    private static List<StatsRankRow> TrackRows(ListeningReport report, IReadOnlyDictionary<Guid, Album> albumsById)
    {
        var max = report.TopTracks.Count > 0 ? report.TopTracks[0].Plays : 0;
        return report.TopTracks.Select((entry, i) => new StatsRankRow
        {
            Rank = i + 1,
            Title = entry.Name,
            Subtitle = entry.Subtitle,
            PlaysText = PlaysLabel(entry.Plays),
            ArtworkPath = NonEmpty(entry.Track?.AlbumArtworkPath),
            Fraction = max > 0 ? (double)entry.Plays / max : 0,
            AlbumId = entry.AlbumId,
            CanOpen = entry.AlbumId != Guid.Empty && albumsById.ContainsKey(entry.AlbumId),
        }).ToList();
    }

    private static void ComputeHours(StatisticsResult r, ListeningReport report)
    {
        var counts = report.HourCounts;
        var max = counts.Max();
        var total = counts.Sum();
        var peak = max > 0 ? Array.IndexOf(counts.ToArray(), max) : -1;

        for (var hour = 0; hour < 24; hour++)
        {
            var count = counts[hour];
            var fraction = max > 0 ? (double)count / max : 0;
            r.HourBars.Add(new StatsHourBar
            {
                Hour = hour,
                Count = count,
                Fraction = fraction,
                BarHeight = count == 0 ? 0 : Math.Max(StatsHourBar.MinBarHeight, fraction * StatsHourBar.ChartHeight),
                IsPeak = hour == peak,
                Label = hour % 3 == 0 ? $"{hour:00}" : string.Empty,
                Tooltip = Loc.T("Stats.HourTip", $"{hour:00}:00", $"{hour:00}:59", PlaysLabel(count)),
            });
        }

        r.PeakHourText = peak >= 0
            ? Loc.T("Stats.PeakHour", $"{peak:00}:00", Percent(max, total))
            : "";
    }

    private static List<StatsSkipRow> SkipRows(ListeningReport report) =>
        report.MostSkipped.Select(s => new StatsSkipRow
        {
            Title = s.Title,
            Subtitle = s.Artist,
            RateText = $"{Math.Round(s.Rate * 100).ToString("N0", CultureInfo.CurrentCulture)}%",
            DetailText = Loc.T("Stats.SkipDetail", Count(s.Skips), Count(s.Plays)),
            Fraction = s.Rate,
            ArtworkPath = NonEmpty(s.Track?.AlbumArtworkPath),
        }).ToList();

    /// <summary>
    /// Loved or 4★+ songs not played in six months. "Last played" is the later of the
    /// track's own LastPlayed and its last logged play: a re-added file starts with no
    /// LastPlayed while the log still remembers it.
    /// </summary>
    private static List<StatsRankRow> ForgottenFavoriteRows(IReadOnlyList<Track> tracks, ListeningReport report,
        IReadOnlyDictionary<Guid, Album> albumsById, DateTime nowLocal)
    {
        const int MaxRows = 5;
        var nowUtc = nowLocal.ToUniversalTime();
        var cutoff = nowUtc.AddMonths(-6);
        return tracks
            .Where(t => (t.IsFavorite || t.Rating >= 4) && !t.IsDisliked)
            .Select(t =>
            {
                DateTime? last = t.LastPlayed;
                if (report.LastPlayedUtc.TryGetValue(t.Id, out var logged) && (last == null || logged > last))
                    last = logged;
                return (Track: t, Last: last);
            })
            .Where(x => x.Last == null || x.Last < cutoff)
            .OrderBy(x => x.Last ?? DateTime.MinValue)
            .Take(MaxRows)
            .Select(x => new StatsRankRow
            {
                Title = x.Track.Title,
                Subtitle = x.Track.Artist,
                PlaysText = x.Last == null
                    ? Loc.T("Stats.NeverPlayed")
                    : Loc.T("Stats.LastPlayed", FormatAge(nowUtc - x.Last.Value)),
                ArtworkPath = NonEmpty(x.Track.AlbumArtworkPath),
                AlbumId = x.Track.AlbumId,
                CanOpen = albumsById.ContainsKey(x.Track.AlbumId),
            })
            .ToList();
    }

    private static void BuildHistoryRows(StatisticsResult r, ListeningReport report, DateTime nowLocal)
    {
        var rows = r.HistoryRows;
        if (report.Recent.Count == 0)
        {
            rows.Add(new StatsFeedEmpty
            {
                Text = report.LoggedPlays == 0 ? Loc.T("Statistics.NoPlaysRecordedYet") : Loc.T("Stats.NoPlaysInPeriod"),
            });
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var today = nowLocal.Date;
        var i = 0;
        while (i < report.Recent.Count)
        {
            var day = report.Recent[i].PlayedLocal.Date;
            var end = i;
            while (end < report.Recent.Count && report.Recent[end].PlayedLocal.Date == day) end++;

            rows.Add(new StatsDayHeader
            {
                Text = DayLabel(day, today, culture),
                CountText = PlaysLabel(end - i),
            });
            for (; i < end; i++)
            {
                var play = report.Recent[i];
                rows.Add(new StatsPlayRow
                {
                    Title = play.Track?.Title ?? play.Event.Title,
                    Artist = play.Track?.Artist ?? play.Event.Artist,
                    Album = play.Track != null && Track.IsRealAlbumName(play.Track.Album) ? play.Track.Album : string.Empty,
                    TimeText = play.PlayedLocal.ToString("t", culture),
                    Skipped = play.Event.Skipped,
                    ArtworkPath = NonEmpty(play.Track?.AlbumArtworkPath),
                });
            }
        }
        r.RecentPlayCount = report.Recent.Count;
    }

    // ── Library ──

    private static void ComputeLibrary(StatisticsResult r, IReadOnlyList<Track> tracks, int albumCount, int artistCount)
    {
        long durationTicks = 0;
        int lossless = 0, hiRes = 0, liked = 0;
        long rateSum = 0, depthSum = 0;
        int rateCount = 0, depthCount = 0;
        var formats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            durationTicks += t.Duration.Ticks;
            if (t.IsLossless) lossless++;
            if (t.IsHiResLossless) hiRes++;
            if (t.IsFavorite) liked++;
            if (t.SampleRate > 0) { rateSum += t.SampleRate; rateCount++; }
            if (t.BitsPerSample > 0) { depthSum += t.BitsPerSample; depthCount++; }
            var label = FormatLabel(t);
            formats[label] = formats.GetValueOrDefault(label) + 1;
        }

        var total = tracks.Count;
        r.TotalTracks = total;
        r.TotalTracksText = Count(total);
        r.TotalAlbumsText = Count(albumCount);
        r.TotalArtistsText = Count(artistCount);
        r.TotalDuration = FormatDuration(TimeSpan.FromTicks(durationTicks));
        r.LikedText = Count(liked);
        // The library's own average. This chip used to show the average logged play,
        // which on a log of songs that left the library is the 3.5-minute fallback (3:30).
        r.AvgTrackLength = total > 0 ? FormatClock(TimeSpan.FromTicks(durationTicks / total)) : "0:00";
        r.LosslessPercentText = Percent(lossless, total);
        r.HiResPercentText = Percent(hiRes, total);
        r.AvgSampleRateText = rateCount > 0
            ? $"{(rateSum / (double)rateCount / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)} kHz"
            : "N/A";
        r.AvgBitDepthText = depthCount > 0
            ? $"{(depthSum / (double)depthCount).ToString("0", CultureInfo.CurrentCulture)} bit"
            : "N/A";

        var max = formats.Count > 0 ? formats.Values.Max() : 0;
        r.FormatBreakdown = formats
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new StatsFormatRow
            {
                Label = kv.Key,
                Fraction = max > 0 ? (double)kv.Value / max : 0,
                ValueText = $"{Count(kv.Value)} · {Percent(kv.Value, total)}",
            })
            .ToList();
    }

    /// <summary>Display label for the format breakdown: codec short name when known,
    /// otherwise a label derived from the codec string or file extension.</summary>
    private static string FormatLabel(Track track)
    {
        var shortName = track.CodecShortName;
        if (!string.IsNullOrEmpty(shortName)) return shortName;

        var codec = (track.Codec ?? string.Empty).ToLowerInvariant();
        if (codec.Contains("mpeg") || codec.Contains("mp3")) return "MP3";
        if (codec.Contains("aac")) return "AAC";
        if (codec.Contains("vorbis")) return "OGG";
        if (codec.Contains("opus")) return "OPUS";
        if (codec.Contains("wma") || codec.Contains("windows media")) return "WMA";

        var ext = Path.GetExtension(track.FilePath).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(ext) ? "Unknown" : ext;
    }

    // ── Formatting helpers ──

    private static string? NonEmpty(string? path) => string.IsNullOrEmpty(path) ? null : path;

    private static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    internal static string PlaysLabel(int plays) =>
        plays == 1 ? Loc.T("Stats.PlayCountOne") : Loc.T("Stats.PlayCountMany", Count(plays));

    private static string DaysLabel(int days) =>
        days == 1 ? Loc.T("Stats.DayCountOne") : Loc.T("Stats.DayCountMany", Count(days));

    /// <summary>
    /// Share of a total. A non-zero share under 0.1% reads "&lt;0.1%": "0.#" rounded one MP3 in
    /// a 4,353-track library to "0%" (owner screenshot 10-09, "MP3 0%").
    /// </summary>
    internal static string Percent(long part, long total)
    {
        if (total <= 0 || part <= 0) return "0%";
        var pct = part * 100.0 / total;
        if (pct < 0.05) return $"<{0.1.ToString("0.0", CultureInfo.CurrentCulture)}%";
        return $"{pct.ToString("0.#", CultureInfo.CurrentCulture)}%";
    }

    private static string FormatClock(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");

    /// <summary>Library size: days for a big library ("11d 0h 7m").</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes} min";
    }

    /// <summary>Listening time in hours ("348h 10m"): one unit reads faster than "14d 12h 10m".
    /// Rounded to the minute like the Wrap's minutes, so both show the same total.</summary>
    internal static string FormatListening(TimeSpan time)
    {
        var minutes = (long)Math.Round(time.TotalMinutes);
        if (minutes >= 60)
            return $"{(minutes / 60).ToString("N0", CultureInfo.CurrentCulture)}h {minutes % 60}m";
        return $"{minutes} min";
    }

    private static string ShortDate(DateTime local) =>
        local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

    private static string DayLabel(DateTime day, DateTime today, CultureInfo culture)
    {
        if (day == today) return Loc.T("Stats.Today");
        if (day == today.AddDays(-1)) return Loc.T("Stats.Yesterday");
        return day.Year == today.Year
            ? $"{day.ToString("dddd", culture)}, {day.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}"
            : day.ToString("D", culture);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365)
        {
            var years = (int)(age.TotalDays / 365);
            return years == 1 ? Loc.T("Stats.YearAgo") : Loc.T("Stats.YearsAgo", years);
        }
        var months = Math.Max(1, (int)(age.TotalDays / 30));
        return months == 1 ? Loc.T("Stats.MonthAgo") : Loc.T("Stats.MonthsAgo", months);
    }
}

// ── Row models (display-ready; built off the UI thread) ──

/// <summary>One portrait in the Top Artists row.</summary>
public sealed class StatsArtistTile
{
    public int Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PlaysText { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);
    /// <summary>The artist is in the library, so the tile can open its page.</summary>
    public bool CanOpen { get; init; }
}

/// <summary>A ranked album or song (Top Albums / Top Songs), or a Forgotten Favorite (Rank 0).</summary>
public sealed class StatsRankRow
{
    public int Rank { get; init; }
    public string RankText => Rank > 0 ? Rank.ToString(CultureInfo.CurrentCulture) : string.Empty;
    public bool IsPodium => Rank is >= 1 and <= 3;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string PlaysText { get; init; } = string.Empty;
    public string? ArtworkPath { get; init; }
    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);
    /// <summary>Plays relative to the list's leader (0–1).</summary>
    public double Fraction { get; init; }
    public Guid AlbumId { get; init; }
    public bool CanOpen { get; init; }
}

/// <summary>One hour column of the Listening by Hour chart.</summary>
public sealed class StatsHourBar
{
    public const double ChartHeight = 96;
    public const double MinBarHeight = 3;

    public int Hour { get; init; }
    public int Count { get; init; }
    public double Fraction { get; init; }
    public double BarHeight { get; init; }
    public bool IsPeak { get; init; }
    /// <summary>Axis label every third hour ("00", "03" … "21"), empty otherwise.</summary>
    public string Label { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>A Most Skipped row.</summary>
public sealed class StatsSkipRow
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string RateText { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    /// <summary>Skip rate (0–1), drawn as the row's bar.</summary>
    public double Fraction { get; init; }
    public string? ArtworkPath { get; init; }
    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);
}

/// <summary>A Format Breakdown bar.</summary>
public sealed class StatsFormatRow
{
    public string Label { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public string ValueText { get; init; } = string.Empty;
}

/// <summary>First item of the Play History feed: the hour chart and the two short lists.</summary>
public sealed class StatsHistoryHeader
{
    public StatsHistoryHeader(StatisticsViewModel owner) => Owner = owner;
    public StatisticsViewModel Owner { get; }
}

/// <summary>A day heading in the Play History feed.</summary>
public sealed class StatsDayHeader
{
    public string Text { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;
}

/// <summary>One logged play in the Play History feed.</summary>
public sealed class StatsPlayRow
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    /// <summary>Empty when the song left the library (the log keeps no album).</summary>
    public string Album { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
    public bool Skipped { get; init; }
    public string? ArtworkPath { get; init; }
    public bool HasArtwork => !string.IsNullOrEmpty(ArtworkPath);
}

/// <summary>The feed's empty state (no plays in the period, or none logged yet).</summary>
public sealed class StatsFeedEmpty
{
    public string Text { get; init; } = string.Empty;
}
