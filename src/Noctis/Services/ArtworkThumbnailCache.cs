using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Noctis.Services;

/// <summary>
/// Disk cache of downscaled cover decodes, so a big cover file is decoded at full size
/// once instead of every time its tile scrolls back in.
///
/// Why: cached covers are mostly 3000×3000 PNGs (the owner's two libraries, 09-24: 1,432
/// of 2,046 cover files at least 3000 px wide, up to 5000 px / 57 MB), and PNG cannot
/// shrink while decoding. Measured with the app's decoder: a 384 px decode from the
/// source file p50 88 ms, from a 384 px lossless WebP of the same pixels 2.3 ms. The
/// in-memory <see cref="ArtworkCache"/> holds a few screens of the grid, so a long grid,
/// every restart and every revisit past the budget paid the full decode again — and on
/// a 4-core laptop those decodes fill the cores while the grid is gliding.
///
/// Thumbnails are lossless (WebP lossless), so the pixels are exactly what the full
/// decode produces and nothing on screen changes. Only a real shrink is cached: requests
/// up to <see cref="MaxThumbWidth"/> from a source at least <see cref="MinShrink"/>× as
/// wide; smaller sources decode quickly anyway.
///
/// The file name carries hash(path), the width, and the source's size and write time, so
/// a replaced cover misses and gets a new thumbnail; <see cref="Invalidate"/> deletes a
/// path's thumbnails and the folder is trimmed oldest-first to <see cref="MaxBytes"/>.
/// Everything runs on the calling thread (the pool thread a cache miss decodes on).
/// Off until <see cref="Enable"/>: tests and tools never write into a real profile.
/// </summary>
public static class ArtworkThumbnailCache
{
    private const string Extension = ".thumb";

    /// <summary>Largest decode width that gets a thumbnail (a 2× grid tile, the player cover).</summary>
    internal const int MaxThumbWidth = 768;

    /// <summary>The source must be at least this many times the requested width.</summary>
    internal const int MinShrink = 2;

    private static string? _directory;

    /// <summary>The thumbnail folder, or null while the cache is off.</summary>
    public static string? Directory => Volatile.Read(ref _directory);

    /// <summary>Disk budget; the trim brings the folder back to four fifths of it. Internal for tests.</summary>
    internal static long MaxBytes { get; set; } = 512L * 1024 * 1024;

    private static long _writtenSinceTrim;
    private static int _trimming;

    /// <summary>Thumbnails already touched this session (the LRU stamp is refreshed at most once).</summary>
    private static readonly ConcurrentDictionary<string, byte> Touched = new(StringComparer.Ordinal);

    /// <summary>Turns the cache on at <paramref name="directory"/> and trims it in the background.</summary>
    public static void Enable(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artwork", $"thumbnail cache off: {ex.Message}");
            return;
        }
        Volatile.Write(ref _directory, directory);
        ScheduleTrim();
    }

    /// <summary>Test seam: turns the cache off again.</summary>
    internal static void Disable()
    {
        Volatile.Write(ref _directory, null);
        Touched.Clear();
    }

    /// <summary>
    /// <see cref="Helpers.SkiaArtworkDecoder.DecodeToWidth(string?, int)"/> through the
    /// thumbnail cache: the thumbnail when there is a valid one, otherwise the source file
    /// (writing a thumbnail when the decode was a real shrink). The caller owns the bitmap.
    /// </summary>
    public static SKBitmap? DecodeToWidth(string path, int width)
    {
        var dir = Directory;
        if (dir == null || width <= 0 || width > MaxThumbWidth)
            return Helpers.SkiaArtworkDecoder.DecodeToWidth(path, width);

        string? thumb = null;
        try
        {
            var source = new FileInfo(path);
            if (source.Exists)
                thumb = ThumbPath(dir, path, width, source.Length, source.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            // No stamp to key on: decode the source and cache nothing.
        }

        if (thumb != null && File.Exists(thumb))
        {
            var cached = Helpers.SkiaArtworkDecoder.DecodeToWidth(thumb, width);
            if (cached != null && cached.Width == width)
            {
                Touch(thumb);
                return cached;
            }
            // Truncated or foreign file: drop it and rebuild it from the source below.
            cached?.Dispose();
            TryDelete(thumb);
        }

        var decoded = Helpers.SkiaArtworkDecoder.DecodeToWidth(path, width, out var sourceWidth);
        if (decoded != null && thumb != null && decoded.Width == width && sourceWidth >= width * MinShrink)
            TryWrite(thumb, decoded);
        return decoded;
    }

    /// <summary>
    /// Deletes every thumbnail of <paramref name="path"/> (in the background). Only tidies
    /// up: a rewritten cover already misses, since its size or write time changed.
    /// </summary>
    public static void Invalidate(string path)
    {
        var dir = Directory;
        if (dir == null || string.IsNullOrEmpty(path)) return;
        var pattern = PathKey(path) + "-*" + Extension;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, pattern))
                    TryDelete(file);
            }
            catch
            {
                // Folder gone or unreadable: nothing to tidy.
            }
        });
    }

    /// <summary>
    /// Bumped when the decode's pixels change, so thumbnails from an older resize are
    /// rebuilt instead of served (v2: trilinear, see SkiaArtworkDecoder.Downscale).
    /// Stale ones age out through the trim.
    /// </summary>
    private const string Version = "v2";

    internal static string ThumbPath(string dir, string path, int width, long size, long writeTicks)
        => Path.Combine(dir, $"{PathKey(path)}-{width}-{size:x}-{writeTicks:x}-{Version}{Extension}");

    private static string PathKey(string path)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(path), hash);
        return Convert.ToHexString(hash[..8]);
    }

    private static void TryWrite(string thumb, SKBitmap bitmap)
    {
        // Write aside and rename, so a reader never sees half a file.
        var temp = $"{thumb}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null) return;
            using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 70));
            if (data == null || data.Size == 0) return;
            using (var stream = File.Create(temp))
                data.SaveTo(stream);
            File.Move(temp, thumb, overwrite: true);
            if (Interlocked.Add(ref _writtenSinceTrim, data.Size) > MaxBytes / 16)
                ScheduleTrim();
        }
        catch
        {
            // Disk full, read-only profile, or another thread reading the target: no
            // thumbnail this time, the next miss tries again.
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Refreshes the LRU stamp of a thumbnail in use, at most once a day.</summary>
    private static void Touch(string thumb)
    {
        if (!Touched.TryAdd(thumb, 0)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(thumb) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(thumb, now);
        }
        catch
        {
            // The trim may just have taken it.
        }
    }

    private static void ScheduleTrim()
    {
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0) return;
        Interlocked.Exchange(ref _writtenSinceTrim, 0);
        _ = Task.Run(() =>
        {
            try { Trim(); }
            catch { /* best effort */ }
            finally { Interlocked.Exchange(ref _trimming, 0); }
        });
    }

    /// <summary>
    /// Deletes the least recently used thumbnails until the folder is at four fifths of
    /// <see cref="MaxBytes"/>, and temp files a crash left behind. Internal for tests.
    /// </summary>
    internal static void Trim()
    {
        var dir = Directory;
        if (dir == null) return;
        var now = DateTime.UtcNow;
        var thumbs = new List<FileInfo>();
        long total = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (now - file.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(file.FullName);
                continue;
            }
            if (!file.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
            thumbs.Add(file);
            total += file.Length;
        }
        if (total <= MaxBytes) return;

        var target = MaxBytes / 5 * 4;
        foreach (var file in thumbs.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target) break;
            var length = file.Length;
            try
            {
                file.Delete();
                total -= length;
            }
            catch
            {
                // In use right now: skip it.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* already gone or in use */ }
    }
}
