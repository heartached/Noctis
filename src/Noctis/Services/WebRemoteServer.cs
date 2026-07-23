using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Services;

/// <summary>
/// Minimal local-network web remote: a small hand-rolled HTTP server
/// (TcpListener, no admin URL-ACL needed, no extra dependencies) serving a
/// single mobile-friendly control page plus a tiny JSON API for
/// play/pause/next/prev/volume/seek and the current queue.
///
/// Security posture (LAN-only by design):
/// - Off by default; started only from the Settings toggle.
/// - Requests from non-private remote addresses are rejected outright.
/// - Every route requires the per-session access token (the "k" query value
///   baked into the URL shown in Settings). This blocks other LAN devices
///   that don't have the link, and CSRF/DNS-rebinding pages that can reach
///   the port but can't know the token. A new token is minted on each start.
/// - Only fixed routes are served; no filesystem paths are exposed.
/// </summary>
public sealed class WebRemoteServer : IDisposable
{
    private readonly PlayerViewModel _player;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    /// <summary>Per-session access token required on every request; regenerated on Start.</summary>
    public string Token { get; private set; } = string.Empty;

    public WebRemoteServer(PlayerViewModel player) => _player = player;

    public void Start(int port)
    {
        Stop();
        Port = port;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        DebugLogger.Info(DebugLogger.Category.State, "WebRemote.Start", $"port={port}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { /* shutdown is best-effort */ }
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>Best local LAN IPv4 for displaying the remote URL in Settings.</summary>
    public static string? GetLocalAddress()
    {
        try
        {
            // Connect-less UDP trick: routes without sending a packet.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("192.168.1.1", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True for loopback, RFC1918 private ranges, and link-local addresses.</summary>
    public static bool IsPrivateAddress(IPAddress? address)
    {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    // Bounds simultaneous request handlers so a LAN host can't exhaust
    // threads/sockets by holding connections open; excess connections are
    // dropped immediately. A phone remote uses 1-2 at a time.
    private const int MaxConcurrentClients = 16;
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                DebugLogger.Error(DebugLogger.Category.Error, "WebRemote.Accept", ex.Message);
                continue;
            }

            if (!_clientSlots.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = HandleClientAsync(client, ct)
                .ContinueWith(_ => _clientSlots.Release(), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (!IsPrivateAddress(remote))
                    return; // fail closed: drop non-LAN callers without a response

                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var stream = client.GetStream();

                var reader = new LineReader(stream);
                var requestLine = await reader.ReadLineAsync(ct);
                if (requestLine == null) return;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                var method = parts[0];
                var target = parts[1];

                // Drain headers (ignored; no bodies are accepted).
                while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }

                var (status, contentType, body) = await RouteAsync(method, target);
                var payload = Encoding.UTF8.GetBytes(body);
                var header =
                    $"HTTP/1.1 {status}\r\n" +
                    $"Content-Type: {contentType}; charset=utf-8\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "X-Content-Type-Options: nosniff\r\n" +
                    "Referrer-Policy: no-referrer\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                await stream.WriteAsync(payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugLogger.Error(DebugLogger.Category.Error, "WebRemote.Client", ex.Message);
            }
        }
    }

    /// <summary>
    /// Buffered line reader over the request stream — the previous implementation
    /// awaited one ReadAsync per byte (thousands of awaits per request). One reader
    /// per connection; over-read bytes stay buffered for the next line.
    /// </summary>
    private sealed class LineReader
    {
        private readonly NetworkStream _stream;
        private readonly byte[] _buf = new byte[4096];
        private int _len;
        private int _pos;

        public LineReader(NetworkStream stream) => _stream = stream;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (sb.Length < 4096)
            {
                if (_pos == _len)
                {
                    _len = await _stream.ReadAsync(_buf, ct);
                    _pos = 0;
                    if (_len == 0) return sb.Length > 0 ? sb.ToString() : null;
                }
                char c = (char)_buf[_pos++];
                if (c == '\n')
                    return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    private async Task<(string Status, string ContentType, string Body)> RouteAsync(string method, string target)
    {
        var path = target.Split('?')[0];
        var query = ParseQuery(target);

        // Every route — including the page itself — requires the session token,
        // so LAN devices without the link and cross-origin pages get nothing.
        if (!IsAuthorized(query))
            return ("403 Forbidden", "text/plain", "forbidden");

        switch (method, path)
        {
            case ("GET", "/"):
                return ("200 OK", "text/html", RemotePageHtml);

            case ("GET", "/api/status"):
                var status = await OnUiThread(BuildStatus);
                return ("200 OK", "application/json", status);

            case ("POST", "/api/playpause"):
                await OnUiThread(() => { _player.PlayPauseCommand.Execute(null); return ""; });
                return ("200 OK", "application/json", "{}");

            case ("POST", "/api/next"):
                await OnUiThread(() => { _player.NextCommand.Execute(null); return ""; });
                return ("200 OK", "application/json", "{}");

            case ("POST", "/api/prev"):
                await OnUiThread(() => { _player.PreviousCommand.Execute(null); return ""; });
                return ("200 OK", "application/json", "{}");

            case ("POST", "/api/volume") when query.TryGetValue("value", out var v)
                                              && int.TryParse(v, out var vol):
                await OnUiThread(() => { _player.Volume = Math.Clamp(vol, 0, 100); return ""; });
                return ("200 OK", "application/json", "{}");

            case ("POST", "/api/seek") when query.TryGetValue("fraction", out var f)
                                            && double.TryParse(f, System.Globalization.CultureInfo.InvariantCulture, out var fraction):
                await OnUiThread(() => { _player.SeekToPositionCommand.Execute(Math.Clamp(fraction, 0, 1)); return ""; });
                return ("200 OK", "application/json", "{}");

            default:
                return ("404 Not Found", "text/plain", "not found");
        }
    }

    /// <summary>Constant-time check of the "k" query value against the session token.</summary>
    private bool IsAuthorized(Dictionary<string, string> query) =>
        Token.Length > 0
        && query.TryGetValue("k", out var k)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(Token));

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qIndex = target.IndexOf('?');
        if (qIndex < 0) return result;
        foreach (var pair in target[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[WebUtility.UrlDecode(pair[..eq])] = WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return result;
    }

    private static Task<string> OnUiThread(Func<string> action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private string BuildStatus()
    {
        var track = _player.CurrentTrack;
        var queue = _player.UpNext.Take(20)
            .Select(t => new { title = t.Title, artist = t.Artist })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            title = track?.Title ?? "Nothing playing",
            artist = track?.Artist ?? "",
            album = track?.Album ?? "",
            isPlaying = _player.State == Models.PlaybackState.Playing,
            positionMs = (long)_player.Position.TotalMilliseconds,
            durationMs = (long)_player.Duration.TotalMilliseconds,
            volume = _player.Volume,
            queue,
        });
    }

    /// <summary>Single-file mobile control page (vanilla JS, polls /api/status).</summary>
    private const string RemotePageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no">
<title>Noctis Remote</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; font-family:system-ui,-apple-system,sans-serif; background:#0d1018; color:#fff;
         display:flex; flex-direction:column; align-items:center; min-height:100vh; }
  .wrap { width:100%; max-width:430px; padding:28px 22px; box-sizing:border-box; }
  h1 { font-size:14px; letter-spacing:3px; opacity:.5; text-align:center; margin:0 0 26px; }
  #title { font-size:22px; font-weight:700; text-align:center; margin:0; min-height:28px; }
  #artist { font-size:15px; opacity:.6; text-align:center; margin:6px 0 24px; min-height:18px; }
  .seekrow { display:flex; align-items:center; gap:10px; font-size:11px; opacity:.8; }
  input[type=range] { flex:1; accent-color:#7c8cff; }
  .controls { display:flex; justify-content:center; gap:18px; margin:26px 0; }
  button { background:#1b2030; color:#fff; border:0; border-radius:50%; width:64px; height:64px;
           font-size:22px; cursor:pointer; }
  button.play { width:80px; height:80px; background:#7c8cff; color:#0d1018; font-size:28px; }
  .volrow { display:flex; align-items:center; gap:12px; margin-bottom:28px; }
  .volrow span { font-size:18px; }
  h2 { font-size:12px; letter-spacing:2px; opacity:.5; margin:0 0 10px; }
  #queue { list-style:none; margin:0; padding:0; }
  #queue li { padding:9px 4px; border-bottom:1px solid #1c2233; font-size:14px; }
  #queue li small { display:block; opacity:.5; font-size:12px; }
</style>
</head>
<body>
<div class="wrap">
  <h1>NOCTIS REMOTE</h1>
  <p id="title">—</p>
  <p id="artist"></p>
  <div class="seekrow">
    <span id="pos">0:00</span>
    <input id="seek" type="range" min="0" max="1000" value="0">
    <span id="dur">0:00</span>
  </div>
  <div class="controls">
    <button onclick="post('prev')">&#9198;</button>
    <button class="play" id="playBtn" onclick="post('playpause')">&#9654;</button>
    <button onclick="post('next')">&#9197;</button>
  </div>
  <div class="volrow">
    <span>&#128264;</span>
    <input id="vol" type="range" min="0" max="100" value="50">
    <span>&#128266;</span>
  </div>
  <h2>UP NEXT</h2>
  <ul id="queue"></ul>
</div>
<script>
let seeking = false, voling = false;
const KEY = new URLSearchParams(location.search).get('k') || '';
function withKey(qs) { return (qs ? qs + '&' : '?') + 'k=' + encodeURIComponent(KEY); }
function fmt(ms) {
  const s = Math.floor(ms / 1000);
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
}
function post(action, qs) { fetch('/api/' + action + withKey(qs), { method: 'POST' }).then(refresh); }
function refresh() {
  fetch('/api/status' + withKey()).then(r => r.json()).then(s => {
    document.getElementById('title').textContent = s.title;
    document.getElementById('artist').textContent = s.artist;
    document.getElementById('playBtn').innerHTML = s.isPlaying ? '&#10074;&#10074;' : '&#9654;';
    document.getElementById('pos').textContent = fmt(s.positionMs);
    document.getElementById('dur').textContent = fmt(s.durationMs);
    if (!seeking && s.durationMs > 0)
      document.getElementById('seek').value = Math.round(1000 * s.positionMs / s.durationMs);
    if (!voling) document.getElementById('vol').value = s.volume;
    const q = document.getElementById('queue');
    q.innerHTML = '';
    for (const t of s.queue) {
      const li = document.createElement('li');
      li.textContent = t.title;
      const sm = document.createElement('small');
      sm.textContent = t.artist;
      li.appendChild(sm);
      q.appendChild(li);
    }
  }).catch(() => {});
}
const seek = document.getElementById('seek');
seek.addEventListener('pointerdown', () => seeking = true);
seek.addEventListener('change', () => { post('seek', '?fraction=' + (seek.value / 1000)); seeking = false; });
const vol = document.getElementById('vol');
vol.addEventListener('pointerdown', () => voling = true);
vol.addEventListener('change', () => { post('volume', '?value=' + vol.value); voling = false; });
setInterval(refresh, 2000);
refresh();
</script>
</body>
</html>
""";
}
