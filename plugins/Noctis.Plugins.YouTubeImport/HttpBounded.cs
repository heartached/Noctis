namespace Noctis.Plugins.YouTubeImport;

/// <summary>
/// Bounded HTTP reads and an image sniff for cover art and thumbnails: the plugin's copy of the
/// app's HttpSafety rules (a server cannot make it buffer more than the cap, and an error page
/// never becomes a cover).
/// </summary>
internal static class HttpBounded
{
    /// <summary>Cap for downloaded artwork (high-res covers run 1–5 MB).</summary>
    public const int MaxImageBytes = 24 * 1024 * 1024;

    private const int MaxPreallocateBytes = 1024 * 1024;

    /// <summary>Reads the body as bytes; throws <see cref="HttpRequestException"/> past <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadBytesAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes)
            throw new HttpRequestException($"Response body ({declared} bytes) exceeds the {maxBytes}-byte limit.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(declared.HasValue ? (int)Math.Min(declared.Value, MaxPreallocateBytes) : 0);
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

    /// <summary>True when the data starts like a JPEG, PNG, GIF, WebP or BMP.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true;
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8') return true;
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P') return true;
        return data[0] == (byte)'B' && data[1] == (byte)'M';
    }

    /// <summary>MIME type of a sniffed image (for the cover tag).</summary>
    public static string MimeType(ReadOnlySpan<byte> data) =>
        data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 ? "image/png"
        : data.Length > 3 && data[0] == (byte)'G' && data[1] == (byte)'I' ? "image/gif"
        : data.Length > 11 && data[8] == (byte)'W' && data[9] == (byte)'E' ? "image/webp"
        : data.Length > 1 && data[0] == (byte)'B' && data[1] == (byte)'M' ? "image/bmp"
        : "image/jpeg";
}
