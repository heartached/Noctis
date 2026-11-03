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

    /// <summary>
    /// Initial MemoryStream capacity when a Content-Length is advertised. The header is
    /// server-controlled, so allocating it verbatim let a response *declaring* 24 MB
    /// force that allocation immediately even if the body turned out to be one byte.
    /// </summary>
    private const int MaxPreallocateBytes = 1024 * 1024;

    /// <summary>Reads the response body as bytes, failing once <paramref name="maxBytes"/> is exceeded.</summary>
    public static async Task<byte[]> ReadBytesBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct = default)
    {
        // Oversize is thrown as HttpRequestException so the existing per-service
        // "network failed" catch blocks handle it like any other transfer error.
        var declared = content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > maxBytes)
            throw new HttpRequestException($"Response body ({declared.Value} bytes) exceeds the {maxBytes}-byte limit.");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Clamped: trust the declared length only as a sizing hint, and let the stream
        // grow past it if the body really is that big.
        var capacity = declared.HasValue
            ? (int)Math.Min(declared.Value, MaxPreallocateBytes)
            : 0;
        using var buffer = new MemoryStream(capacity);
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
    /// Deserializes the response body as JSON straight off the wire, failing once
    /// <paramref name="maxBytes"/> is exceeded. For large payloads (the GitHub release list
    /// is ~1.5 MB): reading it into bytes and then a string first put ~7 MB of short-lived
    /// large-object-heap buffers behind every fetch, enough to set off a gen2 collection.
    /// </summary>
    public static async Task<T?> ReadJsonBoundedAsync<T>(HttpContent content, long maxBytes = MaxTextBytes, CancellationToken ct = default)
    {
        var declared = content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > maxBytes)
            throw new HttpRequestException($"Response body ({declared.Value} bytes) exceeds the {maxBytes}-byte limit.");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var bounded = new BoundedReadStream(stream, maxBytes);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(bounded, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Read-only pass-through that throws once more than the cap has been read.</summary>
    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        private int Count(int read)
        {
            _read += read;
            if (_read > maxBytes)
                throw new HttpRequestException($"Response body exceeds the {maxBytes}-byte limit.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
