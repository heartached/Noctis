using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>Thrown by the import service when a TIDAL link needs a signed-in account first.</summary>
public sealed class TidalNotConnectedException : Exception
{
    public TidalNotConnectedException() : base("Sign in to TIDAL to import this link.") { }
}

/// <summary>Access + refresh tokens as returned by TIDAL's token endpoint.</summary>
public sealed record TidalTokens(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn);

/// <summary>
/// Pure pieces of TIDAL's Authorization Code + PKCE flow for a desktop app: the browser URL,
/// the loopback callback, the token-endpoint forms and their JSON. No secret is involved —
/// the flow is safe to ship with only the public client id. Network and the local listener
/// live in <see cref="TidalAuthService"/>.
/// </summary>
public static class TidalOAuth
{
    /// <summary>
    /// The app's TIDAL developer client id (developer.tidal.com → your application). Empty
    /// means TIDAL import is not available in this build; <c>NOCTIS_TIDAL_CLIENT_ID</c> in the
    /// environment overrides it for local testing.
    /// </summary>
    public const string BuiltInClientId = "3RGhYqYDtSytglPz";

    public static string ClientId
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NOCTIS_TIDAL_CLIENT_ID");
            return string.IsNullOrWhiteSpace(env) ? BuiltInClientId : env.Trim();
        }
    }

    /// <summary>False when no client id is compiled in or set — the UI falls back to the TuneMyMusic hint.</summary>
    public static bool IsConfigured => ClientId.Length > 0;

    public const int CallbackPort = 47474;
    public const string CallbackPath = "/callback";
    /// <summary>Must match the redirect URI registered on the TIDAL application, byte for byte.</summary>
    public const string RedirectUri = "http://127.0.0.1:47474/callback";
    public const string Scope = "playlists.read";
    public const string AuthorizeEndpoint = "https://login.tidal.com/authorize";
    public const string TokenEndpoint = "https://auth.tidal.com/v1/oauth2/token";

    /// <summary>A fresh high-entropy PKCE verifier (43–128 unreserved chars per RFC 7636).</summary>
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    /// <summary>A fresh opaque state value bound to one login attempt.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>S256 challenge: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string ComputeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string BuildAuthorizeUrl(string clientId, string challenge, string state)
        => $"{AuthorizeEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
           $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}" +
           $"&code_challenge_method=S256&code_challenge={Uri.EscapeDataString(challenge)}&state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// Reads the browser's redirect from the raw HTTP request line ("GET /callback?code=…&amp;state=… HTTP/1.1").
    /// Returns false when the request is for another path; <paramref name="error"/> carries
    /// TIDAL's <c>error</c> parameter (e.g. <c>access_denied</c>) when the user declined.
    /// </summary>
    public static bool TryParseCallback(string? requestLine, out string code, out string state, out string error)
    {
        code = state = error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestLine)) return false;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.Ordinal)) return false;
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        if (!path.Equals(CallbackPath, StringComparison.Ordinal)) return false;
        if (q < 0) return true;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            switch (key)
            {
                case "code": code = value; break;
                case "state": state = value; break;
                case "error": error = value; break;
            }
        }
        return true;
    }

    public static Dictionary<string, string> BuildCodeExchangeForm(string clientId, string code, string verifier) => new()
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = clientId,
        ["code"] = code,
        ["redirect_uri"] = RedirectUri,
        ["code_verifier"] = verifier,
    };

    public static Dictionary<string, string> BuildRefreshForm(string clientId, string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = clientId,
        ["refresh_token"] = refreshToken,
        ["scope"] = Scope,
    };

    /// <summary>Token response → tokens; null when there is no <c>access_token</c> (error bodies included).</summary>
    public static TidalTokens? ParseTokenResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                return null;
            var access = at.GetString();
            if (string.IsNullOrEmpty(access)) return null;
            var refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
            var expires = root.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.TryGetInt32(out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : TimeSpan.FromHours(1);
            return new TidalTokens(access, string.IsNullOrEmpty(refresh) ? null : refresh, expires);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>True when the token endpoint says the refresh token itself is dead (re-login needed).</summary>
    public static bool IsInvalidGrant(string? json)
        => json is not null && json.Contains("invalid_grant", StringComparison.Ordinal);

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The signed-in TIDAL account used for playlist import.</summary>
public interface ITidalAuthService
{
    /// <summary>A refresh token is stored; the next call can mint an access token without the browser.</summary>
    bool IsConnected { get; }

    /// <summary>A valid bearer token, refreshed if needed; null when not connected or the refresh was refused.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs the browser sign-in: opens TIDAL's login page, waits for the loopback redirect,
    /// exchanges the code and stores the refresh token. False when declined, timed out or failed.
    /// </summary>
    Task<bool> LoginAsync(CancellationToken ct = default);

    /// <summary>Forgets the stored tokens.</summary>
    void Disconnect();
}

/// <summary>
/// Desktop PKCE login against TIDAL plus token storage. The refresh token lives in its own
/// <c>tidal-auth.json</c> under the data directory (DPAPI-protected on Windows, like the
/// scrobbler credentials) so it never rides along in <c>settings.json</c> merges. The loopback
/// listener is a bare <see cref="TcpListener"/> on 127.0.0.1 — no URL ACL, no admin rights —
/// that serves exactly one redirect and then goes away.
/// </summary>
public sealed class TidalAuthService : ITidalAuthService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresUtc;

    public TidalAuthService(HttpClient http, string dataDirectory)
    {
        _http = http;
        _storePath = Path.Combine(dataDirectory, "tidal-auth.json");
        _refreshToken = Load();
    }

    public bool IsConnected => !string.IsNullOrEmpty(_refreshToken);

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (!TidalOAuth.IsConfigured) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiresUtc - TimeSpan.FromMinutes(1))
                return _accessToken;
            if (string.IsNullOrEmpty(_refreshToken)) return null;

            var (tokens, body) = await PostTokenAsync(TidalOAuth.BuildRefreshForm(TidalOAuth.ClientId, _refreshToken), ct).ConfigureAwait(false);
            if (tokens is null)
            {
                if (TidalOAuth.IsInvalidGrant(body)) ClearUnlocked();
                return null;
            }
            Store(tokens);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        if (!TidalOAuth.IsConfigured) return false;

        var verifier = TidalOAuth.CreateVerifier();
        var state = TidalOAuth.CreateState();
        var listener = new TcpListener(IPAddress.Loopback, TidalOAuth.CallbackPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Login", $"port {TidalOAuth.CallbackPort} unavailable: {ex.Message}");
            return false;
        }

        try
        {
            PlatformHelper.OpenUrl(TidalOAuth.BuildAuthorizeUrl(TidalOAuth.ClientId, TidalOAuth.ComputeChallenge(verifier), state));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoginTimeout);
            var code = await WaitForCodeAsync(listener, state, timeout.Token).ConfigureAwait(false);
            if (code is null) return false;

            var (tokens, _) = await PostTokenAsync(TidalOAuth.BuildCodeExchangeForm(TidalOAuth.ClientId, code, verifier), ct).ConfigureAwait(false);
            if (tokens is null) return false;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { Store(tokens); }
            finally { _gate.Release(); }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Login", ex.Message);
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { ClearUnlocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Serves loopback requests until the redirect for <paramref name="state"/> arrives.
    /// Anything else (favicon probes, a stale tab, a mismatched state) gets a 404 and the wait
    /// continues, so a stray request can't hijack the login. Returns the code, or null when
    /// the user declined or the wait was cancelled.
    /// </summary>
    private static async Task<string?> WaitForCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            try
            {
                var stream = client.GetStream();
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                var requestLine = await ReadLineAsync(stream, readCts.Token).ConfigureAwait(false);

                if (!TidalOAuth.TryParseCallback(requestLine, out var code, out var gotState, out var error))
                {
                    await RespondAsync(stream, 404, "Not found.", ct).ConfigureAwait(false);
                    continue;
                }
                if (error.Length > 0)
                {
                    await RespondAsync(stream, 200, "Sign-in was cancelled. You can close this tab and return to Noctis.", ct).ConfigureAwait(false);
                    return null;
                }
                if (code.Length == 0 || !string.Equals(gotState, state, StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 400, "This sign-in link doesn't match the one Noctis started. Try again from Noctis.", ct).ConfigureAwait(false);
                    continue;
                }
                await RespondAsync(stream, 200, "Signed in to TIDAL. You can close this tab and return to Noctis.", ct).ConfigureAwait(false);
                return code;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Slow or silent client: drop it and keep waiting for the real redirect.
            }
            catch (IOException) { }
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)buf[0]);
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Noctis</title></head>" +
                   "<body style=\"font-family:system-ui;background:#111;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
                   $"<p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<(TidalTokens? Tokens, string? Body)> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await _http.PostAsync(TidalOAuth.TokenEndpoint, content, ct).ConfigureAwait(false);
            var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Token", $"HTTP {(int)resp.StatusCode}");
                return (null, body);
            }
            return (TidalOAuth.ParseTokenResponse(body), body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Token", ex.Message);
            return (null, null);
        }
    }

    // ── storage (caller holds _gate) ──────────────────────────

    private void Store(TidalTokens tokens)
    {
        _accessToken = tokens.AccessToken;
        _accessExpiresUtc = DateTimeOffset.UtcNow + tokens.ExpiresIn;
        if (tokens.RefreshToken is not null && tokens.RefreshToken != _refreshToken)
        {
            _refreshToken = tokens.RefreshToken;
            Save(_refreshToken);
        }
    }

    private void ClearUnlocked()
    {
        _accessToken = null;
        _refreshToken = null;
        try { if (File.Exists(_storePath)) File.Delete(_storePath); }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Store", ex.Message); }
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_storePath));
            if (!doc.RootElement.TryGetProperty("refreshToken", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var raw = PersistenceService.UnprotectSecret(v.GetString() ?? string.Empty);
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
        catch (Exception ex)
        {
            DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Store", ex.Message);
            return null;
        }
    }

    private void Save(string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var json = JsonSerializer.Serialize(new { refreshToken = PersistenceService.ProtectSecret(refreshToken) });
            File.WriteAllText(_storePath, json);
        }
        catch (Exception ex) { DebugLogger.Error(DebugLogger.Category.Error, "Tidal.Store", ex.Message); }
    }
}
