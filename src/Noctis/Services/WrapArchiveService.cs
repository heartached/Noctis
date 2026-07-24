using System.Text.Json;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>One archived year's Wrap snapshot.</summary>
public sealed class ArchivedWrap
{
    public int Year { get; set; }
    public WrapStats Stats { get; set; } = new();

    /// <summary>
    /// Earliest event in the log this snapshot was built from. The live log is capped at
    /// 10,000 events, so a user whose first Wrap open came after last year had already
    /// been partially trimmed used to have the incomplete numbers frozen as the permanent
    /// record, with nothing recorded to say so. Null for snapshots written before this
    /// field existed — those are treated as unknown coverage and never replaced silently.
    /// </summary>
    public DateTime? SourceLogStartUtc { get; set; }

    /// <summary>
    /// True when the source log reached back past the start of <see cref="Year"/>, i.e. the
    /// snapshot covers the whole year. False means the recap is partial.
    /// </summary>
    public bool IsComplete { get; set; } = true;
}

public interface IWrapArchiveService
{
    /// <summary>Archived years, newest first.</summary>
    IReadOnlyList<int> ArchivedYears { get; }

    WrapStats? GetYear(int year);

    /// <summary>
    /// False when the snapshot for <paramref name="year"/> was built from a play log that
    /// had already been trimmed past the start of that year, so the recap is incomplete.
    /// True for unarchived and unknown-coverage years.
    /// </summary>
    bool IsYearComplete(int year);

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

    public bool IsYearComplete(int year)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries!.FirstOrDefault(e => e.Year == year)?.IsComplete ?? true;
        }
    }

    public void EnsureArchived(IReadOnlyList<PlayHistoryEvent> events,
                               IReadOnlyDictionary<Guid, Track> tracksById, int currentYear)
    {
        lock (_lock)
        {
            EnsureLoaded();

            if (events.Count == 0) return;

            // How far back the live log still reaches. Everything before this was trimmed
            // by the 10k cap, so a year whose January 1st is after this point can only be
            // snapshotted partially.
            var logStartUtc = events.Min(e => e.PlayedAtUtc);

            var pastYears = events
                .Select(e => e.PlayedAtUtc.ToLocalTime().Year)
                .Where(y => y < currentYear)
                .Distinct()
                .ToList();

            var changed = false;
            foreach (var year in pastYears)
            {
                var yearStartUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
                var isComplete = logStartUtc <= yearStartUtc;

                var existing = _entries!.FirstOrDefault(e => e.Year == year);
                if (existing != null)
                {
                    // Only replace a snapshot we know to be partial, and only with one that
                    // genuinely covers more of the year (a restored play_history.json, say).
                    // Anything else — including an unknown-coverage legacy entry — is left
                    // alone: the archive is the long-term record and the live log only
                    // shrinks.
                    var improves = !existing.IsComplete
                                   && existing.SourceLogStartUtc is { } prev
                                   && logStartUtc < prev;
                    if (!improves) continue;
                }

                var stats = WrapStatsBuilder.Build(events, tracksById, year);
                if (stats.TotalPlays == 0) continue;

                var entry = new ArchivedWrap
                {
                    Year = year,
                    Stats = stats,
                    SourceLogStartUtc = logStartUtc,
                    IsComplete = isComplete
                };

                if (existing != null) _entries!.Remove(existing);
                _entries!.Add(entry);
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
