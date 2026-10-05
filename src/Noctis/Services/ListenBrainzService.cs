using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services.MediaServer;

namespace Noctis.Services;

/// <summary>
/// HTTP client for ListenBrainz scrobbling. Uses a single user token (no OAuth);
/// users generate it at https://listenbrainz.org/profile/ and paste it into
/// Settings. Submits "playing_now" at track start and a "single" listen once
/// playback hits Last.fm's classic ≥50%-or-≥4-minutes threshold.
/// The API URL is configurable (GitHub #118) so listens can go to self-hosted
/// ListenBrainz-compatible servers such as Koito or Maloja instead.
/// </summary>
public class ListenBrainzService : IListenBrainzService
{
    /// <summary>Official ListenBrainz API root; "/1/..." endpoint paths hang off it.</summary>
    public const string DefaultApiUrl = "https://api.listenbrainz.org";
    private const string SubmissionClient = "Noctis";

    private static readonly string SubmissionClientVersion =
        typeof(ListenBrainzService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ListenBrainzService).Assembly.GetName().Version?.ToString()
        ?? "1.0";

    private readonly HttpClient _http;
    private string? _userToken;

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_userToken);
    public string? Username { get; private set; }
    public string ApiUrl { get; private set; } = DefaultApiUrl;
    public ListenBrainzValidationError LastValidationError { get; private set; }

    public ListenBrainzService(HttpClient http)
    {
        _http = http;
    }

    public void Configure(string? userToken)
    {
        _userToken = string.IsNullOrWhiteSpace(userToken) ? null : userToken.Trim();
        // Username is resolved separately via ValidateTokenAsync; don't block here.
    }

    public void SetApiUrl(string? apiUrl)
    {
        ApiUrl = NormalizeApiUrl(apiUrl) ?? DefaultApiUrl;
    }

    /// <summary>
    /// Turns a user-typed API URL into the root the "/1/..." endpoint paths are appended to.
    /// Blank means the official server. Accepts the root with or without the "/1" version
    /// segment (Koito documents both ".../apis/listenbrainz" and ".../apis/listenbrainz/1")
    /// and a pasted full endpoint URL; drops the trailing slash, query and fragment. A
    /// missing scheme becomes http for LAN hosts and https otherwise; an explicit http is
    /// kept for any host since self-hosted scrobblers often run plain http. Returns null
    /// when the input isn't an http(s) URL.
    /// </summary>
    public static string? NormalizeApiUrl(string? input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return DefaultApiUrl;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            var scheme = Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var probe) && MediaServerUrl.IsPrivateHost(probe)
                ? "http://"
                : "https://";
            trimmed = scheme + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Host.Length == 0)
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var endpoint in new[] { "/submit-listens", "/validate-token" })
        {
            if (path.EndsWith(endpoint, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^endpoint.Length].TrimEnd('/');
                break;
            }
        }
        if (path.EndsWith("/1", StringComparison.Ordinal))
            path = path[..^2].TrimEnd('/');

        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    public async Task<string?> ValidateTokenAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
        {
            LastValidationError = ListenBrainzValidationError.InvalidToken;
            return null;
        }

        LastValidationError = ListenBrainzValidationError.None;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/1/validate-token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", _userToken);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LastValidationError = ListenBrainzValidationError.InvalidToken;
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                // 404/405 etc. = nothing ListenBrainz-shaped at this URL; 5xx = server trouble.
                LastValidationError = (int)resp.StatusCode >= 500
                    ? ListenBrainzValidationError.Unreachable
                    : ListenBrainzValidationError.NotCompatible;
                return null;
            }

            var body = await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct);
            using var doc = JsonDocument.Parse(body);

            // ListenBrainz returns: { "code": 200, "message": "...", "valid": true, "user_name": "..." }
            // A web page or other JSON (e.g. a URL missing Koito's "/apis/listenbrainz") has no "valid".
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("valid", out var validNode) ||
                validNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                LastValidationError = ListenBrainzValidationError.NotCompatible;
                return null;
            }
            if (!validNode.GetBoolean())
            {
                LastValidationError = ListenBrainzValidationError.InvalidToken;
                return null;
            }

            if (doc.RootElement.TryGetProperty("user_name", out var userNode))
                Username = userNode.GetString();

            return Username;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[ListenBrainz] validate-token returned non-JSON: {ex.Message}");
            LastValidationError = ListenBrainzValidationError.NotCompatible;
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListenBrainz] validate-token failed: {ex.Message}");
            LastValidationError = ListenBrainzValidationError.Unreachable;
            return null;
        }
    }

    public void Logout()
    {
        _userToken = null;
        Username = null;
    }

    public async Task UpdateNowPlayingAsync(Track track)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
            return;

        var payload = BuildPayload("playing_now", track, startedAtUnix: null);
        await PostListensAsync(payload, "playing_now");
    }

    public async Task ScrobbleAsync(Track track, DateTime startedAt)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
            return;

        var unix = new DateTimeOffset(startedAt.ToUniversalTime()).ToUnixTimeSeconds();
        var payload = BuildPayload("single", track, startedAtUnix: unix);
        await PostListensAsync(payload, "single");
    }

    private async Task PostListensAsync(string jsonBody, string kind)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/1/submit-listens")
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", _userToken);

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await HttpSafety.ReadStringBoundedAsync(resp.Content);
                Debug.WriteLine($"[ListenBrainz] submit-listens {kind} -> {(int)resp.StatusCode}: {Truncate(body, 200)}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListenBrainz] {kind} listen submission failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the JSON body documented at
    /// https://listenbrainz.readthedocs.io/en/latest/users/api/core.html#post--1-submit-listens
    /// </summary>
    private static string BuildPayload(string listenType, Track track, long? startedAtUnix)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("listen_type", listenType);
            writer.WritePropertyName("payload");
            writer.WriteStartArray();
            writer.WriteStartObject();
            if (startedAtUnix.HasValue)
                writer.WriteNumber("listened_at", startedAtUnix.Value);

            writer.WritePropertyName("track_metadata");
            writer.WriteStartObject();
            writer.WriteString("artist_name", track.Artist ?? string.Empty);
            writer.WriteString("track_name", track.Title ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(track.Album))
                writer.WriteString("release_name", track.Album);

            writer.WritePropertyName("additional_info");
            writer.WriteStartObject();
            if (track.Duration.TotalMilliseconds > 0)
                writer.WriteNumber("duration_ms", (long)track.Duration.TotalMilliseconds);
            writer.WriteString("submission_client", SubmissionClient);
            writer.WriteString("submission_client_version", SubmissionClientVersion);
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
