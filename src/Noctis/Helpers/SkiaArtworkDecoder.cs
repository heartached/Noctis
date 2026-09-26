using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Noctis.Helpers;

/// <summary>
/// Decodes artwork files through Skia's own file stream instead of a managed
/// <see cref="Stream"/>.
///
/// Why not <c>Bitmap.DecodeToWidth(Stream)</c>: Avalonia hands the stream to SkiaSharp's
/// managed-stream adapter, which rents a buffer the size of the whole read from
/// <c>ArrayPool&lt;byte&gt;.Shared</c> and returns it to the pool afterwards. The pool
/// keeps those arrays: a 12 MB cover file leaves a 16 MB array parked in the pool, and
/// after a screen of covers the shared pool held 224 MB that no collection ever
/// reclaimed (the pool only trims idle buffers on a gen2, and an idle app has none).
/// Skia reading the file itself keeps the bytes native and short-lived.
///
/// Also used for the shader/share-card paths, which want an <see cref="SKBitmap"/>
/// without ever allocating the file's native resolution (5000×5000 covers are common).
/// </summary>
public static class SkiaArtworkDecoder
{
    /// <summary>
    /// Decodes <paramref name="path"/> so that its longest side is at most
    /// <paramref name="maxDimension"/> pixels, or the next power-of-two subsample above it
    /// (codecs only shrink by 1/2, 1/4, 1/8 …). Returns null when the file is missing or
    /// not an image. The caller owns the bitmap.
    /// </summary>
    public static SKBitmap? DecodeSubsampled(string? path, int maxDimension)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using var codec = OpenCodec(path);
            if (codec == null) return null;

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;
            NoteLargeDecode(info.Width, info.Height, codec.EncodedFormat, path);
            var longest = Math.Max(info.Width, info.Height);
            var sample = 1;
            while (longest / sample > maxDimension) sample *= 2;
            if (sample == 1) return SKBitmap.Decode(codec);

            // Only ask the codec for a size it can actually produce: JPEG shrinks by 1/2,
            // 1/4, 1/8; PNG and WebP-lossless decode at native size only, and requesting a
            // smaller SKImageInfo from them fails outright (Kawarp then drew nothing for
            // any album with a large PNG cover — Discord, aaron 2026-09-21). When the codec
            // cannot shrink, decode native and resize down so the caller still gets the
            // bounded bitmap it asked for.
            var dims = codec.GetScaledDimensions(1f / sample);
            if (dims.Width <= 0 || dims.Height <= 0) dims = new SKSizeI(info.Width, info.Height);
            var decoded = SKBitmap.Decode(codec, new SKImageInfo(dims.Width, dims.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul));
            if (decoded == null) return null;
            if (Math.Max(decoded.Width, decoded.Height) <= maxDimension) return decoded;

            using (decoded)
            {
                var scale = maxDimension / (double)Math.Max(decoded.Width, decoded.Height);
                var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
                var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
                return decoded.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes <paramref name="path"/> to exactly <paramref name="width"/> pixels wide
    /// (height keeps the aspect ratio), the way <c>Bitmap.DecodeToWidth</c> would: the
    /// codec's nearest supported downscale first, then a high-quality resize. Returns
    /// null when the file is missing or not an image. The caller owns the bitmap.
    /// </summary>
    public static SKBitmap? DecodeToWidth(string? path, int width)
        => DecodeToWidth(path, width, out _);

    /// <summary>
    /// As <see cref="DecodeToWidth(string?, int)"/>, and reports the encoded image's own
    /// width (0 when the file is not an image), so a caller can tell how much the decode shrank.
    /// </summary>
    public static SKBitmap? DecodeToWidth(string? path, int width, out int sourceWidth)
    {
        sourceWidth = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || width <= 0)
            return null;
        try
        {
            using var codec = OpenCodec(path);
            if (codec == null) return null;

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;
            NoteLargeDecode(info.Width, info.Height, codec.EncodedFormat, path);
            sourceWidth = info.Width;

            var targetWidth = Math.Min(width, info.Width);
            var targetHeight = Math.Max(1, (int)Math.Round(info.Height * (targetWidth / (double)info.Width)));

            // Nearest supported codec scale at or above the target (JPEG: 1/2, 1/4, 1/8;
            // PNG: full size only), like Avalonia's own DecodeToWidth.
            var dims = codec.GetScaledDimensions(targetWidth / (float)info.Width);
            if (dims.Width < targetWidth || dims.Height <= 0)
                dims = new SKSizeI(info.Width, info.Height);

            var decoded = SKBitmap.Decode(codec, new SKImageInfo(dims.Width, dims.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul));
            if (decoded == null) return null;

            if (decoded.Width == targetWidth && decoded.Height == targetHeight)
                return decoded; // the decode itself is the result: no extra copy

            using (decoded)
            {
                return decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                    SKFilterQuality.High);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Covers above this many pixels get a session-log line before they are decoded.
    /// 5000×5000 (the largest real cover seen, see ArtworkThumbnailCache) stays quiet.
    /// </summary>
    internal const long LargeDecodePixels = 25_000_000;

    /// <summary>
    /// #97 breadcrumb: names a cover bigger than <see cref="LargeDecodePixels"/> right
    /// before it is decoded, once per path, so a native decode crash or an out-of-memory
    /// kill leaves the file in the session log's last lines. Returns true when the cover
    /// is over the threshold.
    /// </summary>
    internal static bool NoteLargeDecode(int width, int height, SKEncodedImageFormat format, string path)
    {
        if ((long)width * height <= LargeDecodePixels) return false;
        Noctis.Services.DebugLog.WriteOnce("Artwork", "bigdecode:" + path,
            $"decoding large cover {width}x{height} {format}: {path}");
        return true;
    }

    /// <summary>
    /// The encoded image's real pixel size, read from its header without decoding any
    /// pixels. Null when <paramref name="imageData"/> is empty or not an image.
    /// </summary>
    public static PixelSize? ReadPixelSize(byte[]? imageData)
    {
        if (imageData is not { Length: > 0 }) return null;
        try
        {
            using var data = SKData.CreateCopy(imageData);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;
            var info = codec.Info;
            return info.Width > 0 && info.Height > 0 ? new PixelSize(info.Width, info.Height) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Skia's own file stream (native, no managed buffer). Should the native open ever
    /// fail for a path the .NET side can read (an exotic encoding), fall back to one
    /// exact-size managed read copied into native memory: plain garbage, not a pooled
    /// buffer, so it is collected like anything else.
    /// </summary>
    private static SKCodec? OpenCodec(string path)
    {
        var codec = SKCodec.Create(path);
        if (codec != null) return codec;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var data = SKData.CreateCopy(bytes);
            return SKCodec.Create(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a BGRA premultiplied <see cref="SKBitmap"/> into an Avalonia bitmap.
    /// The pixels go straight from Skia memory into the platform bitmap; nothing
    /// managed is allocated for them.
    /// </summary>
    public static Bitmap ToAvaloniaBitmap(SKBitmap bitmap)
    {
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(),
            new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);
    }
}
