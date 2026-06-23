using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>One archived year's Wrap snapshot.</summary>
public sealed class ArchivedWrap
{
    public int Year { get; set; }
    public WrapStats Stats { get; set; } = new();
}

public interface IWrapArchiveService
{
    /// <summary>Archived years, newest first.</summary>
    IReadOnlyList<int> ArchivedYears { get; }

    WrapStats? GetYear(int year);

    /// <summary>Freeze any completed past year that has play data but isn't archived yet,
    /// before the 10k-event play log trims those events away.</summary>
    void EnsureArchived(IReadOnlyList<PlayHistoryEvent> events,
                        IReadOnlyDictionary<Guid, Track> tracksById, int currentYear);
}

/// <summary>
/// JSON-file-backed archive of yearly Wrap recaps under the Noctis data directory.
/// The live play log caps at 10,000 events, so finished years are snapshotted here
/// to survive long-term.
/// </summary>
public sealed class WrapArchiveService : IWrapArchiveService
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private List<ArchivedWrap>? _entries;

    public WrapArchiveService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppPaths.DataRoot, "wrap_archive.json");
    }

    public IReadOnlyList<int> ArchivedYears
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _entries!.Select(e => e.Year).OrderByDescending(y => y).ToArray();
            }
        }
    }

    public WrapStats? GetYear(int year)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries!.FirstOrDefault(e => e.Year == year)?.Stats;
        }
    }

    public void EnsureArchived(IReadOnlyList<PlayHistoryEvent> events,
                               IReadOnlyDictionary<Guid, Track> tracksById, int currentYear)
    {
        lock (_lock)
        {
            EnsureLoaded();

            var pastYears = events
                .Select(e => e.PlayedAtUtc.ToLocalTime().Year)
                .Where(y => y < currentYear)
                .Distinct()
                .ToList();

            var changed = false;
            foreach (var year in pastYears)
            {
                if (_entries!.Any(e => e.Year == year)) continue; // already archived

                var stats = WrapStatsBuilder.Build(events, tracksById, year);
                if (stats.TotalPlays == 0) continue;

                _entries!.Add(new ArchivedWrap { Year = year, Stats = stats });
                changed = true;
            }

            if (changed) Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_entries != null) return;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _entries = JsonSerializer.Deserialize<List<ArchivedWrap>>(json) ?? new List<ArchivedWrap>();
                return;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "WrapArchive.Load", ex.Message);
        }
        _entries = new List<ArchivedWrap>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "WrapArchive.Save", ex.Message);
        }
    }
}
