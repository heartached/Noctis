using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Settings → Audio → Output → ReplayGain → "Scan new files automatically" (GitHub #148). Songs
/// the library meets for the first time — the folder watcher's imports and a library scan's
/// new files (startup included) — wait per album in a <see cref="ReplayGainAutoScanQueue"/>
/// until the copy has finished, then the album is measured and tagged in the background
/// through the same <see cref="IReplayGainScannerService"/> the scanner dialog uses.
/// <list type="bullet">
/// <item>Only new files without a REPLAYGAIN_TRACK_GAIN tag are written.</item>
/// <item>Album gain is over the whole album: songs of it the library already had are
/// measured too (read only) so one new bonus track gets the album's real gain, but their
/// files are left as they are.</item>
/// <item>One album at a time, ffmpeg at below-normal priority, and a write never makes the
/// player let go of a file: an album holding the playing song waits until it is not.</item>
/// </list>
/// </summary>
public sealed class ReplayGainAutoScanService : IDisposable
{
    /// <summary>How often waiting albums are checked.</summary>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(5);

    // Albums handed over per tick; the rest wait for the next one.
    private const int MaxAlbumsPerTick = 16;

    private readonly IReplayGainScannerService _scanner;
    private readonly ILibraryService _library;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string?> _playingPath;
    private readonly Action? _onTagsWritten;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, bool> _hasReplayGainTags;
    private readonly Func<PendingRgAlbum, bool> _isSettled;
    private readonly TimeSpan _tickInterval;

    private readonly object _gate = new();
    private readonly ReplayGainAutoScanQueue _queue;
    // Size + last write per file at the previous settle check (see IsSettledOnDisk).
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> _lastSeen = new(PathComparison.Comparer);
    // Albums already logged as waiting for the playing song, so the wait is logged once.
    private readonly HashSet<Guid> _heldForPlayback = new();
    private Timer? _timer;
    private int _ticking;
    private bool _disposed;
    private CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;

    public ReplayGainAutoScanService(
        IReplayGainScannerService scanner,
        ILibraryService library,
        Func<AppSettings> settings,
        Func<string?>? playingPath = null,
        Action? onTagsWritten = null,
        Func<DateTime>? clock = null,
        TimeSpan? quietPeriod = null,
        TimeSpan? tickInterval = null,
        Func<string, bool>? hasReplayGainTags = null,
        Func<PendingRgAlbum, bool>? isSettled = null)
    {
        _scanner = scanner;
        _library = library;
        _settings = settings;
        _playingPath = playingPath ?? (() => null);
        _onTagsWritten = onTagsWritten;
        _clock = clock ?? (() => DateTime.UtcNow);
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _hasReplayGainTags = hasReplayGainTags ?? HasReplayGainTags;
        _isSettled = isSettled ?? IsSettledOnDisk;
        _queue = new ReplayGainAutoScanQueue(quietPeriod);
    }

    /// <summary>Albums waiting for their copy to finish (tests, diagnostics).</summary>
    public int PendingAlbumCount { get { lock (_gate) return _queue.Count; } }

    private bool Enabled
    {
        get
        {
            try { return _settings().AutoReplayGainScanEnabled; }
            catch { return false; }
        }
    }

    /// <summary>Starts listening for new songs.</summary>
    public void Attach() => _library.TracksAdded += OnTracksAdded;

    private void OnTracksAdded(object? sender, IReadOnlyList<Track> tracks) => Enqueue(tracks);

    /// <summary>Queues new songs. Nothing happens with the setting off.</summary>
    public void Enqueue(IReadOnlyList<Track> tracks)
    {
        if (_disposed || tracks.Count == 0 || !Enabled) return;
        var local = tracks.Where(IsCandidate).ToList();
        if (local.Count == 0) return;

        var now = _clock();
        lock (_gate)
        {
            if (_disposed) return;
            _queue.Add(local, now);
            foreach (var t in local) Observe(t.FilePath);
            _timer ??= new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_tickInterval, _tickInterval);
        }
        DebugLog.Write("ReplayGain", $"Auto scan: {local.Count} new file(s) waiting " +
            $"({_queue.QuietPeriod.TotalSeconds:0} s after the last one arrives)");
    }

    private static bool IsCandidate(Track t) =>
        t.SourceType == SourceType.Local
        && !string.IsNullOrWhiteSpace(t.FilePath)
        // The app's own temp copies (MetadataService.SaveTagsAtomicallyOrThrow).
        && !Path.GetFileName(t.FilePath).StartsWith(".noctis-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops every waiting album and stops a running scan before its next file
    /// (the setting was turned off).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _lastSeen.Clear();
            _heldForPlayback.Clear();
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Hands ready albums to the scanner, one at a time. Runs on the timer; a tick that
    /// finds the previous one still scanning returns at once. Internal for tests.
    /// </summary>
    internal Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return Task.CompletedTask;
        var run = _run = RunTickAsync();
        return run;
    }

    private async Task RunTickAsync()
    {
        try
        {
            if (!Enabled)
            {
                Clear();
                return;
            }

            List<PendingRgAlbum> ready;
            CancellationToken ct;
            lock (_gate)
            {
                if (_disposed) return;
                ready = _queue.TakeReady(_clock(), _isSettled, MaxAlbumsPerTick);
                ct = _cts.Token;
            }

            foreach (var album in ready)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await ProcessAlbumAsync(album, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One album's failure must not drop the others taken with it.
                    DebugLog.Write("ReplayGain", $"Auto scan: \"{album.AlbumName}\" failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* turned off or shutting down */ }
        catch (Exception ex)
        {
            DebugLog.Write("ReplayGain", $"Auto scan failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (_queue.IsEmpty) _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task ProcessAlbumAsync(PendingRgAlbum album, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(album.AlbumName) ? "(no album)" : album.AlbumName;

        // New files that still exist and carry no ReplayGain yet. Tag reads are file IO;
        // this already runs on the thread pool.
        var toWrite = new HashSet<Track>(ReferenceEqualityComparer.Instance);
        var alreadyTagged = 0;
        foreach (var t in album.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(t.FilePath)) continue;
            if (_hasReplayGainTags(t.FilePath)) alreadyTagged++;
            else toWrite.Add(t);
        }
        if (toWrite.Count == 0)
        {
            DebugLog.Write("ReplayGain", $"Auto scan: \"{name}\" skipped, {alreadyTagged} file(s) already tagged");
            return;
        }

        // The playing song can't be rewritten without a gap (Windows refuses to replace an
        // open file, and a background write must not make the player let go of it).
        var playing = _playingPath();
        if (!string.IsNullOrEmpty(playing) && toWrite.Any(t => PathComparison.Comparer.Equals(t.FilePath, playing)))
        {
            lock (_gate)
            {
                if (_disposed) return;
                _queue.Add(album.Tracks, _clock());
                if (!_heldForPlayback.Add(album.Key)) return;
            }
            DebugLog.Write("ReplayGain", $"Auto scan: \"{name}\" waits, one of its songs is playing");
            return;
        }
        lock (_gate) { _heldForPlayback.Remove(album.Key); }

        var members = AlbumMembers(album, toWrite);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = await _scanner.ScanAsync(
            members,
            albumMode: true,
            new Progress<ScanProgress>(_ => { }),
            ct,
            new ReplayGainScanOptions { ShouldWrite = toWrite.Contains, Background = true }).ConfigureAwait(false);

        DebugLog.Write("ReplayGain",
            $"Auto scan: \"{name}\" tagged {summary.Scanned}/{toWrite.Count} new file(s)"
            + (summary.Failed > 0 ? $", {summary.Failed} failed" : string.Empty)
            + (alreadyTagged > 0 ? $", {alreadyTagged} already tagged" : string.Empty)
            + $", album gain over {members.Count} song(s), {sw.Elapsed.TotalSeconds:0.0} s");

        if (summary.Scanned > 0)
        {
            // The scan put the written gains on the tracks (Songs list ReplayGain column,
            // GitHub #151); keep them across a restart.
            try { await _library.SaveAsync().ConfigureAwait(false); } catch { /* best effort */ }
            try { _onTagsWritten?.Invoke(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The songs the album gain is computed over: the new files plus the songs of the same
    /// album the library already had (read only). Measuring only the new files would give an
    /// album gain for "these few songs", wrong for every album that grows by a track.
    /// </summary>
    private List<Track> AlbumMembers(PendingRgAlbum album, HashSet<Track> toWrite)
    {
        var members = new List<Track>(toWrite);
        // New files that already carry tags are still part of the album: they come back in
        // from the library below like its older songs.
        var paths = new HashSet<string>(toWrite.Select(t => t.FilePath), PathComparison.Comparer);
        // A single without a real album name is its own album (AlbumGroupKey = its id).
        if (!album.Tracks.Any(t => ReplayGainScannerService.AlbumGroupKey(t) == t.Id))
        {
            IReadOnlyList<Track> all;
            try { all = _library.AllTracks.ToList(); }
            catch (InvalidOperationException) { all = Array.Empty<Track>(); } // mutated mid-copy
            foreach (var t in all)
            {
                if (t.SourceType != SourceType.Local || ReplayGainScannerService.AlbumGroupKey(t) != album.Key) continue;
                if (!paths.Add(t.FilePath) || !File.Exists(t.FilePath)) continue;
                members.Add(t);
            }
        }
        return members;
    }

    /// <summary>
    /// True once nothing in the album's folders is still being copied: every audio file
    /// there has kept its size and last-write time since the previous check, opens without
    /// a sharing violation, and is in the library (the watcher imports a file only after its
    /// copy finished, so an unknown one is usually still arriving).
    /// </summary>
    private bool IsSettledOnDisk(PendingRgAlbum album)
    {
        var settled = true;
        foreach (var dir in album.Directories)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir)
                    .Where(f => MetadataService.SupportedExtensions.Contains(Path.GetExtension(f))
                                && !Path.GetFileName(f).StartsWith(".noctis-", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                continue; // folder vanished or unreadable — nothing to wait for
            }

            foreach (var file in files)
            {
                // Observe every file, not just the first unsettled one, so each needs one
                // more check, not one per file.
                if (!Observe(file)) settled = false;
                else if (!CanOpen(file)) settled = false;
                else if (_library.GetTrackById(LibraryService.TrackIdForPath(file)) == null) settled = false;
            }
        }
        return settled;
    }

    /// <summary>Records the file's size + last write; true when they match the previous record.</summary>
    private bool Observe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { _lastSeen.Remove(path); return true; }
            var now = (fi.Length, fi.LastWriteTimeUtc);
            var same = _lastSeen.TryGetValue(path, out var before) && before == now;
            _lastSeen[path] = now;
            return same;
        }
        catch
        {
            return true; // unreadable metadata — don't hold the album for it
        }
    }

    /// <summary>A writer still holding the file makes this open fail (see LibraryWatcherService.IsFileReady).</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException) { return false; }
        catch { return true; }
    }

    private static bool HasReplayGainTags(string path)
    {
        try { return !string.IsNullOrWhiteSpace(AdvancedTagIO.ReadAll(path).ReplayGainTrackGain); }
        catch { return false; } // unreadable — the scan reports its own failure
    }

    /// <summary>
    /// Stops the scan and waits for it to unwind, bounded by <paramref name="timeout"/>: the
    /// tag write is a synchronous file rewrite, so the process must not exit midway.
    /// </summary>
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
            _queue.Clear();
        }
    }
}
