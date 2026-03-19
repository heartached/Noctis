using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// ViewModel for the Statistics view. Computes library analytics from ILibraryService data.
/// </summary>
public partial class StatisticsViewModel : ViewModelBase
{
    private readonly ILibraryService _library;

    // ── Overview cards ──

    [ObservableProperty] private int _totalTracks;
    [ObservableProperty] private int _totalAlbums;
    [ObservableProperty] private int _totalArtists;
    [ObservableProperty] private int _totalGenres;
    [ObservableProperty] private string _totalListeningTime = "";
    [ObservableProperty] private string _totalFileSize = "";

    // ── Audio quality ──

    [ObservableProperty] private int _losslessCount;
    [ObservableProperty] private int _lossyCount;
    [ObservableProperty] private int _hiResCount;
    [ObservableProperty] private double _losslessPercentage;
    [ObservableProperty] private string _losslessPercentageText = "";
    [ObservableProperty] private string _lossyPercentageText = "";

    // ── Chart collections ──

    public BulkObservableCollection<StatItem> TopTracks { get; } = new();
    public BulkObservableCollection<StatItem> TopArtists { get; } = new();
    public BulkObservableCollection<StatItem> TopAlbums { get; } = new();
    public BulkObservableCollection<StatItem> GenreDistribution { get; } = new();
    public BulkObservableCollection<StatItem> DecadeDistribution { get; } = new();
    public BulkObservableCollection<StatItem> MonthlyGrowth { get; } = new();

    public StatisticsViewModel(ILibraryService library)
    {
        _library = library;
    }

    /// <summary>
    /// Recomputes all statistics from the current library data.
    /// Called when the user navigates to the Statistics view.
    /// </summary>
    public void Refresh()
    {
        var tracks = _library.Tracks;
        if (tracks.Count == 0)
        {
            ClearAll();
            return;
        }

        ComputeOverview(tracks);
        ComputeAudioQuality(tracks);
        ComputeTopTracks(tracks);
        ComputeTopArtists(tracks);
        ComputeTopAlbums(tracks);
        ComputeGenreDistribution(tracks);
        ComputeDecadeDistribution(tracks);
        ComputeMonthlyGrowth(tracks);
    }

    private void ClearAll()
    {
        TotalTracks = 0;
        TotalAlbums = 0;
        TotalArtists = 0;
        TotalGenres = 0;
        TotalListeningTime = "0 min";
        TotalFileSize = "0 MB";
        LosslessCount = 0;
        LossyCount = 0;
        HiResCount = 0;
        LosslessPercentage = 0;
        LosslessPercentageText = "0%";
        LossyPercentageText = "0%";
        TopTracks.ReplaceAll(Array.Empty<StatItem>());
        TopArtists.ReplaceAll(Array.Empty<StatItem>());
        TopAlbums.ReplaceAll(Array.Empty<StatItem>());
        GenreDistribution.ReplaceAll(Array.Empty<StatItem>());
        DecadeDistribution.ReplaceAll(Array.Empty<StatItem>());
        MonthlyGrowth.ReplaceAll(Array.Empty<StatItem>());
    }

    private void ComputeOverview(IReadOnlyList<Track> tracks)
    {
        TotalTracks = tracks.Count;
        TotalAlbums = _library.Albums.Count;
        TotalArtists = _library.Artists.Count;
        TotalGenres = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .Select(t => t.Genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var totalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        TotalListeningTime = FormatDuration(totalDuration);

        var totalBytes = tracks.Sum(t => t.FileSize);
        TotalFileSize = FormatFileSize(totalBytes);
    }

    private void ComputeAudioQuality(IReadOnlyList<Track> tracks)
    {
        LosslessCount = tracks.Count(t => t.IsLossless);
        LossyCount = tracks.Count - LosslessCount;
        HiResCount = tracks.Count(t => t.IsHiResLossless);

        if (tracks.Count > 0)
        {
            var pct = (double)LosslessCount / tracks.Count;
            LosslessPercentage = pct;
            LosslessPercentageText = $"{pct * 100:F0}%";
            LossyPercentageText = $"{(1 - pct) * 100:F0}%";
        }
    }

    private void ComputeTopTracks(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => t.PlayCount > 0)
            .OrderByDescending(t => t.PlayCount)
            .Take(10)
            .Select(t => new StatItem
            {
                Label = t.Title ?? "Unknown",
                SubLabel = t.Artist ?? "Unknown Artist",
                Value = t.PlayCount,
                ValueLabel = $"{t.PlayCount} plays"
            })
            .ToList();

        ApplyPercentages(items);
        TopTracks.ReplaceAll(items);
    }

    private void ComputeTopArtists(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new StatItem
            {
                Label = g.Key,
                SubLabel = $"{g.Count()} tracks",
                Value = g.Sum(t => t.PlayCount),
                ValueLabel = $"{g.Sum(t => t.PlayCount)} plays"
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(10)
            .ToList();

        ApplyPercentages(items);
        TopArtists.ReplaceAll(items);
    }

    private void ComputeTopAlbums(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => t.AlbumId)
            .Select(g =>
            {
                var album = _library.Albums.FirstOrDefault(a => a.Id == g.Key);
                var totalPlays = g.Sum(t => t.PlayCount);
                return new StatItem
                {
                    Label = album?.Name ?? g.First().Album ?? "Unknown",
                    SubLabel = album?.Artist ?? g.First().Artist ?? "Unknown Artist",
                    Value = totalPlays,
                    ValueLabel = $"{totalPlays} plays"
                };
            })
            .Where(i => i.Value > 0)
            .OrderByDescending(i => i.Value)
            .Take(5)
            .ToList();

        ApplyPercentages(items);
        TopAlbums.ReplaceAll(items);
    }

    private void ComputeGenreDistribution(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .GroupBy(t => t.Genre.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new StatItem
            {
                Label = g.Key,
                Value = g.Count(),
                ValueLabel = $"{g.Count()} tracks"
            })
            .OrderByDescending(i => i.Value)
            .Take(10)
            .ToList();

        ApplyPercentages(items);
        GenreDistribution.ReplaceAll(items);
    }

    private void ComputeDecadeDistribution(IReadOnlyList<Track> tracks)
    {
        var items = tracks
            .Where(t => t.Year > 0)
            .GroupBy(t => (t.Year / 10) * 10)
            .Select(g => new StatItem
            {
                Label = $"{g.Key}s",
                Value = g.Count(),
                ValueLabel = $"{g.Count()} tracks"
            })
            .OrderBy(i => i.Label)
            .ToList();

        ApplyPercentages(items);
        DecadeDistribution.ReplaceAll(items);
    }

    private void ComputeMonthlyGrowth(IReadOnlyList<Track> tracks)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var items = tracks
            .Where(t => t.DateAdded >= cutoff)
            .GroupBy(t => new { t.DateAdded.Year, t.DateAdded.Month })
            .Select(g => new StatItem
            {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"),
                Value = g.Count(),
                ValueLabel = $"{g.Count()} tracks"
            })
            .OrderBy(i => i.Label) // Alphabetical is wrong — need chronological
            .ToList();

        // Sort chronologically
        var sorted = tracks
            .Where(t => t.DateAdded >= cutoff)
            .GroupBy(t => new { t.DateAdded.Year, t.DateAdded.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new StatItem
            {
                Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"),
                Value = g.Count(),
                ValueLabel = $"{g.Count()} tracks"
            })
            .ToList();

        ApplyPercentages(sorted);
        MonthlyGrowth.ReplaceAll(sorted);
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

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
        return $"{bytes / (double)(1L << 10):F0} KB";
    }
}
