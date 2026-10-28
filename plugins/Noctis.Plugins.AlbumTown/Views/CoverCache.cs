using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Noctis.Plugins.AlbumTown.Views;

/// <summary>A decoded cover: the 64 px thumbnail and the tones the cube's top and side are painted in.</summary>
public sealed class Cover
{
    public Cover(Bitmap? image, Color top, Color side)
    {
        Image = image;
        Top = top;
        Side = side;
    }

    public Bitmap? Image { get; }
    public Color Top { get; }
    public Color Side { get; }
}

/// <summary>
/// Cover thumbnails, decoded once, small (64 px) and off the UI thread, then kept (least recently
/// used beyond <see cref="Capacity"/> are dropped). Until a cover is ready, and for albums without
/// one, a pastel colour from the album's name stands in.
/// </summary>
public sealed class CoverCache : IDisposable
{
    public const int ThumbSize = 64;
    public int Capacity { get; }

    private readonly Dictionary<string, LinkedListNode<(string Path, Cover Cover)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, Cover Cover)> _lru = new();
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public CoverCache(int capacity = 400) => Capacity = Math.Max(8, capacity);

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
            _ = Task.Run(() => Decode(path)).ContinueWith(t => Dispatcher.UIThread.Post(() => Store(path, t.IsCompletedSuccessfully ? t.Result : null)),
                TaskScheduler.Default);
        return null;
    }

    /// <summary>Tests and previews: decode now, on this thread.</summary>
    public Cover? GetNow(string? path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return null;
        if (_map.TryGetValue(path, out var node)) return node.Value.Cover;
        Store(path, Decode(path));
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
        cover ??= new Cover(null, Pastel(path, 0.78f), Pastel(path, 0.55f));
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

    private static Cover? Decode(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, ThumbSize, BitmapInterpolationMode.MediumQuality);
            var (top, side) = Tones(bitmap) ?? (Pastel(path, 0.78f), Pastel(path, 0.55f));
            return new Cover(bitmap, top, side);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Average colour of the top rows (the cube's lid, lightened) and the right
    /// columns (its side, darkened), so the cube reads as one object with its cover.</summary>
    private static (Color Top, Color Side)? Tones(Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return null;
            var stride = size.Width * 4;
            var bytes = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, stride); }
            finally { handle.Free(); }
            var rgba = bitmap.Format is { } f && f == PixelFormats.Rgba8888;
            Color Average(int x0, int y0, int x1, int y1)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var i = y * stride + x * 4;
                        var c0 = bytes[i]; var c1 = bytes[i + 1]; var c2 = bytes[i + 2];
                        if (rgba) { r += c0; g += c1; b += c2; } else { b += c0; g += c1; r += c2; }
                        n++;
                    }
                return n == 0 ? Colors.Gray : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
            var band = Math.Max(1, size.Height / 6);
            var top = Average(0, 0, size.Width, band);
            var side = Average(size.Width - Math.Max(1, size.Width / 6), 0, size.Width, size.Height);
            return (Shade(top, 1.18f), Shade(side, 0.62f));
        }
        catch
        {
            return null;
        }
    }

    public static Color Shade(Color c, float factor)
    {
        static byte Ch(float v) => (byte)Math.Clamp(v, 0, 255);
        return factor >= 1
            ? Color.FromRgb(Ch(c.R + (255 - c.R) * (factor - 1)), Ch(c.G + (255 - c.G) * (factor - 1)), Ch(c.B + (255 - c.B) * (factor - 1)))
            : Color.FromRgb(Ch(c.R * factor), Ch(c.G * factor), Ch(c.B * factor));
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
