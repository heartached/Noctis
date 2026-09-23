using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace Noctis.Services;

/// <summary>
/// Thread-safe LRU bitmap cache shared across the application.
/// Uses ConcurrentDictionary for lock-free reads on cache hits.
/// Decodes artwork at thumbnail size (512px) to balance sharpness and memory.
///
/// Lifetime: decoded pixels live in native Skia memory the GC cannot see, so an
/// evicted bitmap that nobody disposes stays resident until its finalizer happens to
/// run — under a fast grid scroll that put the real footprint far above the byte
/// budget. Holders that keep a bitmap on screen (<see cref="Noctis.Controls.CachedImage"/>,
/// the player's current cover) therefore <see cref="Acquire"/> it and
/// <see cref="Release"/> it when they let go; an evicted or invalidated entry is
/// disposed as soon as its last holder releases it (or right away when it has none),
/// after a short grace period that covers a TryGet racing an eviction.
/// </summary>
public static class ArtworkCache
{
    private sealed class CacheEntry
    {
        public readonly Bitmap Bitmap;
        public readonly string Key;
        public readonly string Path;
        public readonly int Width;
        public readonly long Bytes; // approximate decoded size (W*H*4)
        public long LastAccess; // atomic via Interlocked
        public int Refs; // live holders, atomic via Interlocked
        public int State; // 0 = cached, 1 = removed from the cache, 2 = disposed

        public CacheEntry(string key, string path, int width, Bitmap bitmap, long accessCounter)
        {
            Key = key;
            Path = path;
            Width = width;
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

    /// <summary>Every live entry (cached or evicted-but-held) by its bitmap, for Acquire/Release.</summary>
    private static readonly ConcurrentDictionary<Bitmap, CacheEntry> EntryByBitmap =
        new(ReferenceEqualityComparer.Instance);

    private static long _accessCounter;
    private static long _totalBytes; // atomic via Interlocked — approximate resident size
    private static int _evictLock; // 0 = free, 1 = held — used with Monitor.TryEnter pattern via Interlocked

    // Bound the cache by resident bytes (the dominant cost on large libraries:
    // a 512px RGBA bitmap is ~1 MB, so an entry-count cap alone let the cache
    // grow to >1 GB during a full grid scroll). Keep a generous entry-count
    // backstop as well. 128 MB holds ~200 album tiles decoded for a 2x display
    // (384px, 0.6 MB each) — several screens of the grid — now that controls ask
    // for the size they draw at instead of a fixed 768.
    private const int MaxCacheSize = 2000;
    private const long DefaultMaxCacheBytes = 128L * 1024 * 1024;
    private const int DecodeWidth = 512;

    /// <summary>Resident-byte budget. Internal so tests can shrink it.</summary>
    internal static long MaxCacheBytes { get; set; } = DefaultMaxCacheBytes;

    /// <summary>Grace period before an unreferenced evicted bitmap is disposed. Internal for tests.</summary>
    internal static TimeSpan DisposeGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Test seam: replaces the file decode. Receives (path, decodeWidth) and returns the
    /// bitmap to cache, or null. Production leaves it null.
    /// </summary>
    internal static Func<string, int, Bitmap?>? DecoderOverride { get; set; }

    /// <summary>Approximate resident bytes currently held by the cache (diagnostic).</summary>
    internal static long ResidentBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Number of cached bitmaps currently resident (diagnostic).</summary>
    internal static int Count => Cache.Count;

    /// <summary>Bitmaps alive anywhere (cached or evicted-but-held). Internal for tests.</summary>
    internal static int LiveBitmapCount => EntryByBitmap.Count;

    /// <summary>
    /// Decode-width buckets. Requests round UP to the next bucket so surfaces that draw
    /// at similar sizes share one decode instead of each keying its own copy.
    /// </summary>
    private static readonly int[] Buckets = { 128, 256, 384, 512, 768, 1024, 1280, 2048 };

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
        => TryGetAnyWidth(path, decodeWidth, out _);

    /// <summary>
    /// As <see cref="TryGetAnyWidth(string,int)"/>, and reports whether the returned bitmap
    /// is good enough to keep: at least the requested width and no more than twice it.
    /// A control that gets a "sufficient" bitmap skips its own decode, so one cover no
    /// longer ends up resident once per surface that shows it.
    /// </summary>
    public static Bitmap? TryGetAnyWidth(string path, int decodeWidth, out bool sufficient)
    {
        sufficient = false;
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
        sufficient = chosen == atLeast && atLeastWidth <= requested * 2;
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
    /// Registers a live holder of a cache bitmap. Pair with <see cref="Release"/>.
    /// Unknown bitmaps (already disposed, or not from this cache) are ignored.
    /// </summary>
    public static void Acquire(Bitmap? bitmap)
    {
        if (bitmap is null || !EntryByBitmap.TryGetValue(bitmap, out var entry)) return;
        Interlocked.Increment(ref entry.Refs);
    }

    /// <summary>
    /// Drops a holder registered with <see cref="Acquire"/>. The last release of an
    /// evicted or invalidated bitmap disposes it (after the grace period).
    /// </summary>
    public static void Release(Bitmap? bitmap)
    {
        if (bitmap is null || !EntryByBitmap.TryGetValue(bitmap, out var entry)) return;
        var refs = Interlocked.Decrement(ref entry.Refs);
        if (refs < 0) Interlocked.Exchange(ref entry.Refs, 0);
        if (refs <= 0 && Volatile.Read(ref entry.State) == 1)
            ScheduleDispose(entry);
    }

    /// <summary>
    /// Removes a cached bitmap for the given path so the next load reads fresh data from disk.
    /// The bitmap is disposed once every holder has released it.
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
            if (DecoderOverride is null && !File.Exists(path))
                return null;

            var width = NormalizeDecodeWidth(decodeWidth);
            var key = BuildKey(path, width);

            // Double-check: another thread may have cached this while we waited for I/O to start
            if (Cache.TryGetValue(key, out var hit))
            {
                Touch(hit);
                return hit.Bitmap;
            }

            Bitmap? bitmap;
            if (DecoderOverride is { } decoder)
                bitmap = decoder(path, width);
            else
            {
                // Not Bitmap.DecodeToWidth(Stream): that path rents a file-sized buffer
                // from ArrayPool<byte>.Shared per decode, and the pool kept 224 MB of
                // them after one screen of covers. Skia reads the file itself here.
                using var decoded = Helpers.SkiaArtworkDecoder.DecodeToWidth(path, width);
                bitmap = decoded is null ? null : Helpers.SkiaArtworkDecoder.ToAvaloniaBitmap(decoded);
            }
            if (bitmap is null)
                return null;

            var counter = Interlocked.Increment(ref _accessCounter);
            var newEntry = new CacheEntry(key, path, width, bitmap, counter);

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
            EntryByBitmap[bitmap] = newEntry;
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
        // Evict oldest-accessed entries until both budgets sit at three quarters. The
        // hysteresis keeps the next few decodes from re-triggering a sort of the whole
        // cache. The old rule ("drop at least a batch of 200") never stopped early on
        // a cache that held fewer than 200 entries, so at the byte budget every
        // overflow flushed the entire cache, on-screen covers included, and the grid
        // re-decoded what it was already showing.
        var byteTarget = MaxCacheBytes / 4 * 3;
        var countTarget = MaxCacheSize / 4 * 3;
        var ordered = Cache.Values.OrderBy(e => Interlocked.Read(ref e.LastAccess)).ToList();
        foreach (var entry in ordered)
        {
            if (Cache.Count <= countTarget && Interlocked.Read(ref _totalBytes) <= byteTarget)
                break;

            if (Cache.TryRemove(entry.Key, out var removed))
                OnEntryRemoved(removed);
        }
    }

    private static void OnEntryRemoved(CacheEntry removed)
    {
        Interlocked.Add(ref _totalBytes, -removed.Bytes);
        Volatile.Write(ref removed.State, 1);
        // Held on screen: the last Release disposes it. Otherwise dispose after the
        // grace period, which covers a TryGet that handed the bitmap out a moment ago
        // and is about to Acquire it on the UI thread.
        ScheduleDispose(removed);
    }

    private static void ScheduleDispose(CacheEntry entry)
    {
        var grace = DisposeGrace;
        if (grace <= TimeSpan.Zero)
        {
            TryDispose(entry);
            return;
        }
        _ = Task.Delay(grace).ContinueWith(_ => TryDispose(entry), TaskScheduler.Default);
    }

    /// <summary>Disposes the entry's bitmap when it has left the cache and nobody holds it.</summary>
    private static void TryDispose(CacheEntry entry)
    {
        if (Volatile.Read(ref entry.Refs) > 0) return; // Release will reschedule
        if (Interlocked.CompareExchange(ref entry.State, 2, 1) != 1) return; // still cached, or already disposed
        EntryByBitmap.TryRemove(entry.Bitmap, out _);
        try { entry.Bitmap.Dispose(); }
        catch { /* a bitmap the platform already tore down */ }
        try { GC.RemoveMemoryPressure(entry.Bytes); }
        catch { /* mismatched pressure is non-fatal */ }
    }

    /// <summary>
    /// Test seam: drops every entry and disposes every live bitmap, held or not, so
    /// one test's leftovers (a closed window still holding a cover) cannot leak into
    /// the next test's counts.
    /// </summary>
    internal static void ClearForTests()
    {
        foreach (var key in Cache.Keys.ToList())
        {
            if (Cache.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _totalBytes, -removed.Bytes);
                Volatile.Write(ref removed.State, 1);
            }
        }
        foreach (var entry in EntryByBitmap.Values.ToList())
        {
            Interlocked.Exchange(ref entry.Refs, 0);
            Volatile.Write(ref entry.State, 1);
            TryDispose(entry);
        }
        Interlocked.Exchange(ref _totalBytes, 0);
    }

    private static string BuildKey(string path, int decodeWidth)
    {
        var width = NormalizeDecodeWidth(decodeWidth);
        _observedWidths.TryAdd(width, 0);
        return $"{width}|{path}";
    }

    /// <summary>
    /// Rounds a requested decode width up to its bucket (64–2048). Grids and lists ask
    /// for ≤ 1024; the artist hero (a portrait stretched across the window) asks for
    /// 2048 so an 1800px photo is decoded at its own resolution instead of being halved
    /// and re-enlarged. Public so controls can size their request the same way.
    /// </summary>
    public static int NormalizeDecodeWidth(int decodeWidth)
    {
        if (decodeWidth <= 0) return DecodeWidth;
        var clamped = Math.Clamp(decodeWidth, 64, 2048);
        foreach (var b in Buckets)
            if (clamped <= b) return b;
        return 2048;
    }
}
