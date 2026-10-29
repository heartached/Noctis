using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Noctis.Plugins.AlbumRoulette.Views;

/// <summary>A decoded, pre-scaled cover (square, corners rounded): <see cref="Image"/> is null when
/// the file is missing or unreadable.</summary>
public sealed class Cover
{
    public Cover(Bitmap? image)
    {
        Image = image;
        if (image is null) return;
        // The centred square, in the bitmap's pixels (Avalonia 12 reads DrawImage's source rect in pixels).
        var w = image.PixelSize.Width;
        var h = image.PixelSize.Height;
        var side = Math.Min(w, h);
        Source = new Rect((w - side) / 2.0, (h - side) / 2.0, side, side);
    }

    public Bitmap? Image { get; }
    public Rect Source { get; }
}

/// <summary>
/// Covers decoded once, off the UI thread, straight to the size the stage draws them
/// (<see cref="PixelSize"/> device pixels wide), then kept, least recently used beyond
/// <see cref="Capacity"/> dropped. A reel frame only draws bitmaps that are already here.
/// </summary>
public sealed class CoverCache : IDisposable
{
    private readonly Dictionary<string, LinkedListNode<(string Path, Cover Cover)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, Cover Cover)> _lru = new();
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _disposed;

    /// <summary>Corner radius of a cover, as a share of its side (baked into the bitmap, used for its edge).</summary>
    public const double CornerShare = 0.06;

    public CoverCache(int capacity = 64, int pixelSize = 320)
    {
        Capacity = Math.Max(8, capacity);
        PixelSize = Math.Clamp(pixelSize, 32, 1024);
    }

    public int Capacity { get; }

    /// <summary>Width the covers are decoded to, in device pixels.</summary>
    public int PixelSize { get; private set; }

    public int Count => _map.Count;

    /// <summary>Raised on the UI thread with the path of a cover that finished decoding.</summary>
    public event EventHandler<string>? CoverReady;

    /// <summary>
    /// The stage grew past what the covers were decoded for (a bigger window, a higher display
    /// scale): drop them so they decode again at the new size. Shrinking keeps them (they are drawn
    /// a little smaller, which looks fine).
    /// </summary>
    public bool EnsurePixelSize(int pixels)
    {
        pixels = Math.Clamp((pixels + 31) / 32 * 32, 32, 1024);
        if (pixels <= PixelSize) return false;
        PixelSize = pixels;
        Clear();
        return true;
    }

    /// <summary>The cover for <paramref name="path"/> if decoded; otherwise starts decoding it and returns null.</summary>
    public Cover? Get(string? path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return null;
        if (_map.TryGetValue(path, out var node))
        {
            if (!ReferenceEquals(_lru.First, node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            return node.Value.Cover;
        }
        if (_loading.Add(path)) StartDecode(path, PixelSize, _generation);
        return null;
    }

    /// <summary>Tests and previews: decode now, on this thread.</summary>
    public Cover? GetNow(string? path)
    {
        if (string.IsNullOrEmpty(path) || _disposed) return null;
        if (_map.TryGetValue(path, out var node)) return node.Value.Cover;
        Store(path, Decode(path, PixelSize), _generation);
        return _map.TryGetValue(path, out node) ? node.Value.Cover : null;
    }

    private void StartDecode(string path, int pixels, int generation)
        => _ = Task.Run(() => Decode(path, pixels)).ContinueWith(
            t => Dispatcher.UIThread.Post(() => Store(path, t.IsCompletedSuccessfully ? t.Result : null, generation)),
            TaskScheduler.Default);

    private void Store(string path, Cover? cover, int generation)
    {
        if (_disposed || generation != _generation)
        {
            cover?.Image?.Dispose();
            if (generation == _generation) _loading.Remove(path);
            return;
        }
        _loading.Remove(path);
        // A failed decode still stores an entry (no image), so it is not retried every spin.
        cover = cover?.Image is { } decoded ? new Cover(Round(decoded, cover.Source)) : new Cover(null);
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
            // Not disposed: the last frame drawn may still reference it; the GC frees it.
        }
        CoverReady?.Invoke(this, path);
    }

    /// <summary>
    /// The centred square of <paramref name="decoded"/> with its corners rounded, as its own bitmap
    /// (UI thread, once per cover): a reel frame then draws it as is, without a clip.
    /// </summary>
    public static Bitmap Round(Bitmap decoded, Rect square)
    {
        var side = (int)Math.Round(square.Width);
        if (side < 2) return decoded;
        try
        {
            var baked = new RenderTargetBitmap(new PixelSize(side, side), new Vector(96, 96));
            using (var ctx = baked.CreateDrawingContext())
            using (ctx.PushClip(new RoundedRect(new Rect(0, 0, side, side), side * CornerShare)))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(decoded, square, new Rect(0, 0, side, side));
            decoded.Dispose();
            return baked;
        }
        catch (Exception)
        {
            return decoded; // no render target here: draw it square
        }
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

    private void Clear()
    {
        _generation++;
        _loading.Clear();
        _lru.Clear();
        _map.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, cover) in _lru) cover.Image?.Dispose();
        Clear();
        CoverReady = null;
    }
}
