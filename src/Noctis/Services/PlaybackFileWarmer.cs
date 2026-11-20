using System.Diagnostics;

namespace Noctis.Services;

/// <summary>
/// Reads the playing song's file once, in the background, so the operating system holds it in
/// its file cache. A seek into a part of the file that was never read otherwise waits on the
/// disk: the demuxer probes several places to find the frame, and on a hard disk each probe is
/// a head move. Measured 11-19 on a hard-disk library (official 1.7.0, 54 timeline clicks in one
/// song): audio back in 5 ms when the spot was already read, 100-722 ms in 8 of them; a cold
/// file seeked from the test harness went silent for 315 ms. One sequential read of the file
/// costs the disk less than a second and makes every later seek in that song a memory read.
/// </summary>
internal sealed class PlaybackFileWarmer : IDisposable
{
    /// <summary>Playback starts first: the song's own first read is not made to share the disk.</summary>
    internal const int StartDelayMs = 1500;

    /// <summary>Longer files (audiobooks, DSD albums in one file) are left alone: reading
    /// gigabytes to save a seek would push other things out of the cache.</summary>
    internal const long MaxBytes = 768L * 1024 * 1024;

    private const int ChunkBytes = 1 << 20;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _skipDelay; // cancelled by StartNow: the pass reads at once
    private string? _path;
    private readonly int _startDelayMs;

    internal PlaybackFileWarmer(int startDelayMs = StartDelayMs) => _startDelayMs = startDelayMs;

    /// <summary>The read of the last file given to <see cref="Warm"/>; completed when nothing runs. For tests.</summary>
    internal Task Current { get; private set; } = Task.CompletedTask;

    /// <summary>Bytes read by the last finished or running pass. For tests.</summary>
    internal long BytesRead => Interlocked.Read(ref _bytesRead);
    private long _bytesRead;

    /// <summary>
    /// Starts reading <paramref name="path"/> (a local file) after a short delay and drops the
    /// read of any other file. The same path again changes nothing, so a restart or a seek that
    /// reopens the song does not read it twice. Null or a path that is not a file stops it.
    /// </summary>
    internal void Warm(string? path)
    {
        lock (_gate)
        {
            if (path != null && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _skipDelay = null;
            _path = path;
            if (string.IsNullOrEmpty(path)) { Current = Task.CompletedTask; return; }
            var cts = _cts = new CancellationTokenSource();
            var skip = _skipDelay = new CancellationTokenSource();
            // Tokens taken here: a Warm right after this one disposes the source before the pass runs.
            var token = cts.Token;
            var skipToken = skip.Token;
            Current = Task.Run(() => ReadThrough(path, token, skipToken));
        }
    }

    /// <summary>
    /// A seek in the song being warmed: start its read now instead of after the start delay.
    /// A far seek right after a cold start otherwise waits on the disk (harness 11-19, hard
    /// disk: 70-354 ms at 0.7-1.3 s into the song against 6-8 ms once the file was read).
    /// Nothing waiting (already read, or no file): no effect.
    /// </summary>
    internal void StartNow()
    {
        lock (_gate)
        {
            try { _skipDelay?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadThrough(string path, CancellationToken ct, CancellationToken skipDelay)
    {
        try
        {
            Interlocked.Exchange(ref _bytesRead, 0);
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, skipDelay);
                await Task.Delay(_startDelayMs, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* StartNow */ }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return;

            var started = Stopwatch.GetTimestamp();
            // Shared for everything: tag edits, moves and deletes of the playing file go on working.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                Interlocked.Add(ref _bytesRead, read);

            DebugLogger.Info(DebugLogger.Category.Playback, "FileWarm.Done",
                $"file={Path.GetFileName(path)}, mb={BytesRead / (1024 * 1024)}, ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A file that cannot be read this way plays as before; nothing to report.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _skipDelay = null;
            _path = null;
        }
    }
}
