using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>A decoded cover thumbnail (null image: the file is missing or could not be read).</summary>
public sealed class Cover
{
    public Cover(Bitmap? image) => Image = image;

    public Bitmap? Image { get; }
}

/// <summary>
/// Cover thumbnails, decoded once, at <see cref="ThumbSize"/> and off the UI thread, then kept
/// (least recently used beyond <see cref="Capacity"/> are dropped). The plaza keeps a few big
/// ones (its creatures are drawn ~100 px wide), the Apartments many small ones. Until a cover is
/// ready, and for albums without one, a pastel colour from the album's name stands in.
/// </summary>
public sealed class CoverCache : IDisposable
{
    public const int DefaultThumbSize = 64;
    public int Capacity { get; }
    public int ThumbSize { get; }

    private readonly Dictionary<string, LinkedListNode<(string Path, Cover Cover)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, Cover Cover)> _lru = new();
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public CoverCache(int capacity = 400, int thumbSize = DefaultThumbSize)
    {
        Capacity = Math.Max(8, capacity);
        ThumbSize = Math.Clamp(thumbSize, 16, 512);
    }

    /// <summary>Raised on the UI thread when a cover finished decoding.</summary>
    public event EventHandler? CoverReady;

    public int Count => _map.Count;

    /// <summary>The cover for <paramref name="path"/> if decoded; otherwise starts decoding it and returns null.</summary>
    public Cover? Get(string? path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return null;
        if (_map.TryGetValue(path, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Cover;
        }
        if (_loading.Add(path))
            _ = Task.Run(() => Decode(path, ThumbSize)).ContinueWith(t => Dispatcher.UIThread.Post(() => Store(path, t.IsCompletedSuccessfully ? t.Result : null)),
                TaskScheduler.Default);
        return null;
    }

    /// <summary>Tests and previews: decode now, on this thread.</summary>
    public Cover? GetNow(string? path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return null;
        if (_map.TryGetValue(path, out var node)) return node.Value.Cover;
        Store(path, Decode(path, ThumbSize));
        return _map.TryGetValue(path, out node) ? node.Value.Cover : null;
    }

    private void Store(string path, Cover? cover)
    {
        _loading.Remove(path);
        if (_disposed)
        {
            cover?.Image?.Dispose();
            return;
        }
        // A failed decode still stores an entry (no image) so it is not retried every frame.
        cover ??= new Cover(null);
        if (_map.TryGetValue(path, out var existing))
        {
            _lru.Remove(existing);
            _map.Remove(path);
        }
        _map[path] = _lru.AddFirst((path, cover));
        while (_map.Count > Capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Path);
            // Not disposed here: a frame being drawn may still hold it; the GC frees it.
        }
        CoverReady?.Invoke(this, EventArgs.Empty);
    }

    private static Cover? Decode(string path, int width)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return new Cover(Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A soft colour picked from <paramref name="seed"/> (stable across runs).</summary>
    public static Color Pastel(string seed, float lightness = 0.75f)
    {
        uint h = 2166136261;
        foreach (var ch in seed) h = (h ^ ch) * 16777619;
        // FNV barely moves the low bits for a different last letter ("Artist A", "Artist B"): mix.
        h ^= h >> 15;
        h *= 0x2C1B3C6D;
        h ^= h >> 12;
        var hue = h % 360 / 360.0;
        return FromHsl(hue, 0.55, lightness);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        var q2 = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p2 = 2 * l - q2;
        return Color.FromRgb((byte)(Hue(p2, q2, h + 1.0 / 3) * 255), (byte)(Hue(p2, q2, h) * 255), (byte)(Hue(p2, q2, h - 1.0 / 3) * 255));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, cover) in _lru) cover.Image?.Dispose();
        _lru.Clear();
        _map.Clear();
        CoverReady = null;
    }
}
