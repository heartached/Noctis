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

    /// <summary>Approximate resident bytes currently held by the cache (diagnostic).</summary>
    internal static long ResidentBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Number of cached bitmaps currently resident (diagnostic).</summary>
    internal static int Count => Cache.Count;

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
            Touch(entry);
            return entry.Bitmap;
        }
        return null;
    }

    /// <summary>
    /// Cross-width fallback: returns a cached bitmap for this path decoded at ANY width,
    /// or null when no width bucket holds it. No I/O, lock-free. Lets a surface whose
    /// exact bucket missed (e.g. a 128px playlist thumb when only the 768px album-grid
    /// decode exists) paint correct pixels immediately instead of blanking while the
    /// exact-width decode runs. Preference: smallest cached width ≥ requested (sharp
    /// downscale), else the largest cached width (least-blurry upscale).
    /// </summary>
    public static Bitmap? TryGetAnyWidth(string path, int decodeWidth)
    {
        var requested = NormalizeDecodeWidth(decodeWidth);
        CacheEntry? atLeast = null, below = null;
        int atLeastWidth = int.MaxValue, belowWidth = -1;
        foreach (var width in _observedWidths.Keys)
        {
            if (width == requested || !Cache.TryGetValue($"{width}|{path}", out var entry))
                continue;
            if (width >= requested)
            {
                if (width < atLeastWidth) { atLeastWidth = width; atLeast = entry; }
            }
            else if (width > belowWidth)
            {
                belowWidth = width; below = entry;
            }
        }

        var chosen = atLeast ?? below;
        if (chosen == null)
            return null;
        Touch(chosen);
        return chosen.Bitmap;
    }

    /// <summary>
    /// Stamps the entry with the current global access counter. Entries created later
    /// start with a much larger counter value, so merely incrementing an entry's own
    /// stamp by 1 per hit left old-but-hot entries (the on-screen art) sorting older
    /// than fresh one-shot decodes — the LRU evicted exactly the wrong bitmaps.
    /// </summary>
    private static void Touch(CacheEntry entry)
        => Interlocked.Exchange(ref entry.LastAccess, Interlocked.Increment(ref _accessCounter));

    /// <summary>
    /// Removes a cached bitmap for the given path so the next load reads fresh data from disk.
    /// Does not dispose the bitmap — existing UI controls may still reference it.
    /// </summary>
    public static void Invalidate(string path)
    {
        // Targeted removal against the decode widths actually in use, instead of
        // enumerating Cache.Keys — that materialised a fresh List of up to MaxCacheSize
        // (2000) keys on every call, and saving metadata for a multi-track selection
        // calls this once per track.
        //
        // The width set is observed at insert time rather than hardcoded, so a new
        // DecodeWidth added in XAML can't silently escape invalidation.
        foreach (var width in _observedWidths.Keys)
        {
            if (Cache.TryRemove(BuildKey(path, width), out var removed))
                OnEntryRemoved(removed);
        }
        Invalidated?.Invoke(path);
    }

    /// <summary>Normalized decode widths seen so far (used as a set; the value is unused).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _observedWidths = new();

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
                Touch(hit);
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
                    Touch(existing);
                    return existing.Bitmap;
                }
                return null;
            }
            Interlocked.Add(ref _totalBytes, newEntry.Bytes);
            // The decoded pixels live in native (Skia) memory the GC can't see — the
            // managed Bitmap wrapper is tiny, so without this hint evicted bitmaps sit
            // in the finalizer queue for ages while native memory climbs into the GBs.
            // Registering the real cost makes Gen2 collections (and thus finalization
            // of evicted, no-longer-referenced bitmaps) keep pace with decode churn.
            GC.AddMemoryPressure(newEntry.Bytes);

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
                OnEntryRemoved(removed);
                dropped++;
            }
        }
        // We intentionally do not dispose bitmaps here; UI controls may still hold references.
    }

    private static void OnEntryRemoved(CacheEntry removed)
    {
        Interlocked.Add(ref _totalBytes, -removed.Bytes);

        // The pressure is withdrawn LATER, not here.
        //
        // The bitmap deliberately outlives eviction (a control can still be showing it)
        // and its native pixels are only reclaimed when the finalizer runs. Dropping the
        // pressure at eviction time removed the very hint that makes the GC keep pace —
        // exactly when the native memory was still resident — so during a fast grid
        // scroll the real footprint could sit well above the cache's byte budget with
        // nothing pushing collection. Deferring keeps the accounting aligned with what
        // is actually allocated.
        SchedulePressureRelease(removed.Bytes);
    }

    /// <summary>
    /// Releases GC memory pressure for an evicted bitmap after a short delay, giving any
    /// control still rendering it time to drop its reference first.
    /// </summary>
    private static void SchedulePressureRelease(long bytes)
    {
        if (bytes <= 0) return;
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
        {
            try { GC.RemoveMemoryPressure(bytes); }
            catch { /* mismatched pressure is non-fatal */ }
        }, TaskScheduler.Default);
    }

    private static string BuildKey(string path, int decodeWidth)
    {
        var width = NormalizeDecodeWidth(decodeWidth);
        _observedWidths.TryAdd(width, 0);
        return $"{width}|{path}";
    }

    private static int NormalizeDecodeWidth(int decodeWidth)
        => decodeWidth <= 0 ? DecodeWidth : Math.Clamp(decodeWidth, 64, 1024);
}
