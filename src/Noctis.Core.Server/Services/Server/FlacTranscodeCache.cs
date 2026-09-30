using System.Collections.Concurrent;
using System.Globalization;

namespace Noctis.Services.Server;

/// <summary>
/// Disk cache in front of an ALAC → FLAC transcoder, for the server's <c>stream</c>/<c>download</c>
/// with <c>format=flac</c>: many Android phones have no ALAC decoder, and FLAC plays everywhere
/// (lossless both ways, so nothing is lost). Its <see cref="GetAsync"/> is what
/// <see cref="NoctisServer"/> takes as its <c>flacTranscoder</c>.
///
/// One file per (track, source size, source mtime): <c>&lt;guid N&gt;-&lt;size&gt;-&lt;ticks&gt;.flac</c>,
/// named only from the library's Guid and numbers. Written as a unique <c>.part</c> then moved,
/// so a reader never sees half a file. Single-flight per file: parallel requests (the player's
/// retry, a download next to a stream) share one transcode, which runs to the end even when the
/// request that started it goes away. At most <see cref="MaxConcurrent"/> transcodes run at once
/// and <see cref="MaxPending"/> wait or run; above the size cap the least recently served files go first.
/// </summary>
public sealed class FlacTranscodeCache
{
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxConcurrent = 2;

    /// <summary>
    /// Most distinct transcodes waiting or running at once. Past it a new one is refused (null:
    /// the server sends the original), so a client asking for every ALAC song and hanging up
    /// cannot queue hours of ffmpeg work. A phone needs about three (a stream, two downloads).
    /// </summary>
    public const int MaxPending = 8;

    /// <summary>A .part this old is a crashed run's leftover, not a transcode in progress.</summary>
    private static readonly TimeSpan StalePartAge = TimeSpan.FromHours(1);

    private readonly string _directory;
    private readonly Func<string, string, CancellationToken, Task<bool>> _transcode;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trimGate = new();
    private int _pending;

    /// <param name="directory">Cache folder (the desktop's is &lt;DataRoot&gt;/server/transcode).</param>
    /// <param name="transcode">(source path, target path, ct) → true when a complete FLAC was written to target.</param>
    /// <param name="maxBytes">Cache size cap.</param>
    public FlacTranscodeCache(string directory, Func<string, string, CancellationToken, Task<bool>> transcode, long maxBytes = DefaultMaxBytes)
    {
        _directory = Path.GetFullPath(directory);
        _transcode = transcode;
        _maxBytes = maxBytes;
    }

    /// <summary>Path of the cached FLAC for the track's current file, making it first when needed; null when it cannot be made.</summary>
    public Task<string?> GetAsync(Guid trackId, string sourcePath, CancellationToken ct)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists) return Task.FromResult<string?>(null);

        var prefix = trackId.ToString("N") + "-";
        var name = prefix + source.Length.ToString(CultureInfo.InvariantCulture) + "-"
            + source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ".flac";
        var target = Path.Combine(_directory, name);
        if (File.Exists(target))
        {
            Touch(target);
            return Task.FromResult<string?>(target);
        }

        if (!_running.TryGetValue(name, out var run))
        {
            if (Interlocked.Increment(ref _pending) > MaxPending)
            {
                Interlocked.Decrement(ref _pending);
                DebugLogger.Warn(DebugLogger.Category.State, "Server", "flac transcode refused: too many pending");
                return Task.FromResult<string?>(null);
            }
            // Lazy: only the stored one starts (its run gives the slot back when it ends); a
            // request that lost the race joins the stored run and gives its own slot back now.
            var mine = new Lazy<Task<string?>>(() => RunAsync(sourcePath, target, prefix), LazyThreadSafetyMode.ExecutionAndPublication);
            run = _running.GetOrAdd(name, mine);
            if (!ReferenceEquals(run, mine)) Interlocked.Decrement(ref _pending);
        }
        var task = run.Value;
        _ = task.ContinueWith(_ => _running.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(name, run)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private async Task<string?> RunAsync(string sourcePath, string target, string prefix)
    {
        try { return await TranscodeOnceAsync(sourcePath, target, prefix).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _pending); }
    }

    private async Task<string?> TranscodeOnceAsync(string sourcePath, string target, string prefix)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(target)) return target;
            Directory.CreateDirectory(_directory);
            var part = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                bool ok;
                try { ok = await _transcode(sourcePath, part, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    DebugLogger.Warn(DebugLogger.Category.State, "Server", $"flac transcode failed: {ex.GetType().Name}");
                    ok = false;
                }
                var made = new FileInfo(part);
                if (!ok || !made.Exists || made.Length == 0) return null;
                File.Move(part, target, overwrite: true);
            }
            finally { TryDelete(part); }

            DeleteOtherVersions(prefix, target);
            Trim(target);
            return target;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Older transcodes of this track (its file has since changed) are dead weight.</summary>
    private void DeleteOtherVersions(string prefix, string keep)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(_directory, prefix + "*.flac"))
                if (!string.Equals(stale, keep, StringComparison.OrdinalIgnoreCase)) TryDelete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Least recently served first until the folder fits the cap; never the file just made.</summary>
    private void Trim(string keep)
    {
        lock (_trimGate)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var part in Directory.EnumerateFiles(_directory, "*.part"))
                    if (now - File.GetLastWriteTimeUtc(part) > StalePartAge) TryDelete(part);

                var files = new DirectoryInfo(_directory).EnumerateFiles("*.flac").ToList();
                var total = files.Sum(f => f.Length);
                foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (total <= _maxBytes) break;
                    if (string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    // A file being served cannot be deleted on Windows; it stays until a later trim.
                    if (TryDelete(f.FullName)) total -= f.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Marks a hit as recently served, so the trim keeps what the phone actually plays. The
    /// access time, not the write time: that one is the response's Last-Modified.</summary>
    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
