using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private readonly HttpClient? _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Connection state (set by Hello message)
    private string? _baseUrl;
    private string? _clientId;
    private byte[]? _secret;
    private ulong _chunkSize;
    private ulong _maxContentSize;
    private uint _cacheDuration;

    private volatile bool _connected;
    private volatile bool _disposed;
    private string? _serverUrl;

    /// <summary>Serializes ConnectAsync so overlapping calls can't start two receive loops.</summary>
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>Whether the client is connected and ready to generate URLs.</summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// Raised after every successful (re)connect, once a fresh clientId/secret are in place.
    /// Subscribers should re-publish any artwork URL they previously handed out, because a
    /// reconnect rotates the clientId and invalidates URLs generated before it.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>
    /// Raised when the connection drops. Every URL handed out under the old clientId is
    /// dead from this moment, so subscribers must stop re-publishing them rather than
    /// leaving a consumer pointed at a URL that now 404s.
    /// </summary>
    public event Action? Disconnected;

    // Keep the WebSocket pinging so an idle NAT/proxy hop doesn't quietly drop it.
    // (.NET 8 has no pong deadline — ClientWebSocketOptions.KeepAliveTimeout is .NET 9+ —
    // so prompt death detection comes from the bounded send path below.)
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

    // Every wait on the send path is bounded, and all of them together stay under the
    // relay's 30s request timeout so a stalled socket is recycled before the relay gives
    // up on Discord's fetch. One unbounded send used to hold _sendGate until the OS TCP
    // timeout, so later handlers queued behind it, filled _requestGate, and every
    // subsequent request was dropped without a reply — a permanent artwork outage.
    private static readonly TimeSpan SendGateWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RequestGateWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a served cover may be cached, requested in every ContentHeader. Leaving this
    /// unset is what made Discord's media proxy refetch the image on every single profile-card
    /// view — the relay reads a missing field as zero and answers "Cache-Control: no-store".
    /// An hour comfortably covers a listening session, and the relay silently caps it to its
    /// own configured cache_duration. Safe against edited covers because the minted URL is
    /// versioned by the bytes on disk — see <see cref="ArtworkVersion"/>.
    /// </summary>
    private const uint DesiredCacheSeconds = 3600;

    /// <param name="http">
    /// Used only to warm the relay's cache with the client's own URLs. Null disables warming.
    /// </param>
    public LoonClient(string artworkDirectory, HttpClient? http = null)
    {
        _artworkDirectory = artworkDirectory;
        _http = http;
    }

    /// <summary>
    /// Connects to the loon server and starts the receive loop.
    /// Automatically reconnects on disconnection.
    /// </summary>
    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        // Serialized: the "already running" check below reads _receiveTask, which is not
        // assigned until the connect completes, so two overlapping calls (the startup
        // fire-and-forget racing a Discord toggle) both sailed past it and started a second
        // receive loop over the same shared socket field. Each loop's reconnect then disposed
        // the other's socket, so every artwork request in flight died with the connection and
        // the relay failed Discord's fetch with a 504 — a broken-image placeholder, forever,
        // on a ~6s churn cycle. One loop per client.
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_receiveTask is { IsCompleted: false })
            {
                Debug.WriteLine("[Loon] ConnectAsync ignored — a receive loop is already running.");
                return;
            }

            _serverUrl = serverUrl;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            // A throwing first connect used to propagate out of here before the receive loop
            // was started, leaving no loop and therefore no reconnect: Loon stayed dead for
            // the rest of the session while the socket it had opened lingered ESTABLISHED.
            // Swallow it here and let the loop's retry schedule recover instead.
            try
            {
                await ConnectOnceAsync(token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loon] Initial connect failed, retrying in the loop: {ex.Message}");
                DebugLog.Write("Loon", $"Could not reach the artwork relay ({serverUrl}): {ex.Message}. Retrying.");
            }

            // Start receive loop (handles reconnection)
            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), token);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Returns a public URL for the given local artwork path, or null if not connected.
    /// The URL is computed locally in &lt;1ms using HMAC — no network call.
    /// </summary>
    public string? GetArtworkUrl(string? localArtworkPath)
    {
        if (!_connected || _baseUrl == null || _clientId == null || _secret == null)
            return ReportArtworkOutcome("No cover for Discord: the artwork relay is not connected.", null);

        if (string.IsNullOrWhiteSpace(localArtworkPath))
            return ReportArtworkOutcome("No cover for Discord: this track has no album artwork.", null);

        if (!File.Exists(localArtworkPath))
            return ReportArtworkOutcome(
                $"No cover for Discord: cached artwork is missing ({Path.GetFileName(localArtworkPath)}).", null);

        // The URL carries only the file name, and the relay resolves it back inside
        // _artworkDirectory. A path from anywhere else therefore passes File.Exists here
        // but can never be fulfilled — the request arrives, the file isn't in the served
        // directory, and Discord ends up showing a broken-image placeholder. Don't mint a
        // URL we know cannot be served.
        if (!IsServableArtworkPath(_artworkDirectory, localArtworkPath))
            return ReportArtworkOutcome(
                "No cover for Discord: artwork sits outside the served folder " +
                $"({Path.GetDirectoryName(localArtworkPath)}).", null);

        var fileName = Path.GetFileName(localArtworkPath);
        var path = $"artwork/{ArtworkVersion(localArtworkPath)}/{fileName}";
        var hash = ComputeHmac(_clientId, path, _secret);
        var url = $"{_baseUrl}/{_clientId}/{hash}/{path}";

        // Build the thumbnail and push it to the relay now, off the caller's thread. This runs
        // on a track change, and Discord's proxy resolves the URL a second or more later, so
        // both land in that gap instead of inside the request handler where the relay and
        // Discord are blocked waiting on them.
        var prewarmPath = localArtworkPath;
        _ = Task.Run(() => PrewarmAsync(prewarmPath, url));

        return ReportArtworkOutcome($"Handed Discord a cover URL for {fileName}.", url);
    }

    /// <summary>
    /// Decodes the cover, then asks the relay for it so the cache in front of the relay holds
    /// the bytes before Discord ever asks.
    /// </summary>
    private async Task PrewarmAsync(string localArtworkPath, string url)
    {
        // Sequential on purpose. The warm request makes the relay ask this client for exactly
        // these bytes, so running the two in parallel would have both miss the thumbnail cache
        // and pay the ~250-300ms decode twice.
        if (GetOrBuildThumbnail(localArtworkPath) == null) return;
        await WarmRelayCacheAsync(url).ConfigureAwait(false);
    }

    // ── Relay cache warming ──

    /// <summary>Bounds a warm request; it is an optimisation and must never linger.</summary>
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether a warm response has been inspected yet, and what it said. A loon server stores
    /// nothing itself — a cache has to be deployed in front of it — so against a relay without
    /// one, warming would upload every cover twice for no benefit at all. Probe once per
    /// connection, then stop if there is nothing there to warm.
    /// </summary>
    private volatile bool _warmProbed;
    private volatile bool _warmUnsupported;

    /// <summary>
    /// Fetches this client's own freshly minted URL, so the relay pulls the cover up now —
    /// while the track is starting — instead of when a Discord viewer first renders the card.
    /// loon cannot push content ahead of a request (upstream issue #27 is still open), so a
    /// self-request is the only way to get the upload off the render path. Best effort
    /// throughout: every failure here just leaves the previous behaviour in place.
    /// </summary>
    private async Task WarmRelayCacheAsync(string url)
    {
        if (_http == null || _warmUnsupported || !_connected) return;

        try
        {
            using var cts = new CancellationTokenSource(WarmTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // The shared client defaults to Accept: application/json, which is not what this
            // endpoint serves.
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            // The body has to be drained: the relay only finishes streaming the content, and
            // the cache only commits the entry, once the response is read to the end.
            await response.Content.CopyToAsync(Stream.Null, cts.Token).ConfigureAwait(false);

            if (_warmProbed) return;
            _warmProbed = true;

            if (!response.Headers.Contains("Cache-Status"))
            {
                _warmUnsupported = true;
                Debug.WriteLine("[Loon] Relay answered without cache headers — disabling warm requests.");
                DebugLog.Write("Loon",
                    "The artwork relay has no cache in front of it, so covers are fetched on demand.");
            }
            else
            {
                Debug.WriteLine($"[Loon] Warm ok, relay cache present ({(int)response.StatusCode}).");
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or shutting down — Discord's own fetch still works.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Cache warm failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A stamp of the bytes currently on disk, used as a path segment so that editing a cover
    /// mints a different URL. The file name alone is "{albumId}.jpg", which is stable for the
    /// life of the album: now that responses carry a real max_cache_duration, Discord would
    /// otherwise keep serving the superseded image for the rest of the cache window. Same
    /// identity the thumbnail cache keys on (see <see cref="ThumbnailKey"/>).
    /// Falls back to a constant when the file cannot be stat'd — the URL still resolves, it
    /// just isn't versioned.
    /// </summary>
    private static string ArtworkVersion(string localArtworkPath)
    {
        try
        {
            var file = new FileInfo(localArtworkPath);
            return $"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}";
        }
        catch
        {
            return "v0";
        }
    }

    /// <summary>
    /// Seconds to ask for in ContentHeader.max_cache_duration, capped by what the relay
    /// advertised in its Hello. Null when the relay does no caching, in which case the field
    /// is left off rather than sent as a value the relay would only floor to zero anyway.
    /// </summary>
    private uint? RequestedCacheSeconds
        => _cacheDuration == 0 ? null : Math.Min(DesiredCacheSeconds, _cacheDuration);

    /// <summary>Last line written by <see cref="ReportArtworkOutcome"/>, to suppress repeats.</summary>
    private string? _lastArtworkOutcome;

    /// <summary>
    /// Records why a track did or didn't get a cover, then returns <paramref name="result"/>.
    /// This is the only place that can answer "why is there no album art in Discord", and the
    /// three failure gates above used to be invisible: they logged through Debug.WriteLine,
    /// which the compiler strips from release builds, so a user's report could never say
    /// which one fired. Repeats are suppressed because this runs on every track change and
    /// every throttled seek, and the log buffer only holds 500 lines.
    /// </summary>
    private string? ReportArtworkOutcome(string message, string? result)
    {
        if (!string.Equals(_lastArtworkOutcome, message, StringComparison.Ordinal))
        {
            _lastArtworkOutcome = message;
            DebugLog.Write("Loon", message);
        }
        return result;
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

        // Let the cancelled loop actually finish before dropping our handle on it. Leaving it
        // running meant a quick toggle off/on reached ConnectAsync while _receiveTask was
        // still incomplete, hit the guard above, and returned without connecting — Loon then
        // stayed dead (no artwork) for the rest of the session.
        var loop = _receiveTask;
        _receiveTask = null;
        if (loop != null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* cancelled, faulted, or slow to unwind — we are tearing down anyway */ }
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
        var previous = _ws;
        _ws = null;
        _connected = false;
        previous?.Dispose();

        var wsUri = new Uri(_serverUrl!.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/ws");

        // Refuse a cleartext ws:// downgrade to a non-local host: the handshake
        // carries the HMAC ConnectionSecret, so a plaintext link (tampered
        // setting or MITM) would leak it. Loopback stays allowed for dev relays.
        if (wsUri.Scheme == "ws" && !wsUri.IsLoopback)
            throw new InvalidOperationException(
                $"Refusing insecure ws:// connection to non-local host '{wsUri.Host}'. Use wss://.");

        var ws = new ClientWebSocket();

        // The relay sits behind a TLS reverse proxy, so the far end can go away while the
        // local socket stays ESTABLISHED — the client looks connected, keeps minting URLs,
        // and every artwork request the relay forwards times out. Pings keep an otherwise
        // idle hop alive; the bounded send path is what turns a dead link into a prompt
        // failure the receive loop reconnects from.
        ws.Options.KeepAliveInterval = KeepAliveInterval;

        try
        {
            await ws.ConnectAsync(wsUri, ct);

            // Wait for Hello message
            var helloData = await ReceiveMessageAsync(ws, ct);
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
            _cacheDuration = hello.Constraints.CacheDuration;
        }
        catch
        {
            // Don't leave the half-built socket behind: it stayed ESTABLISHED against the
            // relay while this client considered itself disconnected.
            ws.Dispose();
            throw;
        }

        _ws = ws;
        _connected = true;
        Debug.WriteLine(
            $"[Loon] Connected: clientId={_clientId}, chunkSize={_chunkSize}, cacheDuration={_cacheDuration}s");
        DebugLog.Write("Loon", $"Connected to the artwork relay as {_clientId}.");

        // Worth surfacing: with no relay-side caching every profile-card view in Discord
        // refetches the cover across the relay from this machine, which reads as a slow
        // fade-in on the card. Nothing the client can do about it but say so.
        if (_cacheDuration == 0)
            DebugLog.Write("Loon",
                "The artwork relay reports no caching support, so Discord must refetch each cover every time it is shown.");
        _lastArtworkOutcome = null;   // a new clientId is a new story; don't suppress the next line

        // Re-probe for a cache in front of the relay: this may be a different relay entirely.
        _warmProbed = false;
        _warmUnsupported = false;

        try { Reconnected?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"[Loon] Reconnected handler threw: {ex.Message}"); }
    }

    // ── Receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            // Whether this iteration ever held a live connection, and therefore has one to
            // report as lost below. Reading _connected after the loop cannot tell us: a
            // server-initiated Close clears it inside the switch, so the flag was already
            // false by then and Disconnected never fired — leaving the artwork cache holding
            // a URL minted under a clientId the relay had just retired.
            var hadConnection = _connected;
            try
            {
                while (_connected && !ct.IsCancellationRequested)
                {
                    hadConnection = true;

                    var socket = _ws;
                    if (socket == null) break;

                    var data = await ReceiveMessageAsync(socket, ct);
                    if (data == null) break; // disconnected

                    ServerMessage msg;
                    try
                    {
                        msg = LoonMessageCodec.DecodeServerMessage(data);
                    }
                    catch (InvalidDataException ex)
                    {
                        // One unreadable frame is not a dead link. Every WebSocket message is
                        // decoded independently, so drop this one and keep serving: letting
                        // the exception reach the outer catch tore the socket down, and the
                        // relay then failed every in-flight Discord fetch with a 504.
                        Debug.WriteLine($"[Loon] Ignoring undecodable message ({data.Length} bytes): {ex.Message}");
                        continue;
                    }

                    switch (msg.Type)
                    {
                        case ServerMessageType.Request:
                            // Bounded. Each handler reads a file and does a full Skia
                            // decode/resize into memory, so an unthrottled Task.Run per
                            // inbound frame let a compromised or misbehaving relay drive
                            // unbounded parallel I/O and heap growth inside the music
                            // player. MaxInboundMessageBytes caps one message, not the rate.
                            var request = msg.Request!;
                            _ = Task.Run(async () =>
                            {
                                // The gate now waits rather than failing instantly, and a
                                // full queue still answers. Returning without a reply left
                                // the relay blocking until it gave up with a 504, which
                                // Discord renders as a broken-image placeholder.
                                if (!await _requestGate.WaitAsync(RequestGateWait, ct).ConfigureAwait(false))
                                {
                                    Debug.WriteLine($"[Loon] Request {request.Id} queue full — answering empty");
                                    await AnswerEmptyAsync(socket, request.Id, ct).ConfigureAwait(false);
                                    return;
                                }
                                try { await HandleRequestAsync(socket, request, ct).ConfigureAwait(false); }
                                finally { _requestGate.Release(); }
                            }, ct);
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
            if (hadConnection)
            {
                try { Disconnected?.Invoke(); }
                catch (Exception ex) { Debug.WriteLine($"[Loon] Disconnected handler threw: {ex.Message}"); }
            }
            if (_disposed || ct.IsCancellationRequested) break;

            Debug.WriteLine("[Loon] Reconnecting in 5s...");
            if (hadConnection)
                DebugLog.Write("Loon", "Lost the artwork relay connection; reconnecting in 5s.");
            try { await Task.Delay(5000, ct); } catch { break; }

            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Loon] Reconnect failed: {ex.Message}");
                DebugLog.Write("Loon", $"Artwork relay reconnect failed: {ex.Message}");
            }
        }
    }

    // ── Handle incoming requests ──

    /// <summary>
    /// Whether <paramref name="localArtworkPath"/> lives directly in the directory this
    /// client serves, and can therefore be fetched back through the minted URL (which
    /// carries only the file name).
    /// </summary>
    internal static bool IsServableArtworkPath(string artworkDirectory, string localArtworkPath)
    {
        try
        {
            var root = Path.GetFullPath(artworkDirectory);
            var full = Path.GetFullPath(localArtworkPath);
            var dir = Path.GetDirectoryName(full);
            if (dir == null) return false;

            return string.Equals(
                dir.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;   // illegal characters etc.
        }
    }

    /// <summary>
    /// Resolves a relay-requested artwork path to an absolute file path, returning null
    /// when the request escapes <paramref name="artworkDirectory"/> (path traversal).
    /// The relay controls the request string, so callers must reject a null result rather
    /// than read an arbitrary file off the local disk.
    /// Accepts exactly the two shapes this client mints: "artwork/{version}/{file}", and the
    /// older "artwork/{file}" — URLs of that shape can still be sitting in Discord's cache
    /// after an upgrade. The version segment is a cache-buster with no counterpart on disk,
    /// so only the final segment names the file.
    /// </summary>
    internal static string? ResolveArtworkPath(string artworkDirectory, string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath)) return null;
        if (!requestPath.StartsWith("artwork/", StringComparison.Ordinal)) return null;

        var rest = requestPath["artwork/".Length..];

        // Reject dot segments outright. The final containment check below would catch an
        // actual escape anyway, but stripping the version segment means a "../" prefix would
        // otherwise be silently discarded rather than refused, and a request that tries to
        // climb out should be answered as the hostile thing it is.
        if (rest.Contains("..", StringComparison.Ordinal)) return null;

        var slash = rest.IndexOf('/');
        var fileName = slash < 0 ? rest : rest[(slash + 1)..];
        if (string.IsNullOrEmpty(fileName)) return null;
        if (fileName.Contains('/')) return null;   // deeper than anything this client mints

        var root = Path.GetFullPath(artworkDirectory);
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(root, fileName)); }
        catch { return null; } // illegal characters etc.

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSep, StringComparison.Ordinal) ? fullPath : null;
    }

    private async Task HandleRequestAsync(ClientWebSocket ws, RequestMessage request, CancellationToken ct)
    {
        try
        {
            // path is like "artwork/abc123.jpg". The relay supplies this string, so guard
            // against path traversal ("artwork/../../secret") before touching the filesystem —
            // otherwise a malicious or compromised relay could read arbitrary local files.
            var filePath = ResolveArtworkPath(_artworkDirectory, request.Path);
            if (filePath == null)
            {
                Debug.WriteLine($"[Loon] Rejected out-of-bounds request {request.Id}: {request.Path}");
                await AnswerEmptyAsync(ws, request.Id, ct);
                return;
            }
            var fileName = Path.GetFileName(filePath);

            if (!File.Exists(filePath))
            {
                Debug.WriteLine($"[Loon] File not found for request {request.Id}: {filePath}");
                DebugLog.Write("Loon", $"Discord asked for {fileName} but it is not in the artwork folder.");
                await AnswerEmptyAsync(ws, request.Id, ct);
                return;
            }

            // Discord only ever renders a small thumbnail, so always hand over a compact
            // JPEG. The old code only touched files over 2MB and fell back to the original
            // whenever the decode failed, which meant routinely streaming 10-14MB of
            // embedded cover art up a residential uplink — the relay gave up long before
            // that finished and failed Discord's fetch with a 504. Re-encoding every
            // response also keeps the declared content type honest: artwork is cached as
            // "{albumId}.jpg" whatever the real format, so PNG bytes used to go out
            // labelled image/jpeg.
            var thumbnail = await Task.Run(() => GetOrBuildThumbnail(filePath), ct);
            if (thumbnail == null)
            {
                Debug.WriteLine($"[Loon] Could not decode {fileName} — answering empty");
                DebugLog.Write("Loon", $"Discord asked for {fileName} but the image could not be decoded.");
                await AnswerEmptyAsync(ws, request.Id, ct);
                return;
            }

            var fileBytes = thumbnail;
            const string contentType = "image/jpeg";

            var contentSize = (ulong)fileBytes.Length;
            if (_maxContentSize > 0 && contentSize > _maxContentSize)
            {
                Debug.WriteLine($"[Loon] File too large ({contentSize} > {_maxContentSize}): {filePath}");
                await AnswerEmptyAsync(ws, request.Id, ct);
                return;
            }

            // Send ContentHeader
            if (!await SendAsync(ws, LoonMessageCodec.EncodeContentHeader(
                    request.Id, contentType, contentSize, RequestedCacheSeconds), ct))
                return;

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

                // Stop at the first failed chunk instead of grinding through the rest
                // against a socket that is already gone.
                if (!await SendAsync(ws, LoonMessageCodec.EncodeContentChunk(request.Id, sequence, chunk), ct))
                    return;

                offset += size;
                sequence++;
            }

            Debug.WriteLine($"[Loon] Served {fileName} ({contentSize} bytes, {sequence} chunks)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Error handling request {request.Id}: {ex.Message}");
            try { await SendAsync(ws, LoonMessageCodec.EncodeCloseResponse(request.Id), ct); } catch { }
        }
    }

    // ── WebSocket I/O ──

    // Inbound messages are tiny control frames; the cap stops a hostile/hijacked
    // relay from streaming an unbounded message into memory.
    private const int MaxInboundMessageBytes = 4 * 1024 * 1024;

    private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();

        while (true)
        {
            if (ws.State != WebSocketState.Open) return null;

            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct);
            }
            catch
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            if (ms.Length + result.Count > MaxInboundMessageBytes) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return ms.ToArray();
        }
    }

    // ClientWebSocket allows exactly one outstanding SendAsync. Inbound requests are
    // dispatched as independent Task.Runs and each handler sends a long series of chunks,
    // so two overlapping artwork requests (Discord retrying, or two viewers resolving the
    // same presence) raced here and threw
    // InvalidOperationException("There is already one outstanding 'SendAsync' call…"),
    // which was swallowed — leaving the viewer with a truncated image.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Caps concurrent inbound request handlers (each does file I/O + a Skia decode).</summary>
    private readonly SemaphoreSlim _requestGate = new(3, 3);

    /// <summary>
    /// Answers a request with an empty response. The relay holds Discord's fetch open until
    /// this client replies, so every bail-out path must still answer — returning in silence
    /// left the relay to give up with a 504, which Discord renders as a broken image.
    /// </summary>
    private Task<bool> AnswerEmptyAsync(ClientWebSocket ws, ulong requestId, CancellationToken ct)
        => SendAsync(ws, LoonMessageCodec.EncodeEmptyResponse(requestId), ct);

    /// <summary>
    /// Sends one frame on <paramref name="ws"/>. Returns false when the frame did not go
    /// out, so the caller can abandon the rest of a response instead of continuing blind.
    /// Every wait is bounded and a stall aborts the socket, which makes the receive loop
    /// notice immediately and reconnect — previously a send against a half-open TLS link
    /// blocked until the OS TCP timeout, holding the send gate and stalling every later
    /// request behind it.
    /// </summary>
    private async Task<bool> SendAsync(ClientWebSocket ws, byte[] data, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return false;

        if (!await _sendGate.WaitAsync(SendGateWait, ct).ConfigureAwait(false))
        {
            Debug.WriteLine("[Loon] Send gate timed out — aborting socket");
            try { ws.Abort(); } catch { /* already torn down */ }
            return false;
        }
        try
        {
            // Re-check under the gate: the socket can close while queued behind another
            // sender.
            if (ws.State != WebSocketState.Open) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            await ws.SendAsync(data, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Send failed: {ex.Message}");
            try { ws.Abort(); } catch { /* already torn down */ }
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ── Thumbnail cache ──

    /// <summary>Longest edge handed to Discord; it only ever renders a small thumbnail.</summary>
    private const int MaxThumbnailDimension = 512;

    // Re-encoding a cover costs ~250-300ms of CPU (they run 25-30MB here), and it used to be
    // paid inside the request handler — with the relay and Discord both waiting on it. Cache
    // the last one and build it as soon as a URL is minted, which happens on the track change
    // a second or more before Discord's proxy actually fetches. One entry is enough: only the
    // playing album's art is ever asked for.
    private readonly object _thumbnailLock = new();
    private string? _thumbnailKey;
    private byte[]? _thumbnailBytes;

    /// <summary>Identity of the bytes on disk, so an edited cover invalidates the cache.</summary>
    private static string ThumbnailKey(FileInfo file)
        => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    /// <summary>
    /// Returns the cached JPEG thumbnail for <paramref name="filePath"/>, building it if the
    /// file is new or has changed. Null when the bytes cannot be read or decoded.
    /// </summary>
    private byte[]? GetOrBuildThumbnail(string filePath)
    {
        string key;
        try { key = ThumbnailKey(new FileInfo(filePath)); }
        catch { return null; }   // missing file, illegal characters, etc.

        lock (_thumbnailLock)
        {
            if (_thumbnailKey == key && _thumbnailBytes != null)
                return _thumbnailBytes;
        }

        byte[] raw;
        try { raw = File.ReadAllBytes(filePath); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Could not read artwork {filePath}: {ex.Message}");
            return null;
        }

        var thumbnail = MakeThumbnail(raw, MaxThumbnailDimension);
        if (thumbnail == null) return null;

        lock (_thumbnailLock)
        {
            _thumbnailKey = key;
            _thumbnailBytes = thumbnail;
        }
        return thumbnail;
    }

    // ── Image resizing ──

    /// <summary>
    /// Decodes <paramref name="data"/> and re-encodes it as a JPEG no larger than
    /// <paramref name="maxDimension"/> on either side. Returns null when the bytes cannot
    /// be decoded — callers must answer the relay with an empty response rather than fall
    /// back to the original, because the originals are routinely 10-14MB and streaming one
    /// up a home uplink is exactly what made the relay time Discord's fetch out.
    /// </summary>
    internal static byte[]? MakeThumbnail(byte[] data, int maxDimension)
    {
        if (data.Length == 0) return null;

        try
        {
            using var original = SKBitmap.Decode(data);
            if (original == null) return null;

            var w = original.Width;
            var h = original.Height;
            if (w <= 0 || h <= 0) return null;

            SKBitmap? scaled = null;
            try
            {
                var source = original;
                if (w > maxDimension || h > maxDimension)
                {
                    var scale = Math.Min((float)maxDimension / w, (float)maxDimension / h);
                    var info = new SKImageInfo(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
                    scaled = original.Resize(info, SKFilterQuality.Medium);
                    if (scaled == null) return null;
                    source = scaled;
                }

                using var image = SKImage.FromBitmap(source);
                using var encoded = image?.Encode(SKEncodedImageFormat.Jpeg, 85);
                return encoded?.ToArray();
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loon] Thumbnail failed: {ex.Message}");
            return null;
        }
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
