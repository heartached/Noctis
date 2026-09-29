using System.Globalization;
using Noctis.Helpers;
using SkiaSharp;

namespace Noctis.Services;

/// <summary>
/// The desktop's <c>getCoverArt&amp;size=N</c> resizer for the Noctis server: shrinks an album
/// cover to a JPEG whose longest side is N (never upscaled) and keeps it on disk, so a phone
/// syncing a library downloads ~50 KB covers instead of multi-megabyte originals.
///
/// Sources are untrusted in size: files over <see cref="MaxSourceBytes"/> or
/// <see cref="MaxSourcePixels"/> are refused (null → the server answers "not found") rather
/// than decoded, and at most <see cref="MaxConcurrent"/> resizes run at once so a phone
/// fetching hundreds of covers cannot pin every core.
/// </summary>
public sealed class ServerCoverResizer
{
    public const long MaxSourceBytes = 20L * 1024 * 1024;
    public const long MaxSourcePixels = 40_000_000;
    public const int MaxConcurrent = 2;
    private const int JpegQuality = 88;

    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);

    public ServerCoverResizer(string cacheDirectory) => _cacheDirectory = cacheDirectory;

    /// <summary>Path of the cached JPEG for (album, size, source mtime), making it first when needed; null when the source is refused or unreadable.</summary>
    public async Task<string?> ResizeAsync(Guid albumId, string sourcePath, int size, CancellationToken ct)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length > MaxSourceBytes || size <= 0) return null;

        // Cached per source version: a changed cover (new mtime) gets a new file.
        var prefix = $"{albumId:N}-{size}-";
        var target = Path.Combine(_cacheDirectory, prefix + source.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + ".jpg");
        if (File.Exists(target)) return target;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target)) return target; // made by a request that held the gate first
            return await Task.Run(() => Render(sourcePath, size, target, prefix), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private string? Render(string sourcePath, int size, string target, string prefix)
    {
        // Header only: dimensions without decoding a pixel.
        int width, height;
        using (var codec = SKCodec.Create(sourcePath))
        {
            if (codec is null) return null;
            width = codec.Info.Width;
            height = codec.Info.Height;
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaxSourcePixels) return null;

        // Longest side = size; DecodeToWidth decodes at the codec's reduced scale, then shrinks
        // with trilinear sampling, and never enlarges.
        var targetWidth = width >= height ? size : Math.Max(1, (int)Math.Round(size * (width / (double)height)));
        using var bitmap = SkiaArtworkDecoder.DecodeToWidth(sourcePath, targetWidth);
        if (bitmap is null) return null;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        if (data is null) return null;

        Directory.CreateDirectory(_cacheDirectory);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = File.Create(temp)) data.SaveTo(file);
        File.Move(temp, target, overwrite: true);

        // Older renders of this album at this size (the cover has since changed) are dead weight.
        foreach (var stale in Directory.EnumerateFiles(_cacheDirectory, prefix + "*.jpg"))
        {
            if (!string.Equals(stale, target, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return target;
    }
}
