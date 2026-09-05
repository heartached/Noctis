using System.Collections.Concurrent;

namespace Noctis.Services;

/// <summary>
/// Batches file-tag writes so rapid edits (clicking through star ratings, bulk lyrics
/// saves) touch each audio file once, after the user has gone quiet, and never while
/// that file is the one being played.
/// </summary>
public interface IDeferredTagWriter
{
    /// <summary>
    /// Queues <paramref name="write"/> for <paramref name="path"/>. Writes with the same
    /// path + key coalesce — only the latest runs. The write is executed on a worker thread
    /// once the quiet period has elapsed and the file is not in use.
    /// </summary>
    void Enqueue(string path, string key, Action write);

    /// <summary>Runs every pending write now, including files currently in use (shutdown).</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Returns the path of the file that must not be rewritten right now (the playing track), or null.</summary>
    Func<string?>? InUsePath { get; set; }

    int PendingCount { get; }
}

public sealed class DeferredTagWriter : IDeferredTagWriter, IDisposable
{
    /// <summary>Default quiet period between the last queued write and the flush.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(string Path, string Key), Action> _pending =
        new(new PathKeyComparer());
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Timer _timer;
    private readonly TimeSpan _quiet;
    private volatile bool _disposed;

    public Func<string?>? InUsePath { get; set; }

    public int PendingCount => _pending.Count;

    /// <summary>Raised after a flush pass with the number of writes performed (tests / diagnostics).</summary>
    public event Action<int>? Flushed;

    public DeferredTagWriter() : this(DefaultQuietPeriod) { }

    public DeferredTagWriter(TimeSpan quietPeriod)
    {
        _quiet = quietPeriod <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : quietPeriod;
        _timer = new Timer(_ => _ = FlushDueAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Enqueue(string path, string key, Action write)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path) || write is null) return;
        _pending[(path, key ?? string.Empty)] = write;
        // Restart the quiet window on every edit: a burst of star clicks writes once.
        try { _timer.Change(_quiet, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    /// <summary>Quiet period elapsed: write everything except the file in use.</summary>
    internal Task FlushDueAsync() => FlushCoreAsync(includeInUse: false, CancellationToken.None);

    public Task FlushAsync(CancellationToken ct = default) => FlushCoreAsync(includeInUse: true, ct);

    private async Task FlushCoreAsync(bool includeInUse, CancellationToken ct)
    {
        if (_disposed) return;
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var inUse = includeInUse ? null : SafeInUsePath();
            var deferredInUse = false;
            var written = 0;
            foreach (var entry in _pending.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (inUse != null && Helpers.PathComparison.Comparer.Equals(entry.Key.Path, inUse))
                {
                    deferredInUse = true;
                    continue;
                }
                // Remove first: a write queued while this one runs must survive to the next pass.
                if (!_pending.TryRemove(entry.Key, out var action)) continue;
                try
                {
                    await Task.Run(action, ct).ConfigureAwait(false);
                    written++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Warn(DebugLogger.Category.State, "TagWriter.Failed",
                        $"{Path.GetFileName(entry.Key.Path)}: {ex.Message}");
                }
            }
            if (written > 0)
                DebugLogger.Info(DebugLogger.Category.State, "TagWriter.Flushed", $"files={written}, deferredInUse={deferredInUse}");
            Flushed?.Invoke(written);
            // The playing file gets another chance once the user is quiet again.
            if (deferredInUse && !_disposed)
            {
                try { _timer.Change(_quiet, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private string? SafeInUsePath()
    {
        try { return InUsePath?.Invoke(); }
        catch { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed class PathKeyComparer : IEqualityComparer<(string Path, string Key)>
    {
        public bool Equals((string Path, string Key) x, (string Path, string Key) y) =>
            Helpers.PathComparison.Comparer.Equals(x.Path, y.Path) && string.Equals(x.Key, y.Key, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Key) obj) =>
            HashCode.Combine(Helpers.PathComparison.Comparer.GetHashCode(obj.Path), obj.Key);
    }
}
