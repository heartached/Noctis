using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NoctisCoverProxy;

/// <summary>
/// Handles a single WebSocket connection from a Noctis client.
/// Protocol (JSON over WebSocket):
///
///   Server → Client:
///     {"type":"hello","client_id":"<uuid>","secret":"<hex>","base_url":"<url>"}
///
///   Client → Server:
///     {"type":"publish","content_id":"<id>"}   (text frame)
///     &lt;JPEG bytes&gt;                            (binary frame, immediately after)
///
///   Server → Client:
///     {"type":"published","content_id":"<id>","url":"<public-url>"}
///
///   Client → Server:
///     {"type":"unpublish","content_id":"<id>"}
/// </summary>
public sealed class WebSocketHandler
{
    private const int MaxBinarySize = 2 * 1024 * 1024; // 2 MB max image

    private readonly CoverArtStore _store;
    private readonly string _publicBaseUrl;

    public WebSocketHandler(CoverArtStore store, string publicBaseUrl)
    {
        _store = store;
        _publicBaseUrl = publicBaseUrl.TrimEnd('/');
    }

    public async Task HandleAsync(WebSocket ws, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        // Send hello
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            client_id = clientId,
            secret,
            base_url = _publicBaseUrl,
        });
        await SendTextAsync(ws, hello, ct);
        Console.WriteLine($"[WS] Client connected: {clientId}");

        // Receive loop
        var textBuffer = new byte[4096];
        var binaryBuffer = new byte[MaxBinarySize];

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(textBuffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(textBuffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                var msgType = doc.RootElement.GetProperty("type").GetString();

                switch (msgType)
                {
                    case "publish":
                    {
                        var contentId = doc.RootElement.GetProperty("content_id").GetString()!;

                        // Next frame should be binary (JPEG data)
                        var binaryResult = await ReceiveBinaryAsync(ws, binaryBuffer, ct);
                        if (binaryResult == null) continue;

                        var jpegBytes = binaryBuffer[..binaryResult.Value].ToArray();

                        // Served publicly as image/jpeg from this host — only
                        // accept bytes that actually are one.
                        if (!LooksLikeJpeg(jpegBytes))
                        {
                            Console.WriteLine($"[WS] Rejected non-JPEG publish from {clientId} ({jpegBytes.Length} bytes)");
                            continue;
                        }

                        var key = $"{clientId}/{contentId}";
                        if (!_store.Put(key, jpegBytes))
                        {
                            Console.WriteLine($"[WS] Rejected publish from {clientId}: store limits reached");
                            continue;
                        }

                        var url = $"{_publicBaseUrl}/art/{clientId}/{contentId}";
                        var response = JsonSerializer.Serialize(new
                        {
                            type = "published",
                            content_id = contentId,
                            url,
                        });
                        await SendTextAsync(ws, response, ct);
                        Console.WriteLine($"[WS] Published: {key} ({jpegBytes.Length} bytes) -> {url}");
                        break;
                    }

                    case "unpublish":
                    {
                        var contentId = doc.RootElement.GetProperty("content_id").GetString()!;
                        _store.Remove($"{clientId}/{contentId}");
                        Console.WriteLine($"[WS] Unpublished: {clientId}/{contentId}");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[WS] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            // Cleanup all images for this client on disconnect
            _store.RemoveByPrefix($"{clientId}/");
            Console.WriteLine($"[WS] Client disconnected: {clientId}");
        }
    }

    private static bool LooksLikeJpeg(byte[] data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static async Task SendTextAsync(WebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Receives a complete binary message, handling multi-frame messages.
    /// Returns the total byte count, or null if the message type was unexpected.
    /// </summary>
    private static async Task<int?> ReceiveBinaryAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer, totalRead, buffer.Length - totalRead);
            var result = await ws.ReceiveAsync(segment, ct);

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) return null;

            totalRead += result.Count;

            if (result.EndOfMessage)
                return totalRead;

            if (totalRead >= buffer.Length)
            {
                Console.WriteLine("[WS] Binary message too large, discarding");
                return null;
            }
        }
    }
}
