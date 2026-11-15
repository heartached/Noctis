using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Settings ▸ Lyrics ▸ "Find lyrics for new songs" (GitHub #195). Songs the library meets for the
/// first time (a scan's new files, the folder watcher's imports) are collected until no more have
/// arrived for <see cref="QuietPeriod"/>, then looked up in the background by the bulk lyrics
/// service — the same sources, order, pacing (two songs in flight, each source spaced and backed
/// off when busy), save rules and resume ledger as Find Lyrics. One run at a time; songs arriving
/// during a run wait for the next one. Off by default; nothing happens with the setting off.
/// GitHub #219: "Find lyrics for the whole library" also queues every song without synced lyrics a
/// while after start-up and once a day after that. The bulk service's ledger keeps a song nothing
/// was found for from being asked again for a month (so misses are retried now and then, not
/// daily); batches wait <see cref="BatchPause"/> apart, and while the computer is offline the
/// songs stay queued and the run tries again every <see cref="OfflineRetry"/>.
/// </summary>
public sealed class LyricsAutoFindService : IDisposable
{
    /// <summary>Most songs handed to one run; the rest follow in the next.</summary>
    internal const int MaxPerRun = 200;

    private readonly ILyricsBulkService _bulk;
    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();
    private readonly List<Track> _pending = new();
    private readonly HashSet<Guid> _pendingIds = new();
    // Of _pendingIds: queued by the library sweep, not as new songs (dropped when it is turned off).
    private readonly HashSet<Guid> _sweepIds = new();
    private Timer? _timer;
    private Timer? _sweepTimer;
    private CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private bool _running;
    private bool _disposed;

    /// <summary>How long after the last new song a run starts.</summary>
    public TimeSpan QuietPeriod { get; }

    public LyricsAutoFindService(ILyricsBulkService bulk, ILibraryService library, Func<AppSettings> settings, TimeSpan? quietPeriod = null)
    {
        _bulk = bulk;
        _library = library;
        _settings = settings;
        QuietPeriod = quietPeriod ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>First library sweep after start-up or after the setting is turned on.</summary>
    public TimeSpan SweepDelay { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Between two library sweeps.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Between two batches of one run, so a big library is looked up gradually.</summary>
    public TimeSpan BatchPause { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How often an offline run checks the connection again.</summary>
    public TimeSpan OfflineRetry { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Test seam: is the computer online.</summary>
    internal Func<bool> IsOnline { get; init; } = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;

    /// <summary>Songs waiting for the next run (tests, diagnostics).</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    // New songs are looked up with either setting on: the library one covers them too.
    private bool Enabled
    {
        get
        {
            try { var s = _settings(); return s.LyricsAutoFindNew || s.LyricsAutoFindLibrary; }
            catch { return false; }
        }
    }

    private bool LibraryEnabled
    {
        get
        {
            try { return _settings().LyricsAutoFindLibrary; }
            catch { return false; }
        }
    }

    /// <summary>Starts listening for new songs, and schedules the first library sweep.</summary>
    public void Attach()
    {
        _library.TracksAdded += OnTracksAdded;
        ScheduleLibrarySweep();
    }

    /// <summary>GitHub #219: a library sweep after <see cref="SweepDelay"/> (start-up, setting turned on). The
    /// setting is read when the sweep starts (it is saved after the switch moves), so with it off nothing happens.</summary>
    public void ScheduleLibrarySweep()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_disposed) return;
            _sweepTimer ??= new Timer(_ => _ = SweepAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _sweepTimer.Change(SweepDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Queues every local song without synced lyrics (the bulk lookup then skips what already has
    /// the preferred type, and what it looked up lately) and runs them; the next sweep follows after
    /// <see cref="SweepInterval"/>. Internal for tests.
    /// </summary>
    internal Task SweepAsync()
    {
        if (_disposed || !LibraryEnabled) return Task.CompletedTask;
        IReadOnlyList<Track> tracks;
        try
        {
            var candidates = _library.Tracks.Where(t => IsCandidate(t) && string.IsNullOrWhiteSpace(t.SyncedLyrics)).ToList();
            // Only songs never looked up or due again: the rest would end "checked before" after a
            // read of their lyrics files, every day, for nothing.
            tracks = _bulk.WithoutRecentLookups(candidates);
        }
        catch { tracks = Array.Empty<Track>(); }
        var added = 0;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            foreach (var t in tracks)
            {
                if (!IsCandidate(t) || !string.IsNullOrWhiteSpace(t.SyncedLyrics) || !_pendingIds.Add(t.Id)) continue;
                _pending.Add(t);
                _sweepIds.Add(t.Id);
                added++;
            }
            _sweepTimer?.Change(SweepInterval, Timeout.InfiniteTimeSpan);
        }
        if (added > 0) DebugLog.Write("Lyrics", $"Auto find: library sweep queued {added} song(s)");
        return TickAsync();
    }

    /// <summary>The library setting was turned off: its waiting songs are dropped (new songs stay) and no sweep follows.</summary>
    public void StopLibrarySweep()
    {
        lock (_gate)
        {
            _sweepTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_sweepIds.Count == 0) return;
            _pending.RemoveAll(t => _sweepIds.Contains(t.Id));
            _pendingIds.ExceptWith(_sweepIds);
            _sweepIds.Clear();
        }
    }

    private void OnTracksAdded(object? sender, IReadOnlyList<Track> tracks) => Enqueue(tracks);

    /// <summary>Queues new songs and (re)starts the quiet-period wait. Nothing happens with the setting off.</summary>
    public void Enqueue(IReadOnlyList<Track> tracks)
    {
        if (_disposed || tracks.Count == 0 || !Enabled) return;
        lock (_gate)
        {
            if (_disposed) return;
            var added = 0;
            foreach (var t in tracks)
            {
                if (!IsCandidate(t) || !_pendingIds.Add(t.Id)) continue;
                _pending.Add(t);
                added++;
            }
            if (added == 0) return;
            _timer ??= new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
            DebugLog.Write("Lyrics", $"Auto find: {added} new song(s) waiting");
        }
    }

    private static bool IsCandidate(Track t) =>
        t.SourceType == SourceType.Local
        && !t.NoLyrics
        && !string.IsNullOrWhiteSpace(t.FilePath)
        // The app's own temp copies (MetadataService.SaveTagsAtomicallyOrThrow).
        && !Path.GetFileName(t.FilePath).StartsWith(".noctis-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the waiting songs through the bulk service, a batch at a time. Internal for tests.</summary>
    internal Task TickAsync()
    {
        lock (_gate)
        {
            if (_running || _disposed) return _run;
            _running = true;
            return _run = RunAsync(_cts.Token);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<Track> batch;
                lock (_gate)
                {
                    if (!Enabled) { _pending.Clear(); _pendingIds.Clear(); _sweepIds.Clear(); }
                    if (_pending.Count == 0) { _running = false; return; }
                }
                // Offline: the songs stay queued and the run tries again later (GitHub #219).
                if (!SafeIsOnline())
                {
                    lock (_gate)
                    {
                        _timer ??= new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
                        _timer.Change(OfflineRetry, Timeout.InfiniteTimeSpan);
                    }
                    DebugLog.Write("Lyrics", "Auto find: offline, waiting");
                    return;
                }
                lock (_gate)
                {
                    batch = _pending.Take(MaxPerRun).ToList();
                    _pending.RemoveRange(0, batch.Count);
                    foreach (var t in batch) { _pendingIds.Remove(t.Id); _sweepIds.Remove(t.Id); }
                }
                // Default options: songs without lyrics and with plain ones are looked up, a
                // richer find is saved, synced lyrics are left alone (as Find Lyrics does).
                var s = await _bulk.FetchAsync(batch, new LyricsBulkOptions(), null, ct).ConfigureAwait(false);
                DebugLog.Write("Lyrics", $"Auto find: {batch.Count} song(s) — {s.Synced} synced, {s.PlainOnly} plain, " +
                    $"{s.NotFound} not found, {s.Skipped} kept, {s.Failed} failed");
                bool more;
                lock (_gate) more = _pending.Count > 0;
                if (more && BatchPause > TimeSpan.Zero) await Task.Delay(BatchPause, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* turned off or shutting down */ }
        catch (Exception ex)
        {
            DebugLog.Write("Lyrics", $"Auto find failed: {ex.Message}");
        }
        finally
        {
            lock (_gate) _running = false;
        }
    }

    private bool SafeIsOnline()
    {
        try { return IsOnline(); }
        catch { return true; }
    }

    /// <summary>Drops the waiting songs and stops a running lookup (setting turned off, Reset
    /// Everything); what was saved stays saved. Returns the stopped run, to wait for.</summary>
    public Task ClearAsync()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingIds.Clear();
            _sweepIds.Clear();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts = new CancellationTokenSource();
            return _run;
        }
    }

    /// <summary>Stops a run and waits for it to unwind, bounded by <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Task run;
        lock (_gate)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            run = _run;
        }
        try { await run.WaitAsync(timeout).ConfigureAwait(false); }
        catch { /* timed out, or the run's own failure (already logged) */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _library.TracksAdded -= OnTracksAdded;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _timer?.Dispose();
            _timer = null;
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            _pending.Clear();
            _pendingIds.Clear();
            _sweepIds.Clear();
        }
    }
}
