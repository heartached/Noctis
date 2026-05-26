using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// HTTP client for ListenBrainz scrobbling. Uses a single user token (no OAuth);
/// users generate it at https://listenbrainz.org/profile/ and paste it into
/// Settings. Submits "playing_now" at track start and a "single" listen once
/// playback hits Last.fm's classic ≥50%-or-≥4-minutes threshold.
/// </summary>
public class ListenBrainzService : IListenBrainzService
{
    private const string ApiBase = "https://api.listenbrainz.org/1";
    private const string SubmissionClient = "Noctis";

    private static readonly string SubmissionClientVersion =
        typeof(ListenBrainzService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ListenBrainzService).Assembly.GetName().Version?.ToString()
        ?? "1.0";

    private readonly HttpClient _http;
    private string? _userToken;

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_userToken);
    public string? Username { get; private set; }

    public ListenBrainzService(HttpClient http)
    {
        _http = http;
    }

    public void Configure(string? userToken)
    {
        _userToken = string.IsNullOrWhiteSpace(userToken) ? null : userToken.Trim();
        // Username is resolved separately via ValidateTokenAsync; don't block here.
    }

    public async Task<string?> ValidateTokenAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/validate-token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", _userToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            // ListenBrainz returns: { "code": 200, "message": "...", "valid": true, "user_name": "..." }
            if (!doc.RootElement.TryGetProperty("valid", out var validNode) || !validNode.GetBoolean())
                return null;

            if (doc.RootElement.TryGetProperty("user_name", out var userNode))
                Username = userNode.GetString();

            return Username;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListenBrainz] validate-token failed: {ex.Message}");
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
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/submit-listens")
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", _userToken);

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
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
