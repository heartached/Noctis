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
        public readonly string Key;
        public readonly string Path;
        public readonly long Bytes; // approximate decoded size (W*H*4)
        public long LastAccess; // atomic via Interlocked

        public CacheEntry(string key, string path, Bitmap bitmap, long accessCounter)
        {
            Key = key;
            Path = path;
            Bitmap = bitmap;
            LastAccess = accessCounter;
            try
            {
                var px = bitmap.PixelSize;
                Bytes = Math.Max(1L, (long)px.Width * px.Height * 4);
            }
            catch { Bytes = 1L; }
        }
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static long _accessCounter;
    private static long _totalBytes; // atomic via Interlocked — approximate resident size
    private static int _evictLock; // 0 = free, 1 = held — used with Monitor.TryEnter pattern via Interlocked

    // Bound the cache by resident bytes (the dominant cost on large libraries:
    // a 512px RGBA bitmap is ~1 MB, so an entry-count cap alone let the cache
    // grow to >1 GB during a full grid scroll). Keep a generous entry-count
    // backstop as well.
    private const int MaxCacheSize = 2000;
    private const long MaxCacheBytes = 256L * 1024 * 1024; // 256 MB
    private const int EvictBatchSize = 200;
    private const int DecodeWidth = 512;

    /// <summary>
    /// Returns a cached bitmap if available, or null on cache miss. No I/O performed.
    /// Lock-free on the hot path.
    /// </summary>
    public static Bitmap? TryGet(string path)
        => TryGet(path, DecodeWidth);

    public static Bitmap? TryGet(string path, int decodeWidth)
    {
        var key = BuildKey(path, decodeWidth);
        if (Cache.TryGetValue(key, out var entry))
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
        foreach (var key in Cache.Keys.Where(k => k.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
        {
            if (Cache.TryRemove(key, out var removed))
                Interlocked.Add(ref _totalBytes, -removed.Bytes);
        }
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
        => LoadAndCache(path, DecodeWidth);

    public static Bitmap? LoadAndCache(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var width = NormalizeDecodeWidth(decodeWidth);
            var key = BuildKey(path, width);

            // Double-check: another thread may have cached this while we waited for I/O to start
            if (Cache.TryGetValue(key, out var hit))
            {
                Interlocked.Increment(ref hit.LastAccess);
                return hit.Bitmap;
            }

            Bitmap bitmap;
            using (var stream = File.OpenRead(path))
                bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);

            var counter = Interlocked.Increment(ref _accessCounter);
            var newEntry = new CacheEntry(key, path, bitmap, counter);

            if (!Cache.TryAdd(key, newEntry))
            {
                // Another thread won the race — discard our decode
                bitmap.Dispose();
                if (Cache.TryGetValue(key, out var existing))
                {
                    Interlocked.Increment(ref existing.LastAccess);
                    return existing.Bitmap;
                }
                return null;
            }
            Interlocked.Add(ref _totalBytes, newEntry.Bytes);

            // Evict if over capacity — non-blocking; skip if another thread is already evicting
            if ((Cache.Count > MaxCacheSize || Interlocked.Read(ref _totalBytes) > MaxCacheBytes) &&
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
        // Evict oldest-accessed entries until both the entry-count and byte budgets
        // are satisfied (always drop at least one batch so a single huge bitmap that
        // blew the byte budget on its own still triggers cleanup of older entries).
        var ordered = Cache.Values.OrderBy(e => Interlocked.Read(ref e.LastAccess)).ToList();
        var dropped = 0;
        foreach (var entry in ordered)
        {
            if (dropped >= EvictBatchSize &&
                Cache.Count <= MaxCacheSize &&
                Interlocked.Read(ref _totalBytes) <= MaxCacheBytes)
                break;

            if (Cache.TryRemove(entry.Key, out var removed))
            {
                Interlocked.Add(ref _totalBytes, -removed.Bytes);
                dropped++;
            }
        }
        // We intentionally do not dispose bitmaps here; UI controls may still hold references.
    }

    private static string BuildKey(string path, int decodeWidth)
        => $"{NormalizeDecodeWidth(decodeWidth)}|{path}";

    private static int NormalizeDecodeWidth(int decodeWidth)
        => decodeWidth <= 0 ? DecodeWidth : Math.Clamp(decodeWidth, 64, 1024);
}
