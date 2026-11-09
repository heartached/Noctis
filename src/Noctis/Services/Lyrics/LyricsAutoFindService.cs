using Noctis.Models;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Settings ▸ Lyrics ▸ "Find lyrics for new songs" (GitHub #195). Songs the library meets for the
/// first time (a scan's new files, the folder watcher's imports) are collected until no more have
/// arrived for <see cref="QuietPeriod"/>, then looked up in the background by the bulk lyrics
/// service — the same sources, order, pacing (two songs in flight, each source spaced and backed
/// off when busy), save rules and resume ledger as Find Lyrics. One run at a time; songs arriving
/// during a run wait for the next one. Off by default; nothing happens with the setting off.
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
    private Timer? _timer;
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

    /// <summary>Songs waiting for the next run (tests, diagnostics).</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    private bool Enabled
    {
        get
        {
            try { return _settings().LyricsAutoFindNew; }
            catch { return false; }
        }
    }

    /// <summary>Starts listening for new songs.</summary>
    public void Attach() => _library.TracksAdded += OnTracksAdded;

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
                    if (!Enabled) { _pending.Clear(); _pendingIds.Clear(); }
                    if (_pending.Count == 0) { _running = false; return; }
                    batch = _pending.Take(MaxPerRun).ToList();
                    _pending.RemoveRange(0, batch.Count);
                    foreach (var t in batch) _pendingIds.Remove(t.Id);
                }
                // Default options: songs without lyrics and with plain ones are looked up, a
                // richer find is saved, synced lyrics are left alone (as Find Lyrics does).
                var s = await _bulk.FetchAsync(batch, new LyricsBulkOptions(), null, ct).ConfigureAwait(false);
                DebugLog.Write("Lyrics", $"Auto find: {batch.Count} song(s) — {s.Synced} synced, {s.PlainOnly} plain, " +
                    $"{s.NotFound} not found, {s.Skipped} kept, {s.Failed} failed");
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

    /// <summary>Drops the waiting songs and stops a running lookup (setting turned off, Reset
    /// Everything); what was saved stays saved. Returns the stopped run, to wait for.</summary>
    public Task ClearAsync()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingIds.Clear();
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
            _pending.Clear();
            _pendingIds.Clear();
        }
    }
}
