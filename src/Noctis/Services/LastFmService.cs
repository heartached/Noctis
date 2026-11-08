using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

public class LastFmService : ILastFmService
{
    // Last.fm API credentials — register at https://www.last.fm/api/account/create
    private const string ApiKey = LastFmApi.ApiKey;
    private const string ApiSecret = LastFmApi.ApiSecret;
    private const string ApiBase = LastFmApi.ApiBase;
    private const string AuthBase = "https://www.last.fm/api/auth/";

    private readonly HttpClient _http;
    private string? _sessionKey;
    private string? _token;
    /// <summary>Album descriptions (album.getinfo + the JSON cache), shared with the phone;
    /// the description methods below forward to it unchanged.</summary>
    private readonly LastFmAlbumDescriptions _albumDescriptions;


    public bool IsAuthenticated => !string.IsNullOrEmpty(_sessionKey);
    public string? Username { get; private set; }

    public LastFmService(HttpClient http)
    {
        _http = http;
        _albumDescriptions = new LastFmAlbumDescriptions(http, Path.Combine(Helpers.AppPaths.DataRoot, "cache", "lastfm_album_descriptions.json"));
    }

    public void Configure(string? sessionKey)
    {
        _sessionKey = sessionKey;

        // If we have a session key, validate it by getting user info
        if (!string.IsNullOrEmpty(sessionKey))
        {
            SessionCheck = Task.Run(async () =>
            {
                try
                {
                    var (name, dead) = await GetAuthenticatedUsernameAsync();
                    if (dead)
                    {
                        // Last.fm itself says the key is revoked (error 9): forget it.
                        if (_sessionKey == sessionKey)
                        {
                            _sessionKey = null;
                            Username = null;
                            SessionExpired?.Invoke(this, EventArgs.Empty);
                        }
                        return;
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        Username = name;
                        UsernameResolved?.Invoke(this, name);
                    }
                }
                catch (Exception ex)
                {
                    // Offline at startup, a timeout, Last.fm down: the key is still good. This
                    // used to drop it on ANY failure, and the next settings save then wrote the
                    // empty key to settings.json (SettingsViewModel persists GetSessionKey()):
                    // one launch without network signed the user out of Last.fm for good, while
                    // the page kept reading "Connected" and scrobbles silently stopped.
                    DebugLog.Write("LastFm", $"Session check failed: {ex.GetType().Name}");
                }
            });
        }
    }

    /// <summary>The session check <see cref="Configure"/> started (tests await it).</summary>
    internal Task? SessionCheck { get; private set; }

    public async Task<string> GetAuthUrlAsync()
    {
        // Step 1: get a request token
        _token = null;
        try
        {
            _token = await GetTokenAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Failed to get token: {ex.Message}");
            return "";
        }

        if (string.IsNullOrEmpty(_token)) return "";

        return $"{AuthBase}?api_key={ApiKey}&token={_token}";
    }

    public async Task<bool> CompleteAuthAsync()
    {
        if (string.IsNullOrEmpty(_token)) return false;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "auth.getSession" },
                { "api_key", ApiKey },
                { "token", _token }
            };

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var url = BuildUrl(parameters);
            var response = await GetStringBoundedAsync(url);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("session", out var session))
            {
                _sessionKey = session.GetProperty("key").GetString();
                Username = session.GetProperty("name").GetString();
                _token = null;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Auth failed: {ex.Message}");
        }

        return false;
    }

    public string? GetSessionKey() => _sessionKey;

    public void Logout()
    {
        _sessionKey = null;
        Username = null;
        _token = null;
    }

    public async Task ScrobbleAsync(Track track, DateTime startedAt)
    {
        if (!IsAuthenticated || string.IsNullOrEmpty(track.Artist) || string.IsNullOrEmpty(track.Title))
            return;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "track.scrobble" },
                { "api_key", ApiKey },
                { "sk", _sessionKey! },
                { "artist", track.Artist },
                { "track", track.Title },
                { "timestamp", new DateTimeOffset(startedAt).ToUnixTimeSeconds().ToString() }
            };

            if (!string.IsNullOrWhiteSpace(track.Album))
                parameters["album"] = track.Album;

            if (track.Duration.TotalSeconds > 0)
                parameters["duration"] = ((int)track.Duration.TotalSeconds).ToString();

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var content = new FormUrlEncodedContent(parameters);
            using var response = await _http.PostAsync(ApiBase, content);

            // The response used to be discarded entirely, so an expired session key, a
            // 429, or being offline was a silent no-op: the play was lost forever while
            // the UI still read "Connected as <user>".
            if (!response.IsSuccessStatusCode)
            {
                var body = await HttpSafety.ReadStringBoundedAsync(response.Content);
                DebugLog.Write("LastFm",
                    $"Scrobble rejected ({(int)response.StatusCode}): {Truncate(body, 200)}");
                ScrobbleFailed?.Invoke(this,
                    $"Last.fm rejected the scrobble ({(int)response.StatusCode}).");
                return;
            }

            // Last.fm reports application-level errors with HTTP 200 and an "error" code.
            var okBody = await HttpSafety.ReadStringBoundedAsync(response.Content);
            if (okBody.Contains("\"error\"", StringComparison.Ordinal))
            {
                DebugLog.Write("LastFm", $"Scrobble error: {Truncate(okBody, 200)}");
                // Code 9 = invalid session key: the stored credential is dead.
                if (okBody.Contains("\"error\":9", StringComparison.Ordinal))
                {
                    _sessionKey = null;
                    Username = null;
                    SessionExpired?.Invoke(this, EventArgs.Empty);
                }
                ScrobbleFailed?.Invoke(this, "Last.fm rejected the scrobble.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] Scrobble failed: {ex.Message}");
            DebugLog.Write("LastFm", $"Scrobble failed: {ex.Message}");
            ScrobbleFailed?.Invoke(this, "Couldn't reach Last.fm.");
        }
    }

    public async Task<ScrobbleSendResult> SendScrobblesAsync(IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct = default)
    {
        var key = _sessionKey;
        if (string.IsNullOrEmpty(key)) return ScrobbleSendResult.SessionInvalid;
        var result = await new LastFmScrobbleClient(_http).ScrobbleAsync(key, plays, ct).ConfigureAwait(false);
        // SessionInvalid also covers Last.fm's errors 4 and 26 (token / API key), which say nothing
        // about this user's key, so the key is kept: the plays stay queued and the session check at
        // the next start forgets a key Last.fm really revoked (error 9).
        if (result != ScrobbleSendResult.Ok)
            DebugLog.Write("LastFm", $"Scrobble of {plays.Count} play(s): {result}");
        return result;
    }

    /// <summary>Raised when a scrobble could not be delivered. Carries a display message.</summary>
    public event EventHandler<string>? ScrobbleFailed;

    public event EventHandler<string>? UsernameResolved;

    /// <summary>Raised when Last.fm reports the stored session key is no longer valid.</summary>
    public event EventHandler? SessionExpired;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    public async Task UpdateNowPlayingAsync(Track track)
    {
        if (!IsAuthenticated || string.IsNullOrEmpty(track.Artist) || string.IsNullOrEmpty(track.Title))
            return;

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                { "method", "track.updateNowPlaying" },
                { "api_key", ApiKey },
                { "sk", _sessionKey! },
                { "artist", track.Artist },
                { "track", track.Title }
            };

            if (!string.IsNullOrWhiteSpace(track.Album))
                parameters["album"] = track.Album;

            if (track.Duration.TotalSeconds > 0)
                parameters["duration"] = ((int)track.Duration.TotalSeconds).ToString();

            var sig = GenerateSignature(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var content = new FormUrlEncodedContent(parameters);
            await _http.PostAsync(ApiBase, content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LastFm] NowPlaying update failed: {ex.Message}");
        }
    }

    public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return _albumDescriptions.GetAsync(artistName, albumName, preferFullText: false, ct);
    }

    public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return _albumDescriptions.GetAsync(artistName, albumName, preferFullText: true, ct);
    }

    public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
    {
        return _albumDescriptions.SetOverrideAsync(artistName, albumName, description, ct);
    }

    public Task ClearAlbumDescriptionsAsync() => _albumDescriptions.ClearAsync();

    public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
    {
        return SetAlbumDescriptionOverrideAsync(artistName, albumName, null, ct);
    }

    internal static string? CleanAlbumSummary(string? rawSummary) => LastFmAlbumDescriptions.CleanAlbumSummary(rawSummary);

    internal static string? CleanAlbumContent(string? rawContent) => LastFmAlbumDescriptions.CleanAlbumContent(rawContent);

    /// <summary>Repairs text cleaned by an older build (the orphan ". ." was cached on disk).</summary>
    internal static string? ScrubOrphanPeriods(string? text) => LastFmAlbumDescriptions.ScrubOrphanPeriods(text);

    private async Task<string?> GetTokenAsync()
    {
        var parameters = new SortedDictionary<string, string>
        {
            { "method", "auth.getToken" },
            { "api_key", ApiKey }
        };

        var sig = GenerateSignature(parameters);
        parameters["api_sig"] = sig;
        parameters["format"] = "json";

        var url = BuildUrl(parameters);
        var response = await GetStringBoundedAsync(url);
        using var doc = JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("token", out var token))
            return token.GetString();

        return null;
    }

    /// <summary>GET returning the body as a string with the shared byte cap applied.</summary>
    private async Task<string> GetStringBoundedAsync(string url)
    {
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(resp.Content);
    }

    /// <summary>The account name for the session key, and whether Last.fm rejected the key
    /// itself. Throws for network trouble and server errors (the key may still be good).</summary>
    private async Task<(string? Name, bool DeadSession)> GetAuthenticatedUsernameAsync()
    {
        var parameters = new SortedDictionary<string, string>
        {
            { "method", "user.getInfo" },
            { "api_key", ApiKey },
            { "sk", _sessionKey! }
        };

        var sig = GenerateSignature(parameters);
        parameters["api_sig"] = sig;
        parameters["format"] = "json";

        var url = BuildUrl(parameters);
        // Not GetStringBoundedAsync: Last.fm answers a revoked key with a 4xx AND an error
        // body, and only the body's code tells "revoked" from a server hiccup.
        using var resp = await _http.GetAsync(url);
        var response = await HttpSafety.ReadStringBoundedAsync(resp.Content);
        if (IsDeadSessionReply(response)) return (null, true);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(response);

        if (doc.RootElement.TryGetProperty("user", out var user) &&
            user.TryGetProperty("name", out var name))
            return (name.GetString(), false);

        return (null, false);
    }

    /// <summary>Last.fm error 9, "Invalid session key - Please re-authenticate".</summary>
    internal static bool IsDeadSessionReply(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error", out var err) &&
                   err.ValueKind == JsonValueKind.Number && err.TryGetInt32(out var code) && code == 9;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GenerateSignature(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kvp in parameters)
            sb.Append(kvp.Key).Append(kvp.Value);
        sb.Append(ApiSecret);

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = MD5.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static string BuildUrl(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder(ApiBase).Append('?');
        foreach (var kvp in parameters)
            sb.Append(Uri.EscapeDataString(kvp.Key)).Append('=').Append(Uri.EscapeDataString(kvp.Value)).Append('&');
        return sb.ToString().TrimEnd('&');
    }
}
