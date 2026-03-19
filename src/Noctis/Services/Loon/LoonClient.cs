using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Noctis.Services.Loon;

/// <summary>
/// Connects to a loon server via WebSocket and serves local artwork files
/// on demand. Generates HMAC-authenticated URLs that Discord can fetch.
/// </summary>
public sealed class LoonClient : IDisposable
{
    private readonly string _artworkDirectory;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Connection state (set by Hello message)
    private string? _baseUrl;
    private string? _clientId;
    private byte[]? _secret;
    private ulong _chunkSize;
    private ulong _maxContentSize;

    private volatile bool _connected;
    private volatile bool _disposed;
    private string? _serverUrl;

    /// <summary>Whether the client is connected and ready to generate URLs.</summary>
    public bool IsConnected => _connected;

    public LoonClient(string artworkDirectory)
    {
        _artworkDirectory = artworkDirectory;
    }

    /// <summary>
    /// Connects to the loon server and starts the receive loop.
    /// Automatically reconnects on disconnection.
    /// </summary>
    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        _serverUrl = serverUrl;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await ConnectOnceAsync(_cts.Token);

        // Start receive loop (handles reconnection)
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Returns a public URL for the given local artwork path, or null if not connected.
    /// The URL is computed locally in &lt;1ms using HMAC — no network call.
    /// </summary>
    public string? GetArtworkUrl(string? localArtworkPath)
    {
        if (!_connected || _baseUrl == null || _clientId == null || _secret == null)
            return null;

        if (string.IsNullOrWhiteSpace(localArtworkPath) || !File.Exists(localArtworkPath))
            return null;

        var fileName = Path.GetFileName(localArtworkPath);
        var path = $"artwork/{fileName}";
        var hash = ComputeHmac(_clientId, path, _secret);

        return $"{_baseUrl}/{_clientId}/{hash}/{path}";
    }

    public async Task DisconnectAsync()
    {
        _connected = false;

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* best effort */ }
            _ws.Dispose();
            _ws = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = DisconnectAsync();
    }

    // ── Connection ──

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _connected = false;

        var wsUri = new Uri(_serverUrl!.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/ws");
        await _ws.ConnectAsync(wsUri, ct);

        // Wait for Hello message
        var helloData = await ReceiveMessageAsync(ct);
        if (helloData == null) throw new InvalidOperationException("Server did not send Hello");

        var msg = LoonMessageCodec.DecodeServerMessage(helloData);
        if (msg.Type != ServerMessageType.Hello || msg.Hello == null)
            throw new InvalidOperationException($"Expected Hello, got {msg.Type}");

        var hello = msg.Hello;
        _baseUrl = hello.BaseUrl;
        _clientId = hello.ClientId;
        _secret = hello.ConnectionSecret;
        _chunkSize = hello.Constraints.ChunkSize;
        _maxContentSize = hello.Constraints.MaxContentSize;

        _connected = true;
        Debug.WriteLine($"[Loon] Connected: clientId={_clientId}, chunkSize={_chunkSize}");
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                while (_connected && !ct.IsCancellationRequested)
                {
                    var data = await ReceiveMessageAsync(ct);
                    if (data == null) break; // disconnected

                    var msg = LoonMessageCodec.DecodeServerMessage(data);
                    switch (msg.Type)
                    {
                        case ServerMessageType.Request:
                            _ = Task.Run(() => HandleRequestAsync(msg.Request!, ct), ct);
                            break;
                        case ServerMessageType.Success:
                            Debug.WriteLine($"[Loon] Success for request {msg.Success!.RequestId}");
                            break;
                        case ServerMessageType.RequestClosed:
                            Debug.WriteLine($"[Loon] Request {msg.RequestClosed!.RequestId} closed: {msg.RequestClosed.Message}");
                            break;
                        case ServerMessageType.Close:
                            Debug.WriteLine($"[Loon] Server closing: {msg.Close!.Reason} — {msg.Close.Message}");
                            _connected = false;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loon] Receive error: {ex.Message}");
            }

            // Reconnect after delay
            _connected = false;
            if (_disposed || ct.IsCancellationRequested) break;

            Debug.WriteLine("[Loon] Reconnecting in 5s...");
            try { await Task.Delay(5000, ct); } catch { break; }

            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loon] Reconnect failed: {ex.Message}");
            }
        }
    }

    // ── Handle incoming requests ──

    private async Task HandleRequestAsync(RequestMessage request, CancellationToken ct)
    {
        try
        {
            // path is like "artwork/abc123.jpg"
            var fileName = request.Path;
            if (fileName.StartsWith("artwork/"))
                fileName = fileName["artwork/".Length..];

            var filePath = Path.Combine(_artworkDirectory, fileName);

            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[Loon] File not found for request {request.Id}: {filePath}");
                await SendAsync(LoonMessageCodec.EncodeEmptyResponse(request.Id), ct);
                return;
            }

            var fileBytes = await File.ReadAllBytesAsync(filePath, ct);

            // Determine content type
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };

            // Resize large images — Discord only needs a small thumbnail
            const int maxDimension = 512;
            const long resizeThreshold = 2 * 1024 * 1024; // 2MB
            if (fileBytes.Length > resizeThreshold)
            {
                try
                {
                    fileBytes = ResizeImage(fileBytes, maxDimension);
                    contentType = "image/jpeg";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Loon] Resize failed for {fileName}: {ex.Message}");
                }
            }

            var contentSize = (ulong)fileBytes.Length;
            if (_maxContentSize > 0 && contentSize > _maxContentSize)
            {
                Debug.WriteLine($"[Loon] File too large ({contentSize} > {_maxContentSize}): {filePath}");
                await SendAsync(LoonMessageCodec.EncodeEmptyResponse(request.Id), ct);
                return;
            }

            // Send ContentHeader
            await SendAsync(LoonMessageCodec.EncodeContentHeader(request.Id, contentType, contentSize), ct);

            // Send ContentChunks
            var chunkSize = (int)(_chunkSize > 0 ? _chunkSize : 65536);
            ulong sequence = 0;
            var offset = 0;

            while (offset < fileBytes.Length)
            {
                var remaining = fileBytes.Length - offset;
                var size = Math.Min(chunkSize, remaining);
                var chunk = new byte[size];
                Buffer.BlockCopy(fileBytes, offset, chunk, 0, size);

                await SendAsync(LoonMessageCodec.EncodeContentChunk(request.Id, sequence, chunk), ct);

                offset += size;
                sequence++;
            }

            Debug.WriteLine($"[Loon] Served {fileName} ({contentSize} bytes, {sequence} chunks)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Error handling request {request.Id}: {ex.Message}");
            try { await SendAsync(LoonMessageCodec.EncodeCloseResponse(request.Id), ct); } catch { }
        }
    }

    // ── WebSocket I/O ──

    private async Task<byte[]?> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();

        while (true)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return null;

            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(buffer, ct);
            }
            catch
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return ms.ToArray();
        }
    }

    private async Task SendAsync(byte[] data, CancellationToken ct)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }

    // ── Image resizing ──

    private static byte[] ResizeImage(byte[] data, int maxDimension)
    {
        using var original = SKBitmap.Decode(data);
        if (original == null) return data;

        var w = original.Width;
        var h = original.Height;
        if (w <= maxDimension && h <= maxDimension) return data;

        var scale = Math.Min((float)maxDimension / w, (float)maxDimension / h);
        var newW = (int)(w * scale);
        var newH = (int)(h * scale);

        using var resized = original.Resize(new SKImageInfo(newW, newH), SKFilterQuality.Medium);
        if (resized == null) return data;

        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return encoded.ToArray();
    }

    // ── HMAC URL computation ──

    /// <summary>
    /// Computes HMAC-SHA256(clientId + "/" + path, secret) and returns base64url-encoded hash.
    /// </summary>
    private static string ComputeHmac(string clientId, string path, byte[] secret)
    {
        var message = Encoding.UTF8.GetBytes($"{clientId}/{path}");
        var hash = HMACSHA256.HashData(secret, message);
        return Base64UrlEncode(hash);
    }

    /// <summary>URL-safe base64 encoding using the alphabet from the loon spec.</summary>
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
