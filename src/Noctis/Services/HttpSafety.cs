using System.Net.Http;

namespace Noctis.Services;

/// <summary>
/// Bounded readers for responses from external services (LRCLIB, NetEase,
/// Deezer, iTunes, Last.fm, artist images). A compromised or misbehaving
/// endpoint must not be able to allocate unbounded memory or fill the disk;
/// every remote payload gets a hard byte cap, and bytes destined for the
/// artwork cache must actually look like an image.
/// </summary>
public static class HttpSafety
{
    /// <summary>Cap for JSON/XML/HTML/lyrics payloads (largest legit payloads are long lyrics/HTML pages, well under 1 MB).</summary>
    public const long MaxTextBytes = 4 * 1024 * 1024;

    /// <summary>Cap for downloaded artwork (high-res covers run 1–5 MB).</summary>
    public const long MaxImageBytes = 24 * 1024 * 1024;

    /// <summary>Reads the response body as a string, failing once <paramref name="maxBytes"/> is exceeded.</summary>
    public static async Task<string> ReadStringBoundedAsync(HttpContent content, long maxBytes = MaxTextBytes, CancellationToken ct = default)
    {
        var bytes = await ReadBytesBoundedAsync(content, maxBytes, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads the response body as bytes, failing once <paramref name="maxBytes"/> is exceeded.</summary>
    public static async Task<byte[]> ReadBytesBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct = default)
    {
        // Oversize is thrown as HttpRequestException so the existing per-service
        // "network failed" catch blocks handle it like any other transfer error.
        var declared = content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > maxBytes)
            throw new HttpRequestException($"Response body ({declared.Value} bytes) exceeds the {maxBytes}-byte limit.");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(declared.HasValue ? (int)declared.Value : 0);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new HttpRequestException($"Response body exceeds the {maxBytes}-byte limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// True when the data starts with a known raster-image signature
    /// (JPEG, PNG, GIF, WebP, BMP). Rejects HTML/SVG/error pages so they
    /// never land in the artwork cache as ".jpg".
    /// </summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
        // GIF: "GIF8"
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8') return true;
        // WebP: "RIFF"...."WEBP"
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P') return true;
        // BMP: "BM"
        if (data[0] == (byte)'B' && data[1] == (byte)'M') return true;
        return false;
    }
}
