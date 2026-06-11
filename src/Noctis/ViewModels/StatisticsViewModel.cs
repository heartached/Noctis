using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Listening Statistics view. Computes library analytics from
/// ILibraryService data and playback analytics from the persistent play log,
/// split across Overview / Quality Report / Play History tabs.
/// </summary>
public partial class StatisticsViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IPlayHistoryService _playHistory;

    // ── Tabs ──

    public const string TabOverview = "Overview";
    public const string TabQuality = "Quality";
    public const string TabHistory = "History";

    [ObservableProperty] private string _selectedTab = TabOverview;

    public bool IsOverviewTabSelected => SelectedTab == TabOverview;
    public bool IsQualityTabSelected => SelectedTab == TabQuality;
    public bool IsHistoryTabSelected => SelectedTab == TabHistory;

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewTabSelected));
        OnPropertyChanged(nameof(IsQualityTabSelected));
        OnPropertyChanged(nameof(IsHistoryTabSelected));
    }

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    // ── Overview cards ──

    [ObservableProperty] private int _totalTracks;
    [ObservableProperty] private int _totalAlbums;
    [ObservableProperty] private int _totalArtists;
    [ObservableProperty] private string _totalDuration = "";
    [ObservableProperty] private string _totalPlays = "";
    [ObservableProperty] private string _listeningTime = "";
    [ObservableProperty] private string _avgTrackLength = "";
    [ObservableProperty] private int _likedTracks;

    // ── Quality report ──

    [ObservableProperty] private string _losslessPercentText = "";
    [ObservableProperty] private string _losslessSubText = "";
    [ObservableProperty] private string _hiResPercentText = "";
    [ObservableProperty] private string _hiResSubText = "";
    [ObservableProperty] private string _avgSampleRateText = "";
    [ObservableProperty] private string _avgBitDepthText = "";

    // ── Play history ──

    [ObservableProperty] private bool _hasPlayHistory;

    public BulkObservableCollection<StatItem> TopArtists { get; } = new();
    public BulkObservableCollection<StatItem> TopAlbums { get; } = new();
    public BulkObservableCollection<StatItem> FormatBreakdown { get; } = new();
    public BulkObservableCollection<PlayLogItem> PlayLog { get; } = new();
    public BulkObservableCollection<HourHeatCell> HourlyHeatmap { get; } = new();
    public BulkObservableCollection<StatItem> SkipRates { get; } = new();
    public BulkObservableCollection<StatItem> ForgottenFavorites { get; } = new();

    public StatisticsViewModel(ILibraryService library, IPlayHistoryService playHistory)
    {
        _library = library;
        _playHistory = playHistory;
    }

    /// <summary>
    /// Recomputes all statistics. Called on every navigation to the view —
    /// play counts and the play log change without a LibraryUpdated event,
    /// so caching here would show stale numbers.
    /// </summary>
    public void Refresh()
    {
        var tracks = _library.Tracks;
        var events = _playHistory.Events;

        ComputeOverview(tracks);
        ComputeQuality(tracks);
        ComputeHistory(tracks, events);
    }

    // ── Overview ──

    private void ComputeOverview(IReadOnlyList<Track> tracks)
    {
        TotalTracks = tracks.Count;
        TotalAlbums = _library.Albums.Count;
        TotalArtists = _library.Artists.Count;
        TotalDuration = FormatDuration(TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks)));

        var plays = tracks.Sum(t => (long)t.PlayCount);
        TotalPlays = FormatCount(plays);
        ListeningTime = FormatDuration(TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks * t.PlayCount)));
        AvgTrackLength = tracks.Count > 0
            ? TimeSpan.FromTicks((long)tracks.Average(t => t.Duration.Ticks)).ToString(@"m\:ss")
            : "0:00";
        LikedTracks = tracks.Count(t => t.IsFavorite);

        ComputeTopArtists(tracks);
        ComputeTopAlbums(tracks);
    }

    private void ComputeTopArtists(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new StatItem
            {
                Label = g.Key,
                SubLabel = g.Count() == 1 ? "1 track" : $"{g.Count()} tracks",
                Value = g.Sum(t => t.PlayCount),
                ValueLabel = $"{g.Sum(t => t.PlayCount)} plays"
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(10)
            .ToList();

        ApplyRanks(items);
        ApplyPercentages(items);
        TopArtists.ReplaceAll(items);
    }

    private void ComputeTopAlbums(IReadOnlyList<Track> tracks)
    {
        var albumsById = _library.Albums.ToDictionary(a => a.Id);
        var items = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => t.AlbumId)
            .Select(g =>
            {
                albumsById.TryGetValue(g.Key, out var album);
                var totalPlays = g.Sum(t => t.PlayCount);
                return new StatItem
                {
                    Label = album?.Name ?? g.First().Album,
                    SubLabel = album?.Artist ?? g.First().Artist,
                    Value = totalPlays,
                    ValueLabel = $"{totalPlays} plays"
                };
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(10)
            .ToList();

        ApplyRanks(items);
        ApplyPercentages(items);
        TopAlbums.ReplaceAll(items);
    }

    // ── Quality report ──

    private void ComputeQuality(IReadOnlyList<Track> tracks)
    {
        var total = tracks.Count;
        var lossless = tracks.Count(t => t.IsLossless);
        var hiRes = tracks.Count(t => t.IsHiResLossless);

        LosslessPercentText = total > 0 ? $"{lossless * 100.0 / total:0.#}%" : "0%";
        LosslessSubText = lossless == 1 ? "1 lossless track" : $"{lossless} lossless tracks";
        HiResPercentText = total > 0 ? $"{hiRes * 100.0 / total:0.#}%" : "0%";
        HiResSubText = hiRes == 1 ? "1 hi-res track" : $"{hiRes} hi-res tracks";

        var withRate = tracks.Where(t => t.SampleRate > 0).ToList();
        AvgSampleRateText = withRate.Count > 0
            ? $"{withRate.Average(t => t.SampleRate) / 1000.0:0.#} kHz"
            : "N/A";

        var withDepth = tracks.Where(t => t.BitsPerSample > 0).ToList();
        AvgBitDepthText = withDepth.Count > 0
            ? $"{withDepth.Average(t => t.BitsPerSample):0} bit"
            : "N/A";

        var items = tracks
            .GroupBy(FormatLabel)
            .Select(g => new StatItem
            {
                Label = g.Key,
                Value = g.Count(),
                ValueLabel = total > 0
                    ? $"{g.Count()} tracks · {g.Count() * 100.0 / total:0.#}%"
                    : $"{g.Count()} tracks"
            })
            .OrderByDescending(i => i.Value)
            .ToList();

        ApplyPercentages(items);
        FormatBreakdown.ReplaceAll(items);
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

    // ── Play history ──

    private void ComputeHistory(IReadOnlyList<Track> tracks, IReadOnlyList<PlayHistoryEvent> events)
    {
        HasPlayHistory = events.Count > 0;

        ComputePlayLog(events);
        ComputeHourlyHeatmap(events);
        ComputeSkipRates(events);
        ComputeForgottenFavorites(tracks);
    }

    private void ComputePlayLog(IReadOnlyList<PlayHistoryEvent> events)
    {
        var items = events
            .Reverse()
            .Take(100)
            .Select(e => new PlayLogItem
            {
                Title = e.Title,
                Artist = e.Artist,
                TimeLabel = FormatEventTime(e.PlayedAtUtc.ToLocalTime()),
                Skipped = e.Skipped
            })
            .ToList();

        PlayLog.ReplaceAll(items);
    }

    private void ComputeHourlyHeatmap(IReadOnlyList<PlayHistoryEvent> events)
    {
        var counts = new int[24];
        foreach (var e in events)
            counts[e.PlayedAtUtc.ToLocalTime().Hour]++;

        var max = counts.Max();
        var cells = new List<HourHeatCell>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            cells.Add(new HourHeatCell
            {
                Hour = hour,
                Count = counts[hour],
                Intensity = max > 0 ? 0.06 + 0.94 * counts[hour] / max : 0.06,
                HourLabel = hour % 6 == 0 ? $"{hour:00}" : string.Empty
            });
        }

        HourlyHeatmap.ReplaceAll(cells);
    }

    private void ComputeSkipRates(IReadOnlyList<PlayHistoryEvent> events)
    {
        var items = events
            .GroupBy(e => e.TrackId)
            .Where(g => g.Count() >= 3 && g.Any(e => e.Skipped))
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.PlayedAtUtc).First();
                var skips = g.Count(e => e.Skipped);
                var rate = (double)skips / g.Count();
                return new StatItem
                {
                    Label = latest.Title,
                    SubLabel = latest.Artist,
                    Value = skips,
                    Percentage = rate,
                    ValueLabel = $"{rate * 100:0}% · {skips}/{g.Count()} skipped"
                };
            })
            .OrderByDescending(i => i.Percentage)
            .ThenByDescending(i => i.Value)
            .Take(10)
            .ToList();

        SkipRates.ReplaceAll(items);
    }

    private void ComputeForgottenFavorites(IReadOnlyList<Track> tracks)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-6);
        var items = tracks
            .Where(t => (t.IsFavorite || t.Rating >= 4) && !t.IsDisliked)
            .Where(t => t.LastPlayed == null || t.LastPlayed < cutoff)
            .OrderBy(t => t.LastPlayed ?? DateTime.MinValue)
            .Take(20)
            .Select(t => new StatItem
            {
                Label = t.Title,
                SubLabel = t.Artist,
                ValueLabel = t.LastPlayed == null
                    ? "Never played"
                    : $"Last played {FormatAge(DateTime.UtcNow - t.LastPlayed.Value)}"
            })
            .ToList();

        ForgottenFavorites.ReplaceAll(items);
    }

    // ── Formatting helpers ──

    private static void ApplyRanks(List<StatItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].Rank = i + 1;
    }

    private static void ApplyPercentages(List<StatItem> items)
    {
        if (items.Count == 0) return;
        var max = items.Max(i => i.Value);
        if (max == 0) return;
        foreach (var item in items)
            item.Percentage = (double)item.Value / max;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes} min";
    }

    private static string FormatCount(long count)
    {
        if (count >= 1_000_000) return $"{count / 1_000_000.0:0.#}M";
        if (count >= 1_000) return $"{count / 1_000.0:0.#}K";
        return count.ToString();
    }

    private static string FormatEventTime(DateTime local)
    {
        var today = DateTime.Now.Date;
        if (local.Date == today) return $"Today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.Year == today.Year
            ? local.ToString("MMM d, HH:mm")
            : local.ToString("MMM d yyyy, HH:mm");
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 365)
        {
            var years = (int)(age.TotalDays / 365);
            return years == 1 ? "1 year ago" : $"{years} years ago";
        }
        var months = Math.Max(1, (int)(age.TotalDays / 30));
        return months == 1 ? "1 month ago" : $"{months} months ago";
    }
}
