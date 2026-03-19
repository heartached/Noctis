using Avalonia.Media.Imaging;

namespace Noctis.Services;

/// <summary>
/// Thread-safe LRU bitmap cache shared across the application.
/// Decodes artwork at thumbnail size (300px) to minimize memory and decode time.
/// </summary>
public static class ArtworkCache
{
    private static readonly LinkedList<(string Path, Bitmap Bitmap)> LruList = new();
    private static readonly Dictionary<string, LinkedListNode<(string Path, Bitmap Bitmap)>> LruMap = new();
    private static readonly object CacheLock = new();
    private const int MaxCacheSize = 500;
    private const int DecodeWidth = 512;

    /// <summary>
    /// Returns a cached bitmap if available, or null on cache miss. No I/O performed.
    /// </summary>
    public static Bitmap? TryGet(string path)
    {
        lock (CacheLock)
        {
            if (LruMap.TryGetValue(path, out var node))
            {
                LruList.Remove(node);
                LruList.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        return null;
    }

    /// <summary>
    /// Removes a cached bitmap for the given path so the next load reads fresh data from disk.
    /// Does not dispose the bitmap — existing UI controls may still reference it.
    /// </summary>
    public static void Invalidate(string path)
    {
        lock (CacheLock)
        {
            if (LruMap.TryGetValue(path, out var node))
            {
                LruList.Remove(node);
                LruMap.Remove(path);
            }
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
    {
        try
        {
            if (!File.Exists(path))
                return null;

            Bitmap bitmap;
            using (var stream = File.OpenRead(path))
                bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);

            lock (CacheLock)
            {
                // Another thread may have cached this while we were loading
                if (LruMap.TryGetValue(path, out var existing))
                {
                    LruList.Remove(existing);
                    LruList.AddFirst(existing);
                    bitmap.Dispose();
                    return existing.Value.Bitmap;
                }

                // Evict oldest entries if cache is full
                while (LruList.Count >= MaxCacheSize)
                {
                    var oldest = LruList.Last!;
                    LruList.RemoveLast();
                    LruMap.Remove(oldest.Value.Path);
                    oldest.Value.Bitmap.Dispose();
                }

                var newNode = LruList.AddFirst((path, bitmap));
                LruMap[path] = newNode;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
