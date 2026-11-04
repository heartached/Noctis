using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// Pacing for one online source in a long bulk run (whole-library lyrics or animated artwork):
/// requests at least <see cref="MinInterval"/> apart, and after the source answers busy (HTTP
/// 429/503) everything waits out a back-off that doubles each time (from
/// <see cref="FirstBackoff"/> up to <see cref="MaxBackoff"/>) and resets on the next good
/// answer. After <see cref="GiveUpAfter"/> busy answers in a row the source is left alone for
/// the rest of the run. One gate is shared by every worker of a run.
/// </summary>
public sealed class PoliteGate
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTime _next = DateTime.MinValue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private int _busyInARow;

    public PoliteGate(TimeSpan minInterval, Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        MinInterval = minInterval;
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public TimeSpan MinInterval { get; }
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(4);
    public int GiveUpAfter { get; init; } = 4;

    /// <summary>The source said busy <see cref="GiveUpAfter"/> times in a row: skip it for this run.</summary>
    public bool GaveUp => Volatile.Read(ref _busyInARow) >= GiveUpAfter;

    /// <summary>Waits for this caller's turn: the minimum spacing, and any back-off in force.</summary>
    public async Task WaitTurnAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read after each wait: a busy answer meanwhile pushes the next turn further out.
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    wait = _next - _now();
                    if (wait <= TimeSpan.Zero)
                    {
                        _next = _now() + MinInterval;
                        return;
                    }
                }
                await _delay(wait, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The source answered busy: back off before anyone asks it again.</summary>
    public void ReportBusy()
    {
        lock (_lock)
        {
            _busyInARow++;
            _backoff = _backoff == TimeSpan.Zero ? FirstBackoff : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
            var until = _now() + _backoff;
            if (until > _next) _next = until;
        }
    }

    /// <summary>The source answered (found or not): the back-off is over.</summary>
    public void ReportOk()
    {
        lock (_lock)
        {
            _busyInARow = 0;
            _backoff = TimeSpan.Zero;
        }
    }

    /// <summary>The back-off the next busy answer would start from (tests).</summary>
    internal TimeSpan CurrentBackoff { get { lock (_lock) return _backoff; } }
}

/// <summary>
/// What a bulk run already looked up, so a stopped or interrupted whole-library run picks up
/// where it left off and a second run doesn't ask the servers about the same songs again. Keyed
/// by track (or album) id: when it was checked and the best it found. An entry older than
/// <see cref="MaxAge"/> is looked up again (sources gain lyrics and loops over time).
/// </summary>
public sealed class BulkRunLedger
{
    public sealed record Entry(DateTime CheckedUtc, int Rank);

    private readonly string? _path;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<Guid, Entry> _entries;
    private readonly object _gate = new();

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>How long an entry counts (<see cref="MaxAge"/> unless set).</summary>
    public TimeSpan EntryLifetime { get; init; } = MaxAge;

    /// <param name="path">The JSON file; null keeps it in memory only.</param>
    public BulkRunLedger(string? path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.UtcNow);
        _entries = Load(path);
    }

    private static Dictionary<Guid, Entry> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "BulkLedger.LoadFailed", ex.Message);
        }
        return new();
    }

    /// <summary>True when <paramref name="id"/> was checked within <see cref="MaxAge"/> and found
    /// nothing better than <paramref name="currentRank"/> (so asking again is wasted).</summary>
    public bool CheckedRecently(Guid id, int currentRank)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var e) && _now() - e.CheckedUtc < EntryLifetime && e.Rank <= currentRank;
    }

    public void Record(Guid id, int rank)
    {
        lock (_gate) _entries[id] = new Entry(_now(), rank);
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Writes the ledger (atomically, through a temp file); stale entries are dropped.</summary>
    public void Save()
    {
        if (_path is null) return;
        try
        {
            string json;
            lock (_gate)
            {
                var cutoff = _now() - EntryLifetime;
                json = JsonSerializer.Serialize(_entries.Where(kv => kv.Value.CheckedUtc >= cutoff).ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "BulkLedger.SaveFailed", ex.Message);
        }
    }
}
