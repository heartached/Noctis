using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace Noctis.Services;

/// <summary>
/// Thread-safe LRU bitmap cache shared across the application.
/// Uses ConcurrentDictionary for lock-free reads on cache hits.
/// Decodes artwork at thumbnail size (512px) to balance sharpness and memory.
/// </summary>
public static class ArtworkCache
{
    private sealed class CacheEntry
    {
        public readonly Bitmap Bitmap;
        public readonly string Path;
        public long LastAccess; // atomic via Interlocked

        public CacheEntry(string path, Bitmap bitmap, long accessCounter)
        {
            Path = path;
            Bitmap = bitmap;
            LastAccess = accessCounter;
        }
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static long _accessCounter;
    private static int _evictLock; // 0 = free, 1 = held — used with Monitor.TryEnter pattern via Interlocked

    private const int MaxCacheSize = 2000;
    private const int EvictBatchSize = 200;
    private const int DecodeWidth = 512;

    /// <summary>
    /// Returns a cached bitmap if available, or null on cache miss. No I/O performed.
    /// Lock-free on the hot path.
    /// </summary>
    public static Bitmap? TryGet(string path)
    {
        if (Cache.TryGetValue(path, out var entry))
        {
            Interlocked.Increment(ref entry.LastAccess);
            return entry.Bitmap;
        }
        return null;
    }

    /// <summary>
    /// Removes a cached bitmap for the given path so the next load reads fresh data from disk.
    /// Does not dispose the bitmap — existing UI controls may still reference it.
    /// </summary>
    public static void Invalidate(string path)
    {
        Cache.TryRemove(path, out _);
        Invalidated?.Invoke(path);
    }

    /// <summary>
    /// Raised after a cached entry is removed, allowing live UI controls to reload.
    /// </summary>
    public static event Action<string>? Invalidated;

    /// <summary>
    /// Loads a bitmap from disk, caches it, and returns it.
    /// Safe to call from any thread. Returns null if the file doesn't exist or can't be decoded.
    /// </summary>
    public static Bitmap? LoadAndCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // Double-check: another thread may have cached this while we waited for I/O to start
            if (Cache.TryGetValue(path, out var hit))
            {
                Interlocked.Increment(ref hit.LastAccess);
                return hit.Bitmap;
            }

            Bitmap bitmap;
            using (var stream = File.OpenRead(path))
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);

            var counter = Interlocked.Increment(ref _accessCounter);
            var newEntry = new CacheEntry(path, bitmap, counter);

            if (!Cache.TryAdd(path, newEntry))
            {
                // Another thread won the race — discard our decode
                bitmap.Dispose();
                if (Cache.TryGetValue(path, out var existing))
                {
                    Interlocked.Increment(ref existing.LastAccess);
                    return existing.Bitmap;
                }
                return null;
            }

            // Evict if over capacity — non-blocking; skip if another thread is already evicting
            if (Cache.Count > MaxCacheSize &&
                Interlocked.CompareExchange(ref _evictLock, 1, 0) == 0)
            {
                try { EvictOldest(); }
                finally { Interlocked.Exchange(ref _evictLock, 0); }
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void EvictOldest()
    {
        // Collect entries, sort by last access, evict the oldest batch
        var entries = Cache.Values.OrderBy(e => Interlocked.Read(ref e.LastAccess)).Take(EvictBatchSize);
        foreach (var entry in entries)
            Cache.TryRemove(entry.Path, out _);
        // We intentionally do not dispose bitmaps here; UI controls may still hold references.
    }
}
